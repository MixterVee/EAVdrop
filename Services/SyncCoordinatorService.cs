using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class SyncCoordinatorService
{
    private static readonly TimeSpan PlayingSyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PausedSyncInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan CommandSettleDelay = TimeSpan.FromMilliseconds(350);
    private const long HostSeekDetectionTicks = TimeSpan.TicksPerSecond * 6;

    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _syncCts;
    private string _hostSessionId = "";
    private HashSet<string> _participantSessionIds = new(StringComparer.OrdinalIgnoreCase);

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

            corrected++;
        }

        if (corrected == 0)
            throw new InvalidOperationException("No selected participant is currently available for re-alignment.");

        SetStatus($"Re-aligned once • {_settings.SyncParticipantLeadMilliseconds} ms participant lead");
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
        var host = sessions.FirstOrDefault(s =>
            string.Equals(s.Id, hostSessionId, StringComparison.OrdinalIgnoreCase))
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
        SetStatus($"Sync'EM up active • manual timing • {participants.Count} participant{(participants.Count == 1 ? "" : "s")}");
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
                var host = sessions.FirstOrDefault(s =>
                    string.Equals(s.Id, _hostSessionId, StringComparison.OrdinalIgnoreCase));

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
                SetStatus($"Sync'EM up active • {host.MediaDisplay} • {stateText} • no auto realignment");
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

            if (hostPaused)
            {
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
            }

            return;
        }

        if (!sameItem)
            return;

        var participantPaused = participant.PlayState?.IsPaused == true;

        if (hostPaused)
        {
            if (!participantPaused || hostPauseStateChanged)
            {
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
                await Task.Delay(CommandSettleDelay, ct);
            }

            // Only move the participant while paused when the host actually seeks.
            // Do not chase Emby's reported paused positions.
            if (hostSeeked && participant.PlayState?.CanSeek != false)
            {
                await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
            }

            return;
        }

        if (participantPaused)
        {
            // On a genuine host resume, do one lead-adjusted seek before unpausing.
            // If the participant merely reports paused unexpectedly, just unpause it.
            if (hostPauseStateChanged && participant.PlayState?.CanSeek != false)
            {
                var resumeTarget = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);
                await _api.SendPlayStateCommandAsync(participantId, "Seek", resumeTarget, ct);
                await Task.Delay(CommandSettleDelay, ct);
            }

            await _api.SendPlayStateCommandAsync(participantId, "Unpause", null, ct);
            return;
        }

        // A real host timeline change gets exactly one participant seek.
        if (hostSeeked && participant.PlayState?.CanSeek != false)
        {
            var seekTarget = Math.Max(0, hostPosition + ParticipantPlaybackLeadTicks);
            await _api.SendPlayStateCommandAsync(participantId, "Seek", seekTarget, ct);
        }

        // Steady playback intentionally does nothing. No timers, drift chasing,
        // or periodic seeks are allowed here. Re-align Now is the only manual
        // timing correction during otherwise steady playback.
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
