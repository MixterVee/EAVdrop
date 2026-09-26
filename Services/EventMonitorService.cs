using System.Text.Json;
using EAVdrop.Models;

namespace EAVdrop.Services;

public sealed class EventMonitorService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private const int MaxEvents = 60;
    private const string StoredEventsKey = "eavdrop_event_history_v1";

    private readonly EmbyApiClient _api;
    private readonly SyncCoordinatorService _sync;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly List<EavEventItem> _events = [];
    private Dictionary<string, SessionSnapshot> _baseline =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private bool _hasBaseline;
    private bool _historyLoaded;
    private bool _lastSyncRunning;

    public event EventHandler<EavEventItem>? EventAdded;
    public event EventHandler? EventsChanged;

    public EventMonitorService(
        EmbyApiClient api,
        SyncCoordinatorService sync)
    {
        _api = api;
        _sync = sync;
        _lastSyncRunning = sync.IsRunning;
        _sync.StatusChanged += SyncStatusChanged;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _cts is not null && !_cts.IsCancellationRequested;
        }
    }

    public IReadOnlyList<EavEventItem> GetEvents(int? limit = null)
    {
        EnsureHistoryLoaded();

        lock (_gate)
        {
            IEnumerable<EavEventItem> query = _events
                .OrderByDescending(x => x.Timestamp);

            if (limit is > 0)
                query = query.Take(limit.Value);

            return query.ToList();
        }
    }

    public void Start()
    {
        EnsureHistoryLoaded();

        lock (_gate)
        {
            if (_cts is not null && !_cts.IsCancellationRequested)
                return;

            _baseline = new Dictionary<string, SessionSnapshot>(
                StringComparer.OrdinalIgnoreCase);
            _hasBaseline = false;
            _lastSyncRunning = _sync.IsRunning;
            _cts = new CancellationTokenSource();

            _ = RunAsync(_cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;

        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _baseline.Clear();
            _hasBaseline = false;
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Clear()
    {
        EnsureHistoryLoaded();

        lock (_gate)
            _events.Clear();

        Preferences.Default.Remove(StoredEventsKey);
        EventsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await PollOnceAsync(ct);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await PollOnceAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyCollection<SessionInfoDto> sessions;

        try
        {
            sessions = await _api.GetSessionsAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Event monitoring is deliberately quiet. The normal pages already
            // report connection failures; the monitor simply tries again later.
            return;
        }

        var current = sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .Select(SessionSnapshot.From)
            .ToDictionary(x => x.SessionId, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, SessionSnapshot> previous;

        lock (_gate)
        {
            if (!_hasBaseline)
            {
                _baseline = current;
                _hasBaseline = true;
                return;
            }

            previous = _baseline;
            _baseline = current;
        }

        foreach (var now in current.Values)
        {
            if (!previous.TryGetValue(now.SessionId, out var before))
            {
                if (now.IsPlaying)
                {
                    AddPlaybackEvent(
                        "Playback started",
                        now,
                        now.MediaTitle);
                }
                else
                {
                    AddEvent(
                        EavEventKind.User,
                        "User active",
                        Join(now.UserName, now.DeviceDisplay));
                }

                if (now.IsTranscoding)
                {
                    AddEvent(
                        EavEventKind.Transcoding,
                        "Transcoding started",
                        Join(now.UserName, now.MediaTitle, now.DeviceDisplay));
                }

                continue;
            }

            if (!before.IsPlaying && now.IsPlaying)
            {
                AddPlaybackEvent(
                    "Playback started",
                    now,
                    now.MediaTitle);
            }
            else if (before.IsPlaying && !now.IsPlaying)
            {
                AddPlaybackEvent(
                    "Playback stopped",
                    before,
                    before.MediaTitle);
            }
            else if (before.IsPlaying &&
                     now.IsPlaying &&
                     !string.Equals(
                         before.ItemId,
                         now.ItemId,
                         StringComparison.OrdinalIgnoreCase))
            {
                AddPlaybackEvent(
                    "Playback changed",
                    now,
                    now.MediaTitle);
            }

            if (!before.IsTranscoding && now.IsTranscoding)
            {
                AddEvent(
                    EavEventKind.Transcoding,
                    "Transcoding started",
                    Join(now.UserName, now.MediaTitle, now.DeviceDisplay));
            }
            else if (before.IsTranscoding && !now.IsTranscoding)
            {
                AddEvent(
                    EavEventKind.Transcoding,
                    "Transcoding stopped",
                    Join(before.UserName, before.MediaTitle, before.DeviceDisplay));
            }
        }

        foreach (var before in previous.Values)
        {
            if (current.ContainsKey(before.SessionId))
                continue;

            if (before.IsPlaying)
            {
                AddPlaybackEvent(
                    "Playback stopped",
                    before,
                    before.MediaTitle);
            }
        }
    }

    private void AddPlaybackEvent(
        string title,
        SessionSnapshot session,
        string mediaTitle)
    {
        AddEvent(
            EavEventKind.Playback,
            title,
            Join(
                session.UserName,
                mediaTitle,
                session.DeviceDisplay));
    }

    private void SyncStatusChanged(object? sender, string status)
    {
        var running = _sync.IsRunning;

        if (!_lastSyncRunning && running)
        {
            AddEvent(
                EavEventKind.Sync,
                "Sync'EM started",
                status);
        }
        else if (_lastSyncRunning && !running)
        {
            AddEvent(
                EavEventKind.Sync,
                "Sync'EM stopped",
                status);
        }
        else if (status.StartsWith(
                     "Precision Re-align complete",
                     StringComparison.OrdinalIgnoreCase))
        {
            AddEvent(
                EavEventKind.Sync,
                "Sync'EM re-aligned",
                status);
        }
        else if (status.StartsWith(
                     "Sync stopped",
                     StringComparison.OrdinalIgnoreCase) ||
                 status.StartsWith(
                     "Sync paused by error",
                     StringComparison.OrdinalIgnoreCase))
        {
            AddEvent(
                EavEventKind.Sync,
                "Sync'EM stopped",
                status);

            running = false;
        }

        _lastSyncRunning = running;
    }

    private void AddEvent(
        EavEventKind kind,
        string title,
        string detail)
    {
        EnsureHistoryLoaded();

        var item = new EavEventItem
        {
            Timestamp = DateTimeOffset.Now,
            Kind = kind,
            Title = title,
            Detail = detail
        };

        lock (_gate)
        {
            // Suppress identical events generated almost simultaneously by two
            // state transitions (for example a session disappearing while its
            // playback flag also changes).
            var newest = _events
                .OrderByDescending(x => x.Timestamp)
                .FirstOrDefault();

            if (newest is not null &&
                newest.Kind == item.Kind &&
                string.Equals(newest.Title, item.Title, StringComparison.Ordinal) &&
                string.Equals(newest.Detail, item.Detail, StringComparison.Ordinal) &&
                item.Timestamp - newest.Timestamp < TimeSpan.FromSeconds(3))
            {
                return;
            }

            _events.Add(item);

            if (_events.Count > MaxEvents)
            {
                var removeCount = _events.Count - MaxEvents;
                _events.RemoveRange(0, removeCount);
            }

            SaveHistoryLocked();
        }

        EventAdded?.Invoke(this, item);
        EventsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureHistoryLoaded()
    {
        lock (_gate)
        {
            if (_historyLoaded)
                return;

            _historyLoaded = true;

            var raw = Preferences.Default.Get(StoredEventsKey, "");
            if (string.IsNullOrWhiteSpace(raw))
                return;

            try
            {
                var stored = JsonSerializer.Deserialize<List<StoredEvent>>(raw, _json)
                    ?? [];

                _events.AddRange(
                    stored
                        .OrderBy(x => x.Timestamp)
                        .TakeLast(MaxEvents)
                        .Select(x => new EavEventItem
                        {
                            Id = x.Id,
                            Timestamp = x.Timestamp,
                            Kind = Enum.TryParse<EavEventKind>(x.Kind, out var kind)
                                ? kind
                                : EavEventKind.Playback,
                            Title = x.Title,
                            Detail = x.Detail
                        }));
            }
            catch
            {
                Preferences.Default.Remove(StoredEventsKey);
            }
        }
    }

    private void SaveHistoryLocked()
    {
        try
        {
            var stored = _events
                .OrderBy(x => x.Timestamp)
                .TakeLast(MaxEvents)
                .Select(x => new StoredEvent(
                    x.Id,
                    x.Timestamp,
                    x.Kind.ToString(),
                    x.Title,
                    x.Detail))
                .ToList();

            Preferences.Default.Set(
                StoredEventsKey,
                JsonSerializer.Serialize(stored, _json));
        }
        catch
        {
            // Event history is helpful, not critical. Keep monitoring even if
            // this device refuses a Preferences write.
        }
    }

    private static string Join(params string?[] parts) =>
        string.Join(
            " • ",
            parts.Where(x => !string.IsNullOrWhiteSpace(x)));

    private sealed record SessionSnapshot(
        string SessionId,
        string UserName,
        string DeviceDisplay,
        string ItemId,
        string MediaTitle,
        bool IsPlaying,
        bool IsTranscoding)
    {
        public static SessionSnapshot From(SessionInfoDto session) =>
            new(
                session.Id ?? "",
                session.UserDisplay,
                session.DeviceDisplay,
                session.NowPlayingItem?.Id ?? "",
                session.MediaDisplay,
                session.IsPlaying,
                session.TranscodingInfo is not null);
    }

    private sealed record StoredEvent(
        string Id,
        DateTimeOffset Timestamp,
        string Kind,
        string Title,
        string Detail);
}
