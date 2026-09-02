using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class DashboardPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private bool _loading;
    private CancellationTokenSource? _refreshCts;

    public DashboardPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        await LoadAsync();
        _ = AutoRefreshAsync(_refreshCts.Token);
    }

    protected override void OnDisappearing()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        base.OnDisappearing();
    }

    private async void RefreshClicked(object sender, EventArgs e) => await LoadAsync();

    private async Task AutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                await MainThread.InvokeOnMainThreadAsync(LoadAsync);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        BusyIndicator.IsVisible = BusyIndicator.IsRunning = true;
        StatusLabel.Text = "Loading active sessions…";

        try
        {
            var infoTask = _api.GetSystemInfoAsync();
            var sessionsTask = _api.GetSessionsAsync();
            await Task.WhenAll(infoTask, sessionsTask);

            var info = await infoTask;
            var active = (await sessionsTask)
                .Where(s => s.NowPlayingItem is not null)
                .OrderBy(s => s.UserName)
                .ToList();

            SessionsView.ItemsSource = active;
            EmptyLabel.IsVisible = active.Count == 0;
            ConnectionLabel.Text = $"{info.ServerName ?? "Emby"} • {info.Version ?? "Unknown version"} • {_api.LastConnectedBaseUrl}";
            StatusLabel.Text = active.Count == 1 ? "1 active playback session • live" : $"{active.Count} active playback sessions • live";
        }
        catch (Exception ex)
        {
            SessionsView.ItemsSource = null;
            EmptyLabel.IsVisible = false;
            ConnectionLabel.Text = "Connection failed";
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = false;
            _loading = false;
        }
    }
}
