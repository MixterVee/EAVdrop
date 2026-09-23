using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class SyncPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private readonly SyncCoordinatorService _sync;
    private List<SessionInfoDto> _sessions = [];
    private bool _loading;

    public SyncPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _sync = MauiProgram.Services.GetRequiredService<SyncCoordinatorService>();
        _sync.StatusChanged += SyncStatusChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateSyncButtons();
        StatusLabel.Text = _sync.Status;
        await LoadSessionsAsync();
    }

    private async void RefreshClicked(object sender, EventArgs e) => await LoadSessionsAsync();

    private void HostChanged(object sender, EventArgs e)
    {
        UpdateHostDetails();
        RebuildParticipants();
    }

    private async void StartClicked(object sender, EventArgs e)
    {
        if (HostPicker.SelectedItem is not SessionInfoDto host || string.IsNullOrWhiteSpace(host.Id))
        {
            await DisplayAlert("Sync'EM up", "Choose a currently playing host device first.", "OK");
            return;
        }

        var participantIds = ParticipantsView.SelectedItems
            .OfType<SessionInfoDto>()
            .Select(s => s.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        if (participantIds.Count == 0)
        {
            await DisplayAlert("Sync'EM up", "Choose at least one participant device.", "OK");
            return;
        }

        try
        {
            StartButton.IsEnabled = false;
            StatusLabel.Text = "Starting synchronized playback…";
            await _sync.StartAsync(host.Id, participantIds);
            StatusLabel.Text = _sync.Status;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            await DisplayAlert("Unable to start Sync'EM up", ex.Message, "OK");
        }
        finally
        {
            UpdateSyncButtons();
        }
    }

    private void StopClicked(object sender, EventArgs e)
    {
        _sync.Stop();
        StatusLabel.Text = _sync.Status;
        UpdateSyncButtons();
    }

    private async Task LoadSessionsAsync()
    {
        if (_loading) return;
        _loading = true;

        var previousHostId = (HostPicker.SelectedItem as SessionInfoDto)?.Id;
        StatusLabel.Text = _sync.IsRunning ? _sync.Status : "Looking for controllable Emby devices…";

        try
        {
            _sessions = (await _api.GetControllableSessionsAsync())
                .Where(s => s.IsSyncControllable)
                .OrderBy(s => s.DeviceName)
                .ThenBy(s => s.UserName)
                .ToList();

            var hosts = _sessions.Where(s => s.IsPlaying).ToList();

            HostPicker.ItemsSource = hosts;
            HostPicker.ItemDisplayBinding = new Binding(nameof(SessionInfoDto.SyncDisplay));
            HostPicker.SelectedItem =
                hosts.FirstOrDefault(s => string.Equals(s.Id, previousHostId, StringComparison.OrdinalIgnoreCase));

            if (HostPicker.SelectedItem is null && hosts.Count == 1)
                HostPicker.SelectedItem = hosts[0];

            UpdateHostDetails();
            RebuildParticipants();

            if (!_sync.IsRunning)
            {
                StatusLabel.Text = hosts.Count == 0
                    ? "No remotely controllable Emby session is currently playing."
                    : "Ready to Sync'EM up.";
            }
        }
        catch (Exception ex)
        {
            HostPicker.ItemsSource = null;
            ParticipantsView.ItemsSource = null;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
            UpdateSyncButtons();
        }
    }

    private void RebuildParticipants()
    {
        var hostId = (HostPicker.SelectedItem as SessionInfoDto)?.Id;
        ParticipantsView.SelectedItems.Clear();
        ParticipantsView.ItemsSource = _sessions
            .Where(s => !string.Equals(s.Id, hostId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void UpdateHostDetails()
    {
        if (HostPicker.SelectedItem is SessionInfoDto host)
            HostMediaLabel.Text = $"Host media • {host.MediaDisplay} • {host.ProgressText}";
        else
            HostMediaLabel.Text = "No host selected";
    }

    private void SyncStatusChanged(object? sender, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            UpdateSyncButtons();
        });
    }

    private void UpdateSyncButtons()
    {
        StartButton.IsEnabled = !_sync.IsRunning && !_loading;
        StopButton.IsEnabled = _sync.IsRunning;
    }
}
