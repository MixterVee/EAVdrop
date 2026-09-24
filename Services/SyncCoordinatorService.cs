using System.Collections.Concurrent;
using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class SyncCoordinatorService
{
    private static readonly TimeSpan PlayingSyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PausedSyncInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CorrectionCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandSettleDelay = TimeSpan.FromMilliseconds(350);

    // Keep normal playback calm. A participant must be more than one second away
    // from the selected lead target for two consecutive polls before auto-correction.
    private const long PlayingTargetToleranceTicks = TimeSpan.TicksPerSecond;
    private const int RequiredConsecutiveDriftSamples = 2;
    private const long PausedDriftToleranceTicks = TimeSpan.TicksPerSecond;
    private const long HostSeekDetectionTicks = TimeSpan.TicksPerSecond * 6;

    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _syncCts;
    private string _hostSessionId = "";
    private HashSet<string> _participantSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCorrectionAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _driftSamples = new(StringComparer.OrdinalIgnoreCase);

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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var sessions = await _api.GetSessionsAsync(timeout.Token);
        var host = sessions.FirstOrDefault(s =>
            string.Equals(s.Id, _hostSessionId, StringComparison.OrdinalIgnoreCase));

        if (host?.NowPlayingItem?.Id is not { Length: > 0 } itemId)
            throw new InvalidOperationException("The host is no longer reporting active playback.");

        var hostPosition = host.PlayState?.PositionTicks ?? 0;
        var hostPaused = host.PlayState?.IsPaused == true;
        var corrected = 0;

        foreach (var participantId in _participantSessionIds)
        {
            var participant = sessions.FirstOrDefault(s =>
                string.Equals(s.Id, participantId, StringComparison.OrdinalIgnoreCase));

            if (participant is null ||
                participant.PlayState?.CanSeek == false ||
                !string.Equals(participant.NowPlayingItem?.Id, itemId, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = hostPaused
                ? hostPosition
                : Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);

            await _api.SendPlayStateCommandAsync(participantId, "Seek", target, timeout.Token);

            if (hostPaused)
            {
                await Task.Delay(CommandSettleDelay, timeout.Token);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, timeout.Token);
            }

            MarkCorrected(participantId);
            ResetDrift(participantId);
            corrected++;
        }

        if (corrected == 0)
            throw new InvalidOperationException("No selected participant is currently available for re-alignment.");

        SetStatus($"Re-aligned • {_settings.SyncParticipantLeadMilliseconds} ms participant lead");
    }

    public async Task StartAsync(string hostSessionId, IEnumerable<string> participantSessionIds, CancellationToken ct = default)
    {
        var participants = participantSessionIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, hostSessionId, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(hostSessionId))
            throw new InvalidOperationException("Choose a host session first.");

        if (participants.Count == 0)
            throw new InvalidOperationException("Choose at least one participant device.");

        Stop();

        _hostSessionId = hostSessionId;
        _participantSessionIds = participants;

        using var initialTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        initialTimeout.CancelAfter(TimeSpan.FromSeconds(30));

        var sessions = await _api.GetSessionsAsync(initialTimeout.Token);
        var host = sessions.FirstOrDefault(s => string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected host session is no longer online.");

        if (host.NowPlayingItem?.Id is not { Length: > 0 })
            throw new InvalidOperationException("The host must already be playing a movie or episode.");

        await SyncParticipantsToHostAsync(
            host,
            sessions,
            forcePlay: true,
            hostSeeked: true,
            hostPauseStateChanged: true,
            initialTimeout.Token);

        RememberHostState(host);

        _syncCts = new CancellationTokenSource();
        SetStatus($"Sync'EM up active • {host.DeviceDisplay} is host • {participants.Count} participant{(participants.Count == 1 ? "" : "s")}");
        _ = RunLoopAsync(_syncCts.Token);
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

                var sessions = await _api.GetSessionsAsync(ct);
                var host = sessions.FirstOrDefault(s => string.Equals(s.Id, _hostSessionId, StringComparison.OrdinalIgnoreCase));

                if (host is null || host.NowPlayingItem?.Id is not { Length: > 0 })
                {
                    SetStatus("Sync stopped • host session ended or stopped playback");
                    StopFromLoop();
                    return;
                }

                var hostSeeked = DidHostSeek(host);
                var hostPauseStateChanged =
                    _lastHostPaused.HasValue &&
                    _lastHostPaused.Value != (host.PlayState?.IsPaused == true);

                await SyncParticipantsToHostAsync(
                    host,
                    sessions,
                    forcePlay: false,
                    hostSeeked,
                    hostPauseStateChanged,
                    ct);

                RememberHostState(host);

                var stateText = host.PlayState?.IsPaused == true ? "paused" : "steady";
                SetStatus($"Sync'EM up active • {host.MediaDisplay} • {stateText}");
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

    private async Task SyncParticipantsToHostAsync(
        SessionInfoDto host,
        IReadOnlyCollection<SessionInfoDto> sessions,
        bool forcePlay,
        bool hostSeeked,
        bool hostPauseStateChanged,
        CancellationToken ct)
    {
        var itemId = host.NowPlayingItem?.Id;
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var hostPosition = host.PlayState?.PositionTicks ?? 0;
        var hostPaused = host.PlayState?.IsPaused == true;
        var tasks = new List<Task>();

        foreach (var participantId in _participantSessionIds)
        {
            var participant = sessions.FirstOrDefault(s =>
                string.Equals(s.Id, participantId, StringComparison.OrdinalIgnoreCase));

            if (participant is null)
                continue;

            tasks.Add(SyncParticipantAsync(
                participant,
                itemId,
                hostPosition,
                hostPaused,
                forcePlay,
                hostSeeked,
                hostPauseStateChanged,
                ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SyncParticipantAsync(
        SessionInfoDto participant,
        string hostItemId,
        long hostPosition,
        bool hostPaused,
        bool forcePlay,
        bool hostSeeked,
        bool hostPauseStateChanged,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(participant.Id))
            return;

        var participantId = participant.Id;
        var sameItem = string.Equals(
            participant.NowPlayingItem?.Id,
            hostItemId,
            StringComparison.OrdinalIgnoreCase);

        if (forcePlay)
        {
            var initialPosition = hostPaused
                ? hostPosition
                : Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);
            await _api.PlayOnSessionAsync(participantId, hostItemId, initialPosition, ct);
            MarkCorrected(participantId);
            ResetDrift(participantId);

            if (hostPaused)
            {
                // Give the client a moment to create the playback session, then
                // explicitly leave it paused at the host position.
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
            }

            return;
        }

        // After the initial join, never auto-launch media again. Some Emby clients
        // briefly stop reporting NowPlayingItem during a long pause. Treat that as
        // a temporary reporting gap instead of sending PlayNow and restarting the item.
        if (!sameItem)
        {
            ResetDrift(participantId);
            return;
        }

        var participantPaused = participant.PlayState?.IsPaused == true;
        var participantPosition = participant.PlayState?.PositionTicks ?? 0;
        var pausedDrift = Math.Abs(participantPosition - hostPosition);

        if (hostPaused)
        {
            // Pause first. If the host also moved while paused, seek only after
            // pause has had time to settle, then issue Pause again because some
            // Emby clients briefly resume after a remote seek.
            if (!participantPaused || hostPauseStateChanged)
            {
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
                await Task.Delay(CommandSettleDelay, ct);
            }

            var needsPausedCorrection =
                participant.PlayState?.CanSeek != false &&
                (hostSeeked || pausedDrift > PausedDriftToleranceTicks);

            if (needsPausedCorrection)
            {
                await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
                MarkCorrected(participantId);
            }

            ResetDrift(participantId);
            return;
        }

        // When the host resumes, pre-roll the participant by the selected lead before
        // unpausing. That compensates for the participant's repeatable output delay
        // without ever delaying or otherwise disturbing the host.
        if (participantPaused)
        {
            if (participant.PlayState?.CanSeek != false)
            {
                var resumeTarget = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);
                await _api.SendPlayStateCommandAsync(participantId, "Seek", resumeTarget, ct);
                MarkCorrected(participantId);
                await Task.Delay(CommandSettleDelay, ct);
            }

            await _api.SendPlayStateCommandAsync(participantId, "Unpause", null, ct);
            ResetDrift(participantId);
            return;
        }

        if (participant.PlayState?.CanSeek == false)
            return;

        // A real host seek is intentional and should be mirrored immediately.
        // Re-arm fine alignment afterward because the remote client may settle a
        // little behind or ahead once its decoder catches up.
        if (hostSeeked)
        {
            var seekTarget = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);
            await _api.SendPlayStateCommandAsync(participantId, "Seek", seekTarget, ct);
            MarkCorrected(participantId);
            ResetDrift(participantId);
            return;
        }

        // During steady playback compare against the selected lead target, but do
        // not react to one noisy Emby position sample. Only correct after two
        // consecutive out-of-tolerance polls, then respect the correction cooldown.
        var desiredParticipantPosition = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);
        var targetError = Math.Abs(participantPosition - desiredParticipantPosition);

        if (targetError > PlayingTargetToleranceTicks)
        {
            var samples = _driftSamples.AddOrUpdate(
                participantId,
                1,
                static (_, current) => current + 1);

            if (samples >= RequiredConsecutiveDriftSamples && CorrectionAllowed(participantId))
            {
                await _api.SendPlayStateCommandAsync(participantId, "Seek", desiredParticipantPosition, ct);
                MarkCorrected(participantId);
                ResetDrift(participantId);
            }
        }
        else
        {
            ResetDrift(participantId);
        }
    }

    private void ResetDrift(string participantId) =>
        _driftSamples.TryRemove(participantId, out _);

    private bool CorrectionAllowed(string participantId)
    {
        if (!_lastCorrectionAt.TryGetValue(participantId, out var lastCorrection))
            return true;

        return DateTimeOffset.UtcNow - lastCorrection >= CorrectionCooldown;
    }

    private void MarkCorrected(string participantId) =>
        _lastCorrectionAt[participantId] = DateTimeOffset.UtcNow;

    private void ResetSyncState()
    {
        _lastCorrectionAt.Clear();
        _driftSamples.Clear();
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
