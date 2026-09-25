using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class SyncPage : ContentPage
{
    private static readonly int[] ParticipantLeadOptionsMs =
        Enumerable.Range(0, 16).Select(i => i * 100).ToArray();

    private readonly EmbyApiClient _api;
    private readonly SyncCoordinatorService _sync;
    private readonly SettingsService _settings;
    private List<SessionInfoDto> _allSessions = [];
    private HashSet<string> _controllableSessionIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private bool _searchingMedia;

    public SyncPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _sync = MauiProgram.Services.GetRequiredService<SyncCoordinatorService>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();

        ParticipantLeadPicker.ItemsSource = ParticipantLeadOptionsMs
            .Select(ms => ms == 0 ? "0 ms (none)" : $"{ms} ms")
            .ToList();

        _sync.StatusChanged += SyncStatusChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var leadIndex = Array.IndexOf(
            ParticipantLeadOptionsMs,
            _settings.SyncParticipantLeadMilliseconds);
        ParticipantLeadPicker.SelectedIndex = leadIndex >= 0 ? leadIndex : 5;

        UpdateSyncButtons();
        StatusLabel.Text = _sync.Status;
        await LoadSessionsAsync();
    }

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadSessionsAsync();

    private void ParticipantLeadChanged(object sender, EventArgs e)
    {
        var index = ParticipantLeadPicker.SelectedIndex;
        if (index < 0 || index >= ParticipantLeadOptionsMs.Length)
            return;

        var ms = ParticipantLeadOptionsMs[index];
        _settings.SyncParticipantLeadMilliseconds = ms;

        if (_sync.IsRunning)
            StatusLabel.Text =
                $"Participant lead set to {ms} ms • tap Precision Re-align to apply it immediately.";
    }

    private async void MediaSearchClicked(object sender, EventArgs e) =>
        await SearchMediaAsync();

    private async void MediaSearchCompleted(object sender, EventArgs e) =>
        await SearchMediaAsync();

    private void MediaChanged(object sender, EventArgs e)
    {
        if (MediaPicker.SelectedItem is BaseItemDto item)
            MediaSearchStatusLabel.Text = $"Selected • {item.DisplayName}";

        UpdateSyncButtons();
    }

    private async Task SearchMediaAsync()
    {
        if (_searchingMedia)
            return;

        var term = MediaSearchEntry.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(term))
        {
            await DisplayAlert(
                "Choose media",
                "Enter part of a movie, show, or episode title first.",
                "OK");
            return;
        }

        _searchingMedia = true;
        MediaSearchButton.IsEnabled = false;
        MediaSearchStatusLabel.Text = "Searching Emby library…";

        try
        {
            var result = await _api.SearchSyncMediaAsync(term);
            var items = result.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .OrderBy(i => i.DisplayName)
                .ToList();

            MediaPicker.ItemsSource = items;
            MediaPicker.ItemDisplayBinding = new Binding(nameof(BaseItemDto.DisplayName));
            MediaPicker.SelectedItem = items.Count == 1 ? items[0] : null;
            MediaSearchStatusLabel.Text = items.Count == 0
                ? "No matching movies or episodes found."
                : $"{items.Count} match{(items.Count == 1 ? "" : "es")} • choose one above.";
        }
        catch (Exception ex)
        {
            MediaPicker.ItemsSource = null;
            MediaSearchStatusLabel.Text = ex.Message;
        }
        finally
        {
            _searchingMedia = false;
            MediaSearchButton.IsEnabled = true;
            UpdateSyncButtons();
        }
    }

    private async void RealignClicked(object sender, EventArgs e)
    {
        try
        {
            RealignButton.IsEnabled = false;
            StatusLabel.Text = "Precision re-aligning playback…";
            await _sync.RealignNowAsync();
            StatusLabel.Text = _sync.Status;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            await DisplayAlert("Unable to re-align", ex.Message, "OK");
        }
        finally
        {
            UpdateSyncButtons();
        }
    }

    private void HostChanged(object sender, EventArgs e)
    {
        UpdateHostDetails();
        RebuildParticipants();
    }

    private List<string> GetSelectedParticipantIds() =>
        ParticipantsView.SelectedItems
            .OfType<SessionInfoDto>()
            .Select(s => s.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

    private async void StartFromBeginningClicked(object sender, EventArgs e)
    {
        if (HostPicker.SelectedItem is not SessionInfoDto host ||
            string.IsNullOrWhiteSpace(host.Id))
        {
            await DisplayAlert(
                "Sync'EM up",
                "Choose a host device first.",
                "OK");
            return;
        }

        if (MediaPicker.SelectedItem is not BaseItemDto media ||
            string.IsNullOrWhiteSpace(media.Id))
        {
            await DisplayAlert(
                "Sync'EM up",
                "Search for and choose a movie or episode first.",
                "OK");
            return;
        }

        var participantIds = GetSelectedParticipantIds();
        if (participantIds.Count == 0)
        {
            await DisplayAlert(
                "Sync'EM up",
                "Choose at least one participant device.",
                "OK");
            return;
        }

        try
        {
            StartFromBeginningButton.IsEnabled = false;
            StartButton.IsEnabled = false;
            StatusLabel.Text = $"Starting {media.DisplayName} from the beginning…";
            await _sync.StartFromBeginningAsync(host.Id, participantIds, media.Id);
            StatusLabel.Text = _sync.Status;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            await DisplayAlert(
                "Unable to start from beginning",
                ex.Message,
                "OK");
        }
        finally
        {
            UpdateSyncButtons();
        }
    }

    private async void StartClicked(object sender, EventArgs e)
    {
        if (HostPicker.SelectedItem is not SessionInfoDto host ||
            string.IsNullOrWhiteSpace(host.Id))
        {
            await DisplayAlert(
                "Sync'EM up",
                "Choose a host device first.",
                "OK");
            return;
        }

        if (!host.IsPlaying)
        {
            await DisplayAlert(
                "Join Current Playback",
                "The selected host is idle. Start media on the host first, or use Start from Beginning.",
                "OK");
            return;
        }

        var participantIds = GetSelectedParticipantIds();
        if (participantIds.Count == 0)
        {
            await DisplayAlert(
                "Sync'EM up",
                "Choose at least one participant device.",
                "OK");
            return;
        }

        try
        {
            StartButton.IsEnabled = false;
            StartFromBeginningButton.IsEnabled = false;
            StatusLabel.Text = "Joining current playback…";
            await _sync.StartAsync(host.Id, participantIds);
            StatusLabel.Text = _sync.Status;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            await DisplayAlert(
                "Unable to join current playback",
                ex.Message,
                "OK");
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
        if (_loading)
            return;

        _loading = true;
        var previousHostId = (HostPicker.SelectedItem as SessionInfoDto)?.Id;
        StatusLabel.Text = _sync.IsRunning
            ? _sync.Status
            : "Looking for Emby devices…";

        try
        {
            var allSessionsTask = _api.GetSessionsAsync();
            var controllableSessionsTask = _api.GetControllableSessionsAsync();
            await Task.WhenAll(allSessionsTask, controllableSessionsTask);

            _allSessions = (await allSessionsTask)
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .OrderByDescending(s => s.IsPlaying)
                .ThenBy(s => s.DeviceName)
                .ThenBy(s => s.UserName)
                .ToList();

            _controllableSessionIds = (await controllableSessionsTask)
                .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                .Select(s => s.Id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var session in _allSessions)
                session.IsControllableForSignedInUser =
                    !string.IsNullOrWhiteSpace(session.Id) &&
                    _controllableSessionIds.Contains(session.Id);

            var hosts = _allSessions
                .OrderByDescending(s =>
                    s.IsControllableForSignedInUser || s.SupportsRemoteControl)
                .ThenByDescending(s => s.IsPlaying)
                .ThenBy(s => s.DeviceName)
                .ToList();

            HostPicker.ItemsSource = hosts;
            HostPicker.ItemDisplayBinding =
                new Binding(nameof(SessionInfoDto.SyncDisplay));
            HostPicker.SelectedItem = hosts.FirstOrDefault(s =>
                string.Equals(
                    s.Id,
                    previousHostId,
                    StringComparison.OrdinalIgnoreCase));

            if (HostPicker.SelectedItem is null && hosts.Count == 1)
                HostPicker.SelectedItem = hosts[0];

            UpdateHostDetails();
            RebuildParticipants();

            if (!_sync.IsRunning)
            {
                var hostId = (HostPicker.SelectedItem as SessionInfoDto)?.Id;
                var participants = _allSessions
                    .Where(s => !string.Equals(
                        s.Id,
                        hostId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var reportedControllable = participants.Count(s =>
                    s.IsControllableForSignedInUser ||
                    s.SupportsRemoteControl);

                StatusLabel.Text = hosts.Count == 0
                    ? "No Emby device sessions are currently visible."
                    : $"Ready to Sync'EM up • {hosts.Count} device session{(hosts.Count == 1 ? "" : "s")} seen • {reportedControllable} participant candidate{(reportedControllable == 1 ? "" : "s")} reported controllable.";
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
        ParticipantsView.ItemsSource = _allSessions
            .Where(s => !string.Equals(
                s.Id,
                hostId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s =>
                s.IsControllableForSignedInUser ||
                s.SupportsRemoteControl)
            .ThenByDescending(s => s.LastActivityDate)
            .ThenBy(s => s.DeviceName)
            .ToList();
    }

    private void UpdateHostDetails()
    {
        if (HostPicker.SelectedItem is SessionInfoDto host)
            HostMediaLabel.Text = host.IsPlaying
                ? $"Host media • {host.MediaDisplay} • {host.ProgressText}"
                : "Host is idle • ready for Start from Beginning";
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
        var idle = !_sync.IsRunning && !_loading;
        StartFromBeginningButton.IsEnabled = idle && !_searchingMedia;
        StartButton.IsEnabled = idle;
        StopButton.IsEnabled = _sync.IsRunning;
        RealignButton.IsEnabled = _sync.IsRunning && !_loading;
    }
}
