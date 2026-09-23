using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class SyncCoordinatorService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(2);
    private const long DriftToleranceTicks = TimeSpan.TicksPerSecond * 2;

    private readonly EmbyApiClient _api;
    private CancellationTokenSource? _syncCts;
    private string _hostSessionId = "";
    private HashSet<string> _participantSessionIds = new(StringComparer.OrdinalIgnoreCase);

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

        if (host.NowPlayingItem?.Id is not { Length: > 0 } itemId)
            throw new InvalidOperationException("The host must already be playing a movie or episode.");

        await SyncParticipantsToHostAsync(host, sessions, forcePlay: true, initialTimeout.Token);

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

                await SyncParticipantsToHostAsync(host, sessions, forcePlay: false, ct);
                SetStatus($"Sync'EM up active • {host.MediaDisplay} • checked {DateTime.Now:t}");
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

    private async Task SyncParticipantsToHostAsync(
        SessionInfoDto host,
        IReadOnlyCollection<SessionInfoDto> sessions,
        bool forcePlay,
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

            tasks.Add(SyncParticipantAsync(participant, itemId, hostPosition, hostPaused, forcePlay, ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SyncParticipantAsync(
        SessionInfoDto participant,
        string hostItemId,
        long hostPosition,
        bool hostPaused,
        bool forcePlay,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(participant.Id))
            return;

        var sameItem = string.Equals(
            participant.NowPlayingItem?.Id,
            hostItemId,
            StringComparison.OrdinalIgnoreCase);

        if (forcePlay || !sameItem)
        {
            await _api.PlayOnSessionAsync(participant.Id, hostItemId, hostPosition, ct);

            if (hostPaused)
                await _api.SendPlayStateCommandAsync(participant.Id, "Pause", null, ct);

            return;
        }

        var participantPaused = participant.PlayState?.IsPaused == true;
        if (participantPaused != hostPaused)
        {
            await _api.SendPlayStateCommandAsync(
                participant.Id,
                hostPaused ? "Pause" : "Unpause",
                null,
                ct);
            return;
        }

        if (participant.PlayState?.CanSeek == false)
            return;

        var participantPosition = participant.PlayState?.PositionTicks ?? 0;
        var drift = Math.Abs(participantPosition - hostPosition);

        if (drift > DriftToleranceTicks)
            await _api.SendPlayStateCommandAsync(participant.Id, "Seek", hostPosition, ct);
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
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
