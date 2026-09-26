using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class DashboardPage : ContentPage
{
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecentRefreshInterval = TimeSpan.FromMinutes(1);

    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private readonly SyncCoordinatorService _sync;

    private bool _loading;
    private CancellationTokenSource? _refreshCts;
    private DateTimeOffset? _lastRecentActivityAt;

    public DashboardPage()
    {
        InitializeComponent();

        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
        _sync = MauiProgram.Services.GetRequiredService<SyncCoordinatorService>();

        _sync.StatusChanged += SyncStatusChanged;
        TvNavigation.Attach(this, "dashboard");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();

        var refreshCts = new CancellationTokenSource();
        _refreshCts = refreshCts;

        UpdateSyncStatus();
        await LoadAsync();

        // On a fresh install AppShell can redirect from Dashboard to Settings
        // while the first load is still awaiting. Only start the refresh loop
        // if this page still owns the current cancellation source.
        if (ReferenceEquals(_refreshCts, refreshCts) &&
            !refreshCts.IsCancellationRequested)
        {
            _ = AutoRefreshAsync(refreshCts.Token);
        }
    }

    protected override void OnDisappearing()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;

        base.OnDisappearing();
    }

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadAsync(forceRecent: true);

    private async void OpenSyncClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//sync");

    private async void OpenActivityClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//activity");

    private async void OpenDevicesClicked(object sender, EventArgs e) =>
        await Shell.Current.GoToAsync("//devices");

    private async Task AutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(LiveRefreshInterval);

            while (await timer.WaitForNextTickAsync(ct))
            {
                await MainThread.InvokeOnMainThreadAsync(
                    () => LoadAsync(forceRecent: false));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync(bool forceRecent = false)
    {
        if (_loading)
            return;

        _loading = true;
        BusyIndicator.IsVisible = BusyIndicator.IsRunning = true;

        if (string.IsNullOrWhiteSpace(StatusLabel.Text))
            StatusLabel.Text = "Refreshing dashboard…";

        try
        {
            var infoTask = _api.GetSystemInfoAsync();
            var sessionsTask = _api.GetSessionsAsync();

            await Task.WhenAll(infoTask, sessionsTask);

            var info = await infoTask;
            var sessions = await sessionsTask;

            var active = sessions
                .Where(s => s.NowPlayingItem is not null)
                .OrderBy(s => s.UserName)
                .ThenBy(s => s.DeviceName)
                .ToList();

            var activeUsers = active
                .Select(s =>
                    !string.IsNullOrWhiteSpace(s.UserId)
                        ? s.UserId!
                        : s.UserDisplay)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var transcoding = sessions.Count(s => s.TranscodingInfo is not null);
            var idle = sessions.Count - active.Count;

            BindableLayout.SetItemsSource(SessionsStack, active);

            EmptyLabel.IsVisible = active.Count == 0;
            NowPlayingCaption.Text = active.Count switch
            {
                0 => "No active playback sessions",
                1 => "1 active playback session",
                _ => $"{active.Count} active playback sessions"
            };

            ActiveStreamsValue.Text = active.Count.ToString();
            ActiveStreamsDetail.Text =
                active.Count == 1 ? "active stream" : "active streams";

            ActiveUsersValue.Text = activeUsers.ToString();

            DeviceSessionsValue.Text = sessions.Count.ToString();
            DeviceSessionsDetail.Text =
                idle == 1
                    ? "1 idle session"
                    : $"{idle} idle sessions";

            TranscodingValue.Text = transcoding.ToString();
            TranscodingDetail.Text =
                transcoding == 1
                    ? "active transcode"
                    : "active transcodes";

            var serverName = info.ServerName ?? "Emby";
            var version = info.Version ?? "Unknown version";

            ConnectionLabel.Text = $"{serverName} • {version}";
            ServerNameLabel.Text = $"{serverName} • {version}";
            ServerDetailLabel.Text = string.IsNullOrWhiteSpace(_api.LastConnectedBaseUrl)
                ? "Connected"
                : _api.LastConnectedBaseUrl;
            ConnectionModeLabel.Text = GetConnectionModeDisplay();

            UpdateSyncStatus();

            StatusLabel.Text =
                $"Updated {DateTime.Now:t} • live refresh every {LiveRefreshInterval.TotalSeconds:0} seconds";

            if (forceRecent ||
                !_lastRecentActivityAt.HasValue ||
                DateTimeOffset.UtcNow - _lastRecentActivityAt.Value >= RecentRefreshInterval)
            {
                await LoadRecentActivityAsync();
            }
        }
        catch (Exception ex)
        {
            ConnectionLabel.Text = "Connection failed";
            ServerNameLabel.Text = "Unable to reach Emby";
            ServerDetailLabel.Text = "";
            ConnectionModeLabel.Text = "";
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = false;
            _loading = false;
        }
    }

    private async Task LoadRecentActivityAsync()
    {
        try
        {
            var users = (await _api.GetUsersAsync()).Items
                .Where(u => u.Policy?.IsDisabled != true)
                .ToList();

            var tasks = users.Select(async user =>
            {
                var result = await _api.GetRecentPlayedItemsAsync(
                    user.Id,
                    limit: 5);

                return result.Items
                    .Where(item => item.UserData?.LastPlayedDate is not null)
                    .Select(item => new ActivityFeedItem
                    {
                        UserId = user.Id,
                        UserName = user.Name,
                        Title = item.DisplayName,
                        Type = item.Type ?? "Media",
                        SortDate = item.UserData!.LastPlayedDate!.Value,
                        IsNowPlaying = false
                    })
                    .ToList();
            });

            var recent = (await Task.WhenAll(tasks))
                .SelectMany(x => x)
                .OrderByDescending(x => x.SortDate)
                .Take(5)
                .ToList();

            BindableLayout.SetItemsSource(RecentActivityStack, recent);

            RecentEmptyLabel.IsVisible = recent.Count == 0;
            RecentActivityCaption.Text = recent.Count switch
            {
                0 => "No completed playback found",
                1 => "Latest completed playback",
                _ => $"Latest {recent.Count} completed plays"
            };

            _lastRecentActivityAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            RecentActivityCaption.Text =
                $"Recent activity unavailable • {ex.Message}";
        }
    }

    private string GetConnectionModeDisplay()
    {
        static string Normalize(string value) =>
            (value ?? "").Trim().TrimEnd('/');

        var connected = Normalize(_api.LastConnectedBaseUrl);
        var local = Normalize(_settings.LocalUrl);
        var remote = Normalize(_settings.RemoteUrl);

        var route =
            !string.IsNullOrWhiteSpace(connected) &&
            string.Equals(connected, local, StringComparison.OrdinalIgnoreCase)
                ? "Local"
                : !string.IsNullOrWhiteSpace(connected) &&
                  string.Equals(connected, remote, StringComparison.OrdinalIgnoreCase)
                    ? "Remote"
                    : _settings.Mode.ToString();

        return _settings.Mode == ConnectionMode.Auto
            ? $"{route} • Auto mode"
            : route;
    }

    private void UpdateSyncStatus()
    {
        SyncStatusLabel.Text = _sync.IsRunning
            ? _sync.Status
            : "Not syncing • open Sync'EM to start a group session.";
    }

    private void SyncStatusChanged(object? sender, string status)
    {
        MainThread.BeginInvokeOnMainThread(UpdateSyncStatus);
    }
}
