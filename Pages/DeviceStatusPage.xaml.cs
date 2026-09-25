using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class DeviceStatusPage : ContentPage
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly EmbyApiClient _api;
    private List<DeviceStatusItem> _all = [];
    private DeviceFilter _filter = DeviceFilter.All;

    private bool _loading;
    private DateTimeOffset? _lastLoadedAt;
    private CancellationTokenSource? _refreshCts;

    public DeviceStatusPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        UpdateQuickFilterButtons();
        TvNavigation.Attach(this, "devices");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();

        await LoadAsync(quiet: false);

        _ = RunRefreshLoopAsync(_refreshCts.Token);
    }

    protected override void OnDisappearing()
    {
        _refreshCts?.Cancel();
        base.OnDisappearing();
    }

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadAsync(quiet: false);

    private void SearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter();

    private void QuickFilterClicked(object sender, EventArgs e)
    {
        if (sender is not Button button ||
            button.CommandParameter is not string raw ||
            !Enum.TryParse<DeviceFilter>(raw, out var filter))
            return;

        _filter = filter;
        UpdateQuickFilterButtons();
        ApplyFilter();
    }

    private async Task RunRefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await MainThread.InvokeOnMainThreadAsync(() => LoadAsync(quiet: true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync(bool quiet)
    {
        if (_loading)
            return;

        _loading = true;

        if (!quiet)
            StatusLabel.Text = "Checking Emby device sessions…";

        try
        {
            var sessions = await _api.GetSessionsAsync();

            var fresh = sessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .Select(s => new DeviceStatusItem
                {
                    SessionId = s.Id!,
                    UserName = string.IsNullOrWhiteSpace(s.UserName)
                        ? "Unknown user"
                        : s.UserName,
                    DeviceName = string.IsNullOrWhiteSpace(s.DeviceName)
                        ? "Unknown device"
                        : s.DeviceName,
                    Client = s.Client ?? "",
                    MediaTitle = s.MediaDisplay,
                    MediaType = s.NowPlayingItem?.Type ?? "",
                    PlaybackMethod = s.PlaybackMethod,
                    StreamDetails = s.IsPlaying ? s.StreamDetails : "",
                    QualityDisplay = s.IsPlaying
                        ? string.Join(" • ", s.QualityBadges)
                        : "",
                    ProgressText = s.IsPlaying ? s.ProgressText : "",
                    Progress = s.Progress,
                    Endpoint = s.RemoteEndPoint ?? "",
                    LastActivityDate = s.LastActivityDate,
                    IsPlaying = s.IsPlaying,
                    IsPaused = s.PlayState?.IsPaused == true,
                    IsTranscoding = s.TranscodingInfo is not null,
                    SupportsRemoteControl = s.SupportsRemoteControl
                })
                .OrderByDescending(x => x.IsPlaying)
                .ThenByDescending(x => x.LastActivityDate)
                .ThenBy(x => x.DeviceName)
                .ToList();

            var existingById = _all.ToDictionary(
                x => x.SessionId,
                StringComparer.OrdinalIgnoreCase);

            var sameSessions =
                fresh.Count == _all.Count &&
                fresh.All(x => existingById.ContainsKey(x.SessionId));

            var requiresRebind = !sameSessions;

            if (!requiresRebind)
            {
                foreach (var item in fresh)
                {
                    var existing = existingById[item.SessionId];
                    if (!existing.HasSameCardState(item))
                    {
                        requiresRebind = true;
                        break;
                    }
                }
            }

            if (requiresRebind)
            {
                _all = fresh;
                ApplyFilter();
            }
            else
            {
                foreach (var item in fresh)
                    existingById[item.SessionId].UpdateLiveValues(item);

                UpdateStatusText();
            }

            _lastLoadedAt = DateTimeOffset.Now;
            UpdateLastUpdatedText();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchBox.Text?.Trim() ?? "";
        IEnumerable<DeviceStatusItem> query = _all;

        query = _filter switch
        {
            DeviceFilter.Playing =>
                query.Where(x => x.IsPlaying),

            DeviceFilter.Idle =>
                query.Where(x => !x.IsPlaying),

            DeviceFilter.Transcoding =>
                query.Where(x => x.IsTranscoding),

            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.Matches(search));

        var list = query
            .OrderByDescending(x => x.IsPlaying)
            .ThenByDescending(x => x.LastActivityDate)
            .ThenBy(x => x.DeviceName)
            .ToList();

        DevicesView.ItemsSource = list;

        UpdateStatusText(list.Count);
        EmptyLabel.Text = GetEmptyText(search);
    }

    private void UpdateStatusText(int? visibleCount = null)
    {
        var count = visibleCount ??
            (DevicesView.ItemsSource as IEnumerable<DeviceStatusItem>)?.Count() ??
            _all.Count;

        var playing = _all.Count(x => x.IsPlaying);
        var transcoding = _all.Count(x => x.IsTranscoding);

        StatusLabel.Text =
            $"{count} shown • {_all.Count} sessions • {playing} playing • {transcoding} transcoding";
    }

    private string GetEmptyText(string search)
    {
        if (!string.IsNullOrWhiteSpace(search))
            return $"No device sessions match “{search}”.";

        return _filter switch
        {
            DeviceFilter.Playing => "No devices are playing media right now.",
            DeviceFilter.Idle => "No idle device sessions are visible.",
            DeviceFilter.Transcoding => "No devices are transcoding right now.",
            _ => "No Emby device sessions are currently visible."
        };
    }

    private void UpdateLastUpdatedText()
    {
        LastUpdatedLabel.Text = _lastLoadedAt.HasValue
            ? $"Updated {_lastLoadedAt.Value:g} • auto-refresh every 15 seconds"
            : "Not refreshed yet";
    }

    private void UpdateQuickFilterButtons()
    {
        var buttons = new (Button Button, DeviceFilter Filter, string Text)[]
        {
            (AllFilterButton, DeviceFilter.All, "All"),
            (PlayingFilterButton, DeviceFilter.Playing, "Playing"),
            (IdleFilterButton, DeviceFilter.Idle, "Idle"),
            (TranscodingFilterButton, DeviceFilter.Transcoding, "Transcoding")
        };

        foreach (var entry in buttons)
        {
            var selected = entry.Filter == _filter;
            entry.Button.Text = selected
                ? $"✓ {entry.Text}"
                : entry.Text;
            entry.Button.Opacity = selected ? 1.0 : 0.65;
        }
    }

    private enum DeviceFilter
    {
        All,
        Playing,
        Idle,
        Transcoding
    }
}
