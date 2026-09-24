using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class SyncCoordinatorService
{
    private static readonly TimeSpan PlayingSyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PausedSyncInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CommandSettleDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AnchorPollDelay = TimeSpan.FromMilliseconds(250);
    private const long StableAnchorToleranceTicks = TimeSpan.TicksPerMillisecond * 100;
    private const int StableAnchorMaxPolls = 12;
    private const long HostSeekDetectionTicks = TimeSpan.TicksPerSecond * 6;

    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _precisionGate = new(1, 1);

    private CancellationTokenSource? _syncCts;
    private string _hostSessionId = "";
    private HashSet<string> _participantSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _precisionOperationInProgress;

    private string? _lastHostItemId;
    private long? _lastHostPositionTicks;
    private bool? _lastHostPaused;
    private DateTimeOffset? _lastHostObservedAt;

    public bool IsRunning => _syncCts is not null && !_syncCts.IsCancellationRequested;
    public string Status { get; private set; } = "Not syncing";

    public event EventHandler<string>? StatusChanged;

    private long ParticipantPlaybackLeadTicks =>
        TimeSpan.TicksPerMillisecond * _settings.SyncParticipantLeadMilliseconds;

    public SyncCoordinatorService(EmbyApiClient api, SettingsService settings)
    {
        _api = api;
        _settings = settings;
    }

    public async Task RealignNowAsync(CancellationToken ct = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("Start Sync'EM up first.");

        if (!await _precisionGate.WaitAsync(0, ct))
        {
            SetStatus("Precision Re-align already in progress");
            return;
        }

        _precisionOperationInProgress = true;
        var hostWasPaused = true;
        var activeParticipantIds = new List<string>();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var sessions = await _api.GetSessionsAsync(timeout.Token);
            var host = sessions.FirstOrDefault(s =>
                string.Equals(s.Id, _hostSessionId, StringComparison.OrdinalIgnoreCase));

            if (host?.NowPlayingItem?.Id is not { Length: > 0 } itemId)
                throw new InvalidOperationException("The host is no longer reporting active playback.");

            hostWasPaused = host.PlayState?.IsPaused == true;
            activeParticipantIds = GetAvailableParticipantIds(sessions, itemId);

            if (activeParticipantIds.Count == 0)
                throw new InvalidOperationException("No selected participant is currently playing the host item.");

            SetStatus("Precision Re-align • pausing devices…");

            var pauseTasks = new List<Task>
            {
                _api.SendPlayStateCommandAsync(_hostSessionId, "Pause", null, timeout.Token)
            };
            pauseTasks.AddRange(activeParticipantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Pause", null, timeout.Token)));
            await Task.WhenAll(pauseTasks);

            SetStatus("Precision Re-align • locking stable host position…");
            var anchor = await WaitForStablePausedHostAsync(
                _hostSessionId,
                itemId,
                timeout.Token);

            var hostPosition = anchor.Host.PlayState?.PositionTicks ?? 0;
            // Establish the selected lead while everything is stationary. This
            // means a later resume only needs Unpause; it must not seek again.
            var participantTarget = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);

            SetStatus($"Precision Re-align • setting {_settings.SyncParticipantLeadMilliseconds} ms lead…");

            await Task.WhenAll(activeParticipantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Seek", participantTarget, timeout.Token)));

            // Some Emby clients briefly resume after a remote seek. Give the seek
            // time to land, then force participants back to the paused anchor.
            await Task.Delay(CommandSettleDelay, timeout.Token);
            await Task.WhenAll(activeParticipantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Pause", null, timeout.Token)));

            if (!hostWasPaused)
            {
                await Task.Delay(CommandSettleDelay, timeout.Token);
                SetStatus("Precision Re-align • resuming together…");

                var resumeTasks = new List<Task>
                {
                    _api.SendPlayStateCommandAsync(_hostSessionId, "Unpause", null, timeout.Token)
                };
                resumeTasks.AddRange(activeParticipantIds.Select(id =>
                    _api.SendPlayStateCommandAsync(id, "Unpause", null, timeout.Token)));
                await Task.WhenAll(resumeTasks);
            }

            await Task.Delay(CommandSettleDelay, timeout.Token);
            var finalSessions = await _api.GetSessionsAsync(timeout.Token);
            var finalHost = finalSessions.FirstOrDefault(s =>
                string.Equals(s.Id, _hostSessionId, StringComparison.OrdinalIgnoreCase));
            if (finalHost is not null)
                RememberHostState(finalHost);

            SetStatus(hostWasPaused
                ? $"Precision Re-align complete • paused at anchor • {_settings.SyncParticipantLeadMilliseconds} ms lead set"
                : $"Precision Re-align complete • {_settings.SyncParticipantLeadMilliseconds} ms participant lead");
        }
        catch
        {
            if (!hostWasPaused)
                await BestEffortResumeAsync(_hostSessionId, activeParticipantIds);
            throw;
        }
        finally
        {
            _precisionOperationInProgress = false;
            _precisionGate.Release();
        }
    }

    public async Task StartAsync(
        string hostSessionId,
        IEnumerable<string> participantSessionIds,
        CancellationToken ct = default)
    {
        var participants = participantSessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, hostSessionId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(hostSessionId))
            throw new InvalidOperationException("Choose a currently playing host device first.");

        if (participants.Count == 0)
            throw new InvalidOperationException("Choose at least one participant device.");

        Stop();

        _hostSessionId = hostSessionId;
        _participantSessionIds = participants;

        await _precisionGate.WaitAsync(ct);
        _precisionOperationInProgress = true;
        var hostWasPaused = true;
        var startedParticipantIds = new List<string>();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var sessions = await _api.GetSessionsAsync(timeout.Token);
            var host = sessions.FirstOrDefault(s =>
                string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected host session is no longer online.");

            if (host.NowPlayingItem?.Id is not { Length: > 0 } itemId)
                throw new InvalidOperationException("The host must already be playing a movie or episode.");

            hostWasPaused = host.PlayState?.IsPaused == true;

            SetStatus("Start Together • pausing host to create a stable anchor…");
            await _api.SendPlayStateCommandAsync(hostSessionId, "Pause", null, timeout.Token);

            var anchor = await WaitForStablePausedHostAsync(
                hostSessionId,
                itemId,
                timeout.Token);

            var hostPosition = anchor.Host.PlayState?.PositionTicks ?? 0;
            // Establish the selected lead while everything is stationary. This
            // means a later resume only needs Unpause; it must not seek again.
            var participantTarget = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);

            foreach (var participantId in participants)
            {
                var participant = sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, participantId, StringComparison.OrdinalIgnoreCase));

                if (participant is null)
                    continue;

                startedParticipantIds.Add(participantId);
            }

            if (startedParticipantIds.Count == 0)
                throw new InvalidOperationException("None of the selected participant sessions is currently available.");

            SetStatus("Start Together • loading participants at the anchor…");
            await Task.WhenAll(startedParticipantIds.Select(id =>
                _api.PlayOnSessionAsync(id, itemId, participantTarget, timeout.Token)));

            await Task.Delay(CommandSettleDelay, timeout.Token);
            await Task.WhenAll(startedParticipantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Pause", null, timeout.Token)));

            // PlayNow can advance briefly before the pause reaches the client.
            // Re-seek while everything is stopped so every participant shares the
            // same deterministic anchor before playback resumes.
            await Task.Delay(CommandSettleDelay, timeout.Token);
            await Task.WhenAll(startedParticipantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Seek", participantTarget, timeout.Token)));

            await Task.Delay(CommandSettleDelay, timeout.Token);
            await Task.WhenAll(startedParticipantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Pause", null, timeout.Token)));

            if (!hostWasPaused)
            {
                SetStatus("Start Together • resuming all devices together…");

                var resumeTasks = new List<Task>
                {
                    _api.SendPlayStateCommandAsync(hostSessionId, "Unpause", null, timeout.Token)
                };
                resumeTasks.AddRange(startedParticipantIds.Select(id =>
                    _api.SendPlayStateCommandAsync(id, "Unpause", null, timeout.Token)));
                await Task.WhenAll(resumeTasks);
            }

            await Task.Delay(CommandSettleDelay, timeout.Token);
            var finalSessions = await _api.GetSessionsAsync(timeout.Token);
            var finalHost = finalSessions.FirstOrDefault(s =>
                string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase));
            if (finalHost is not null)
                RememberHostState(finalHost);
            else
                RememberHostState(anchor.Host);

            _syncCts = new CancellationTokenSource();
            SetStatus($"Sync'EM up active • stable-anchor start • {participants.Count} participant{(participants.Count == 1 ? "" : "s")}");
            _ = RunLoopAsync(_syncCts.Token);
        }
        catch
        {
            if (!hostWasPaused)
                await BestEffortResumeAsync(hostSessionId, startedParticipantIds);

            _hostSessionId = "";
            _participantSessionIds.Clear();
            ResetSyncState();
            throw;
        }
        finally
        {
            _precisionOperationInProgress = false;
            _precisionGate.Release();
        }
    }

    public void Stop()
    {
        if (_syncCts is not null)
        {
            _syncCts.Cancel();
            _syncCts.Dispose();
            _syncCts = null;
        }

        _hostSessionId = "";
        _participantSessionIds.Clear();
        ResetSyncState();
        SetStatus("Not syncing");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = _lastHostPaused == true ? PausedSyncInterval : PlayingSyncInterval;
                await Task.Delay(delay, ct);

                if (_precisionOperationInProgress)
                    continue;

                var sessions = await _api.GetSessionsAsync(ct);

                if (_precisionOperationInProgress)
                    continue;

                var host = sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, _hostSessionId, StringComparison.OrdinalIgnoreCase));

                if (host is null || host.NowPlayingItem?.Id is not { Length: > 0 })
                {
                    SetStatus("Sync stopped • host session ended or stopped playback");
                    StopFromLoop();
                    return;
                }

                var hostSeeked = DidHostSeek(host);
                var hostPaused = host.PlayState?.IsPaused == true;
                var hostPauseStateChanged =
                    _lastHostPaused.HasValue &&
                    _lastHostPaused.Value != hostPaused;

                // Timeline seeks and transitions into Pause get the exact same
                // stable-anchor treatment as the manual Precision Re-align button.
                // Steady playback remains untouched.
                if (hostSeeked || (hostPauseStateChanged && hostPaused))
                {
                    SetStatus(hostSeeked
                        ? "Host seek detected • running Precision Re-align…"
                        : "Host paused • running Precision Re-align…");
                    await RealignNowAsync(ct);
                    continue;
                }

                await MirrorHostEventAsync(
                    host,
                    sessions,
                    hostPauseStateChanged,
                    ct);

                RememberHostState(host);

                var stateText = hostPaused ? "paused" : "steady";
                SetStatus($"Sync'EM up active • {host.MediaDisplay} • {stateText} • precision align on seek/pause");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Sync paused by error • {ex.Message}");
            StopFromLoop();
        }
    }

    private async Task MirrorHostEventAsync(
        SessionInfoDto host,
        IReadOnlyCollection<SessionInfoDto> sessions,
        bool hostPauseStateChanged,
        CancellationToken ct)
    {
        var itemId = host.NowPlayingItem?.Id;
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var hostPaused = host.PlayState?.IsPaused == true;

        foreach (var participantId in _participantSessionIds)
        {
            var participant = sessions.FirstOrDefault(s =>
                string.Equals(s.Id, participantId, StringComparison.OrdinalIgnoreCase));

            if (participant is null ||
                !string.Equals(participant.NowPlayingItem?.Id, itemId, StringComparison.OrdinalIgnoreCase))
                continue;

            var participantPaused = participant.PlayState?.IsPaused == true;

            if (hostPaused)
            {
                if (!participantPaused || hostPauseStateChanged)
                    await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);

                continue;
            }

            if (participantPaused)
            {
                // Precision alignment already set the participant lead at a stable
                // paused anchor. Resume without a live seek so we do not disturb it.
                await _api.SendPlayStateCommandAsync(participantId, "Unpause", null, ct);
            }
        }
    }

    private async Task<(SessionInfoDto Host, IReadOnlyCollection<SessionInfoDto> Sessions)>
        WaitForStablePausedHostAsync(
            string hostSessionId,
            string itemId,
            CancellationToken ct)
    {
        long? previousPosition = null;

        for (var attempt = 0; attempt < StableAnchorMaxPolls; attempt++)
        {
            await Task.Delay(AnchorPollDelay, ct);
            var sessions = await _api.GetSessionsAsync(ct);
            var host = sessions.FirstOrDefault(s =>
                string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase));

            if (host is null ||
                !string.Equals(host.NowPlayingItem?.Id, itemId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The host stopped or changed media while creating the sync anchor.");

            if (host.PlayState?.IsPaused == true)
            {
                var position = host.PlayState?.PositionTicks ?? 0;

                if (previousPosition.HasValue &&
                    Math.Abs(position - previousPosition.Value) <= StableAnchorToleranceTicks)
                    return (host, sessions);

                previousPosition = position;
            }
            else
            {
                previousPosition = null;
            }
        }

        throw new InvalidOperationException(
            "The host did not settle into a stable paused position. Precision sync requires a remotely controllable host.");
    }

    private List<string> GetAvailableParticipantIds(
        IReadOnlyCollection<SessionInfoDto> sessions,
        string itemId) =>
        _participantSessionIds
            .Where(id =>
            {
                var participant = sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

                return participant is not null &&
                    participant.PlayState?.CanSeek != false &&
                    string.Equals(participant.NowPlayingItem?.Id, itemId, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

    private async Task BestEffortResumeAsync(string hostSessionId, IEnumerable<string> participantIds)
    {
        try
        {
            var tasks = new List<Task>
            {
                _api.SendPlayStateCommandAsync(hostSessionId, "Unpause")
            };
            tasks.AddRange(participantIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Unpause")));
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Recovery only: preserve the original failure.
        }
    }

    private bool DidHostSeek(SessionInfoDto host)
    {
        var itemId = host.NowPlayingItem?.Id;
        var position = host.PlayState?.PositionTicks ?? 0;
        var now = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(itemId) ||
            !_lastHostPositionTicks.HasValue ||
            !_lastHostObservedAt.HasValue ||
            !string.Equals(itemId, _lastHostItemId, StringComparison.OrdinalIgnoreCase))
            return false;

        var elapsedTicks = Math.Max(0L, (now - _lastHostObservedAt.Value).Ticks);
        var expectedAdvance = _lastHostPaused == true ? 0L : elapsedTicks;
        var actualAdvance = position - _lastHostPositionTicks.Value;
        var unexpectedChange = Math.Abs(actualAdvance - expectedAdvance);

        return unexpectedChange >= HostSeekDetectionTicks;
    }

    private void RememberHostState(SessionInfoDto host)
    {
        _lastHostItemId = host.NowPlayingItem?.Id;
        _lastHostPositionTicks = host.PlayState?.PositionTicks ?? 0;
        _lastHostPaused = host.PlayState?.IsPaused == true;
        _lastHostObservedAt = DateTimeOffset.UtcNow;
    }

    private void ResetSyncState()
    {
        _lastHostItemId = null;
        _lastHostPositionTicks = null;
        _lastHostPaused = null;
        _lastHostObservedAt = null;
    }

    private void StopFromLoop()
    {
        var cts = _syncCts;
        _syncCts = null;

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _hostSessionId = "";
        _participantSessionIds.Clear();
        ResetSyncState();
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
