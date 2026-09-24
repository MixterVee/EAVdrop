using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class SyncCoordinatorService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CorrectionCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandSettleDelay = TimeSpan.FromMilliseconds(350);

    // Normal clients can naturally differ by a second or two because of buffering,
    // decoding and reporting latency. Do not chase that harmless difference.
    private const long PlayingDriftToleranceTicks = TimeSpan.TicksPerSecond * 4;
    private const long PausedDriftToleranceTicks = TimeSpan.TicksPerSecond * 1;
    private const long HostSeekDetectionTicks = TimeSpan.TicksPerSecond * 6;

    private readonly EmbyApiClient _api;
    private CancellationTokenSource? _syncCts;
    private string _hostSessionId = "";
    private HashSet<string> _participantSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastCorrectionAt = new(StringComparer.OrdinalIgnoreCase);

    private string? _lastHostItemId;
    private long? _lastHostPositionTicks;
    private bool? _lastHostPaused;
    private DateTimeOffset? _lastHostObservedAt;

    public bool IsRunning => _syncCts is not null && !_syncCts.IsCancellationRequested;
    public string Status { get; private set; } = "Not syncing";

    public event EventHandler<string>? StatusChanged;

    public SyncCoordinatorService(EmbyApiClient api)
    {
        _api = api;
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
                await Task.Delay(SyncInterval, ct);

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

        if (forcePlay || !sameItem)
        {
            await _api.PlayOnSessionAsync(participantId, hostItemId, hostPosition, ct);
            MarkCorrected(participantId);

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

        var participantPaused = participant.PlayState?.IsPaused == true;
        var participantPosition = participant.PlayState?.PositionTicks ?? 0;
        var drift = Math.Abs(participantPosition - hostPosition);

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
                (hostSeeked || drift > PausedDriftToleranceTicks) &&
                (hostSeeked || CorrectionAllowed(participantId));

            if (needsPausedCorrection)
            {
                await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
                await Task.Delay(CommandSettleDelay, ct);
                await _api.SendPlayStateCommandAsync(participantId, "Pause", null, ct);
                MarkCorrected(participantId);
            }

            return;
        }

        // When the host resumes, line the participant up before unpausing so it
        // doesn't visibly play from an old paused position.
        if (participantPaused)
        {
            if (participant.PlayState?.CanSeek != false &&
                (hostSeeked || drift > PlayingDriftToleranceTicks))
            {
                await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
                MarkCorrected(participantId);
                await Task.Delay(CommandSettleDelay, ct);
            }

            await _api.SendPlayStateCommandAsync(participantId, "Unpause", null, ct);
            return;
        }

        if (participant.PlayState?.CanSeek == false)
            return;

        // A real host seek is intentional and should be mirrored immediately.
        if (hostSeeked)
        {
            await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
            MarkCorrected(participantId);
            return;
        }

        // During ordinary playback tolerate a few seconds of natural client/reporting
        // difference. If correction is needed, do it at most once per cooldown.
        if (drift > PlayingDriftToleranceTicks && CorrectionAllowed(participantId))
        {
            await _api.SendPlayStateCommandAsync(participantId, "Seek", hostPosition, ct);
            MarkCorrected(participantId);
        }
    }

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
