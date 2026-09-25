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
    private bool _suppressSetupEvents;

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

    private static string SessionIdentity(SessionInfoDto session) =>
        string.Join(
            "\u001F",
            session.UserName ?? "",
            session.DeviceName ?? "",
            session.Client ?? "");

    private void SaveCurrentSetup()
    {
        if (_suppressSetupEvents)
            return;

        if (HostPicker.SelectedItem is SessionInfoDto host)
            _settings.SyncLastHostIdentity = SessionIdentity(host);

        _settings.SyncLastParticipantIdentities = ParticipantsView.SelectedItems
            .OfType<SessionInfoDto>()
            .Select(SessionIdentity)
            .ToList();
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

        UpdateReadyPreview();
    }

    private async void ContinueWatchingClicked(object sender, EventArgs e) =>
        await LoadQuickMediaAsync(
            async () => (await _api.GetContinueWatchingSyncMediaAsync()).Items,
            "Continue Watching");

    private async void RecentlyPlayedClicked(object sender, EventArgs e) =>
        await LoadQuickMediaAsync(
            () => _api.GetRecentSyncMediaAsync(),
            "Recently Played");

    private async Task LoadQuickMediaAsync(
        Func<Task<List<BaseItemDto>>> loader,
        string label)
    {
        if (_searchingMedia)
            return;

        _searchingMedia = true;
        MediaSearchButton.IsEnabled = false;
        MediaSearchStatusLabel.Text = $"Loading {label}…";

        try
        {
            var items = (await loader())
                .Where(i => !string.IsNullOrWhiteSpace(i.Id))
                .GroupBy(i => i.Id!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(25)
                .ToList();

            SetMediaItems(items);
            MediaSearchStatusLabel.Text = items.Count == 0
                ? $"Nothing found in {label}."
                : $"{label} • {items.Count} item{(items.Count == 1 ? "" : "s")}";
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

    private async void MediaSearchClicked(object sender, EventArgs e) =>
        await SearchMediaAsync();

    private async void MediaSearchCompleted(object sender, EventArgs e) =>
        await SearchMediaAsync();

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

            SetMediaItems(items);
            MediaSearchStatusLabel.Text = items.Count == 0
                ? "No matching movies or episodes found."
                : $"{items.Count} match{(items.Count == 1 ? "" : "es")} • choose one.";
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

    private void SetMediaItems(List<BaseItemDto> items)
    {
        MediaPicker.ItemsSource = items;
        MediaPicker.ItemDisplayBinding = new Binding(nameof(BaseItemDto.DisplayName));
        MediaPicker.SelectedItem = items.Count == 1 ? items[0] : null;
        UpdateReadyPreview();
    }

    private void MediaChanged(object sender, EventArgs e)
    {
        if (MediaPicker.SelectedItem is BaseItemDto item)
            MediaSearchStatusLabel.Text = $"Selected • {item.DisplayName}";

        UpdateReadyPreview();
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
        if (_suppressSetupEvents)
            return;

        UpdateHostDetails();
        RebuildParticipants(restoreSaved: true);
        SaveCurrentSetup();
        UpdateReadyPreview();
    }

    private void ParticipantsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSetupEvents)
            return;

        SaveCurrentSetup();
        UpdateReadyPreview();
    }

    private List<string> GetSelectedParticipantIds() =>
        ParticipantsView.SelectedItems
            .OfType<SessionInfoDto>()
            .Select(s => s.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

    private async Task<bool> RunReadyCheckAsync(
        SessionInfoDto host,
        IReadOnlyCollection<string> participantIds,
        string? itemId,
        bool requireHostPlaying)
    {
        ReadyLabel.Text = "Checking devices…";

        var result = await _sync.CheckReadyAsync(
            host.Id!,
            participantIds,
            itemId,
            requireHostPlaying);

        ReadyLabel.Text = result.Summary;

        if (result.IsReady)
            return true;

        await DisplayAlert("Not ready yet", result.Summary, "OK");
        return false;
    }

    private async void StartFromBeginningClicked(object sender, EventArgs e)
    {
        if (HostPicker.SelectedItem is not SessionInfoDto host ||
            string.IsNullOrWhiteSpace(host.Id))
        {
            await DisplayAlert("Sync'EM up", "Choose a host device first.", "OK");
            return;
        }

        if (MediaPicker.SelectedItem is not BaseItemDto media ||
            string.IsNullOrWhiteSpace(media.Id))
        {
            await DisplayAlert(
                "Sync'EM up",
                "Choose something from Continue Watching, Recently Played, or Search first.",
                "OK");
            return;
        }

        var participantIds = GetSelectedParticipantIds();
        if (participantIds.Count == 0)
        {
            await DisplayAlert("Sync'EM up", "Choose at least one participant device.", "OK");
            return;
        }

        try
        {
            StartFromBeginningButton.IsEnabled = false;
            StartButton.IsEnabled = false;

            if (!await RunReadyCheckAsync(host, participantIds, media.Id, requireHostPlaying: false))
                return;

            SaveCurrentSetup();
            StatusLabel.Text = $"Starting {media.DisplayName} from the beginning…";
            await _sync.StartFromBeginningAsync(host.Id, participantIds, media.Id);
            StatusLabel.Text = _sync.Status;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            await DisplayAlert("Unable to start from beginning", ex.Message, "OK");
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
            await DisplayAlert("Sync'EM up", "Choose a host device first.", "OK");
            return;
        }

        var participantIds = GetSelectedParticipantIds();
        if (participantIds.Count == 0)
        {
            await DisplayAlert("Sync'EM up", "Choose at least one participant device.", "OK");
            return;
        }

        try
        {
            StartButton.IsEnabled = false;
            StartFromBeginningButton.IsEnabled = false;

            if (!await RunReadyCheckAsync(host, participantIds, null, requireHostPlaying: true))
                return;

            SaveCurrentSetup();
            StatusLabel.Text = "Joining current playback…";
            await _sync.StartAsync(host.Id, participantIds);
            StatusLabel.Text = _sync.Status;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
            await DisplayAlert("Unable to join current playback", ex.Message, "OK");
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
        UpdateReadyPreview();
    }

    private async Task LoadSessionsAsync()
    {
        if (_loading)
            return;

        _loading = true;

        var previousHostId = (HostPicker.SelectedItem as SessionInfoDto)?.Id;
        var previousParticipants = ParticipantsView.SelectedItems
            .OfType<SessionInfoDto>()
            .Select(SessionIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                .OrderByDescending(s => s.IsSyncControllable)
                .ThenByDescending(s => s.IsPlaying)
                .ThenBy(s => s.DeviceName)
                .ToList();

            _suppressSetupEvents = true;
            try
            {
                HostPicker.ItemsSource = hosts;
                HostPicker.ItemDisplayBinding =
                    new Binding(nameof(SessionInfoDto.SyncDisplay));

                HostPicker.SelectedItem =
                    hosts.FirstOrDefault(s =>
                        string.Equals(s.Id, previousHostId, StringComparison.OrdinalIgnoreCase))
                    ?? hosts.FirstOrDefault(s =>
                        string.Equals(
                            SessionIdentity(s),
                            _settings.SyncLastHostIdentity,
                            StringComparison.OrdinalIgnoreCase))
                    ?? (hosts.Count == 1 ? hosts[0] : null);

                UpdateHostDetails();

                var identitiesToRestore = previousParticipants.Count > 0
                    ? previousParticipants
                    : _settings.SyncLastParticipantIdentities
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                RebuildParticipants(
                    restoreSaved: true,
                    identitiesOverride: identitiesToRestore);
            }
            finally
            {
                _suppressSetupEvents = false;
            }

            var restoredCount = ParticipantsView.SelectedItems.Count;
            RememberedSetupLabel.Text =
                HostPicker.SelectedItem is not null && restoredCount > 0
                    ? $"Restored last setup • {restoredCount} participant{(restoredCount == 1 ? "" : "s")}"
                    : "Your last working device setup will be restored automatically.";

            if (!_sync.IsRunning)
                StatusLabel.Text = hosts.Count == 0
                    ? "No Emby device sessions are currently visible."
                    : $"Ready to Sync'EM up • {hosts.Count} device session{(hosts.Count == 1 ? "" : "s")} seen.";
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
            UpdateReadyPreview();
        }
    }

    private void RebuildParticipants(
        bool restoreSaved,
        IReadOnlyCollection<string>? identitiesOverride = null)
    {
        var hostId = (HostPicker.SelectedItem as SessionInfoDto)?.Id;
        var participants = _allSessions
            .Where(s => !string.Equals(
                s.Id,
                hostId,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.IsSyncControllable)
            .ThenByDescending(s => s.LastActivityDate)
            .ThenBy(s => s.DeviceName)
            .ToList();

        var wanted = identitiesOverride?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? _settings.SyncLastParticipantIdentities
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var previousSuppress = _suppressSetupEvents;
        _suppressSetupEvents = true;
        try
        {
            ParticipantsView.SelectedItems.Clear();
            ParticipantsView.ItemsSource = participants;

            if (restoreSaved)
            {
                foreach (var participant in participants)
                {
                    if (wanted.Contains(SessionIdentity(participant)))
                        ParticipantsView.SelectedItems.Add(participant);
                }
            }
        }
        finally
        {
            _suppressSetupEvents = previousSuppress;
        }
    }

    private void UpdateHostDetails()
    {
        if (HostPicker.SelectedItem is SessionInfoDto host)
            HostMediaLabel.Text = host.IsPlaying
                ? $"Now playing • {host.MediaDisplay} • {host.ProgressText}"
                : "Idle • ready for Start Together from Beginning";
        else
            HostMediaLabel.Text = "No host selected";
    }

    private void UpdateReadyPreview()
    {
        if (_sync.IsRunning)
        {
            ReadyLabel.Text =
                $"Sync active • {_settings.SyncParticipantLeadMilliseconds} ms lead";
            return;
        }

        var hostChosen = HostPicker.SelectedItem is SessionInfoDto;
        var participantCount = ParticipantsView.SelectedItems.Count;
        var mediaChosen = MediaPicker.SelectedItem is BaseItemDto;

        if (!hostChosen)
            ReadyLabel.Text = "Choose a host device.";
        else if (participantCount == 0)
            ReadyLabel.Text = "Choose at least one participant.";
        else if (!mediaChosen)
            ReadyLabel.Text =
                $"{participantCount + 1} devices selected • choose media or Join Current Playback.";
        else
            ReadyLabel.Text =
                $"Ready to check • {participantCount + 1} devices • {_settings.SyncParticipantLeadMilliseconds} ms lead";
    }

    private void SyncStatusChanged(object? sender, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            UpdateSyncButtons();
            UpdateReadyPreview();
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
