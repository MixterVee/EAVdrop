using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class SyncCoordinatorService
{
    private static readonly TimeSpan PlayingSyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PausedSyncInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CommandSettleDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AnchorPollDelay = TimeSpan.FromMilliseconds(250);
    private const long StableAnchorToleranceTicks = 0;
    private const int StableAnchorMaxPolls = 16;
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

    private long GetParticipantTarget(long hostPosition) =>
        Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);

    public SyncCoordinatorService(EmbyApiClient api, SettingsService settings)
    {
        _api = api;
        _settings = settings;
    }

    public async Task<SyncReadyResult> CheckReadyAsync(
        string hostSessionId,
        IEnumerable<string> participantSessionIds,
        string? itemId,
        bool requireHostPlaying,
        CancellationToken ct = default)
    {
        var participants = participantSessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, hostSessionId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(hostSessionId))
            return SyncReadyResult.NotReady("Choose a host device.");

        if (participants.Count == 0)
            return SyncReadyResult.NotReady("Choose at least one participant device.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        var sessionsTask = _api.GetSessionsAsync(timeout.Token);
        var controllableTask = _api.GetControllableSessionsAsync(timeout.Token);
        await Task.WhenAll(sessionsTask, controllableTask);

        var sessions = await sessionsTask;
        var controllableIds = (await controllableTask)
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .Select(s => s.Id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var host = sessions.FirstOrDefault(s =>
            string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase));

        if (host is null)
            return SyncReadyResult.NotReady("Host device is no longer online.");

        if (requireHostPlaying && !host.IsPlaying)
            return SyncReadyResult.NotReady("Host is idle. Start media first or use Start from Beginning.");

        var missing = participants
            .Where(id => !sessions.Any(s =>
                string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count > 0)
            return SyncReadyResult.NotReady(
                $"{missing.Count} selected participant device{(missing.Count == 1 ? " is" : "s are")} offline.");

        var targetIds = new[] { hostSessionId }.Concat(participants).ToList();
        var notControllable = targetIds
            .Where(id =>
            {
                var session = sessions.First(s =>
                    string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                return !controllableIds.Contains(id) && !session.SupportsRemoteControl;
            })
            .ToList();

        if (notControllable.Count > 0)
            return SyncReadyResult.NotReady(
                $"{notControllable.Count} selected device{(notControllable.Count == 1 ? " is" : "s are")} not reporting remote-control readiness.");

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            try
            {
                await _api.GetSyncMediaItemAsync(itemId, timeout.Token);
            }
            catch (Exception ex)
            {
                return SyncReadyResult.NotReady($"Selected media is not currently available. {ex.Message}");
            }
        }

        var deviceCount = participants.Count + 1;
        return SyncReadyResult.Ready(
            $"Ready • {deviceCount} devices • {_settings.SyncParticipantLeadMilliseconds} ms lead");
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
            var participantTarget = GetParticipantTarget(hostPosition);

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

    public async Task StartFromBeginningAsync(
        string hostSessionId,
        IEnumerable<string> participantSessionIds,
        string itemId,
        CancellationToken ct = default)
    {
        var participants = participantSessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, hostSessionId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(hostSessionId))
            throw new InvalidOperationException("Choose a host device first.");

        if (participants.Count == 0)
            throw new InvalidOperationException("Choose at least one participant device.");

        if (string.IsNullOrWhiteSpace(itemId))
            throw new InvalidOperationException("Choose a movie or episode first.");

        Stop();
        _hostSessionId = hostSessionId;
        _participantSessionIds = participants;

        await _precisionGate.WaitAsync(ct);
        _precisionOperationInProgress = true;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));

            var sessions = await _api.GetSessionsAsync(timeout.Token);
            var allTargetIds = new List<string> { hostSessionId };
            allTargetIds.AddRange(participants);

            var availableIds = allTargetIds
                .Where(id => sessions.Any(s =>
                    string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (!availableIds.Contains(hostSessionId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected host session is no longer online.");

            if (availableIds.Count < 2)
                throw new InvalidOperationException("No selected participant session is currently available.");

            SetStatus("Start from Beginning • opening media on all devices…");

            var launchTasks = new List<Task>
            {
                _api.PlayOnSessionAsync(hostSessionId, itemId, 0, timeout.Token)
            };
            launchTasks.AddRange(
                availableIds
                    .Where(id => !string.Equals(id, hostSessionId, StringComparison.OrdinalIgnoreCase))
                    .Select(id => _api.PlayOnSessionAsync(
                        id,
                        itemId,
                        ParticipantPlaybackLeadTicks,
                        timeout.Token)));

            await Task.WhenAll(launchTasks);

            SetStatus("Start from Beginning • waiting for players to load…");
            await WaitForItemOnSessionsAsync(availableIds, itemId, timeout.Token);

            SetStatus("Start from Beginning • locking the starting frame…");
            await Task.WhenAll(availableIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Pause", null, timeout.Token)));

            await Task.Delay(CommandSettleDelay, timeout.Token);

            // Re-assert exact starting positions after every player has loaded and
            // stopped. This avoids using startup/load time as part of the sync.
            var seekTasks = new List<Task>
            {
                _api.SendPlayStateCommandAsync(hostSessionId, "Seek", 0, timeout.Token)
            };
            seekTasks.AddRange(
                availableIds
                    .Where(id => !string.Equals(id, hostSessionId, StringComparison.OrdinalIgnoreCase))
                    .Select(id => _api.SendPlayStateCommandAsync(
                        id,
                        "Seek",
                        ParticipantPlaybackLeadTicks,
                        timeout.Token)));

            await Task.WhenAll(seekTasks);
            await Task.Delay(CommandSettleDelay, timeout.Token);

            await Task.WhenAll(availableIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Pause", null, timeout.Token)));

            // Wait until the host reports the exact same paused position twice.
            // We intentionally ignore its value here: the explicit Seek(0) above
            // defines our start anchor, while this confirms the player settled.
            await WaitForStablePausedHostAsync(hostSessionId, itemId, timeout.Token);

            SetStatus($"Start from Beginning • releasing together • {_settings.SyncParticipantLeadMilliseconds} ms lead…");

            var resumeTasks = availableIds.Select(id =>
                _api.SendPlayStateCommandAsync(id, "Unpause", null, timeout.Token));
            await Task.WhenAll(resumeTasks);

            await Task.Delay(CommandSettleDelay, timeout.Token);
            var finalSessions = await _api.GetSessionsAsync(timeout.Token);
            var finalHost = finalSessions.FirstOrDefault(s =>
                string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase));

            if (finalHost is not null)
                RememberHostState(finalHost);
            else
                ResetSyncState();

            _syncCts = new CancellationTokenSource();
            SetStatus($"Sync'EM up active • started from beginning • {_settings.SyncParticipantLeadMilliseconds} ms lead");
            _ = RunLoopAsync(_syncCts.Token);
        }
        catch
        {
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
            var participantTarget = GetParticipantTarget(hostPosition);

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
                        ? $"Host seek detected • Precision Re-align at {_settings.SyncParticipantLeadMilliseconds} ms…"
                        : $"Host paused • Precision Re-align at {_settings.SyncParticipantLeadMilliseconds} ms…");
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

    private async Task<IReadOnlyCollection<SessionInfoDto>> WaitForItemOnSessionsAsync(
        IReadOnlyCollection<string> sessionIds,
        string itemId,
        CancellationToken ct)
    {
        const int maxPolls = 40;

        for (var attempt = 0; attempt < maxPolls; attempt++)
        {
            await Task.Delay(AnchorPollDelay, ct);
            var sessions = await _api.GetSessionsAsync(ct);

            var allLoaded = sessionIds.All(id =>
            {
                var session = sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

                return session is not null &&
                    string.Equals(session.NowPlayingItem?.Id, itemId, StringComparison.OrdinalIgnoreCase);
            });

            if (allLoaded)
                return sessions;
        }

        throw new InvalidOperationException(
            "One or more devices did not load the selected media in time.");
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

public sealed record SyncReadyResult(bool IsReady, string Summary)
{
    public static SyncReadyResult Ready(string summary) => new(true, summary);
    public static SyncReadyResult NotReady(string summary) => new(false, summary);
}
