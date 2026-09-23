using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class UsersPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private bool _loading;
    private bool _hasLoaded;
    private string _loadedContextKey = "";

    public UsersPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync(force: false);
    }

    private async void RefreshClicked(object sender, EventArgs e) => await LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;

        var contextKey = GetContextKey();

        // Shell keeps this page alive between tab switches. If nothing relevant
        // changed, leave the existing cards alone instead of rebuilding the list.
        if (!force && _hasLoaded && string.Equals(_loadedContextKey, contextKey, StringComparison.Ordinal))
        {
            RangeCaptionLabel.Text = $"Recent playback — {_settings.HistoryRangeCaption}";
            return;
        }

        _loading = true;
        var keepExistingOnFailure =
            _hasLoaded && string.Equals(_loadedContextKey, contextKey, StringComparison.Ordinal);

        RangeCaptionLabel.Text = $"Recent playback — {_settings.HistoryRangeCaption}";
        StatusLabel.Text = "Loading users and playback history…";

        try
        {
            var usersTask = _api.GetUsersAsync();
            var sessionsTask = _api.GetSessionsAsync();
            await Task.WhenAll(usersTask, sessionsTask);

            var users = (await usersTask).Items
                .Where(u => u.Policy?.IsDisabled != true)
                .OrderBy(u => u.Name)
                .ToList();
            var sessions = await sessionsTask;
            var cutoff = _settings.GetPlaybackHistoryCutoff();

            var summaryTasks = users.Select(async user =>
            {
                var history = (await _api.GetPlaybackHistoryItemsAsync(user.Id, cutoff))
                    .Where(item => item.UserData?.LastPlayedDate is not null)
                    .OrderByDescending(item => item.UserData!.LastPlayedDate)
                    .ToList();

                var playing = sessions.FirstOrDefault(s =>
                    string.Equals(s.UserId, user.Id, StringComparison.OrdinalIgnoreCase) &&
                    s.NowPlayingItem is not null);

                string summary;
                if (playing is not null)
                {
                    summary = history.Count > 0
                        ? $"Now playing {playing.MediaDisplay} • {history.Count} played in {_settings.HistoryRangeCaption}"
                        : $"Now playing {playing.MediaDisplay}";
                }
                else if (history.FirstOrDefault() is { } recent)
                {
                    var countText = history.Count == 1 ? "1 item" : $"{history.Count} items";
                    summary = $"{countText} • Last played {recent.DisplayName} • {recent.UserData!.LastPlayedDate!.Value.LocalDateTime:g}";
                }
                else
                {
                    summary = _settings.NoPlaybackText;
                }

                return new UserPlaybackSummary
                {
                    Id = user.Id,
                    Name = user.Name,
                    PlaybackSummary = summary
                };
            });

            var summaries = (await Task.WhenAll(summaryTasks))
                .OrderBy(u => u.Name)
                .ToList();

            UsersView.ItemsSource = summaries;
            StatusLabel.Text = summaries.Count == 1 ? "1 Emby user" : $"{summaries.Count} Emby users";
            _loadedContextKey = contextKey;
            _hasLoaded = true;
        }
        catch (Exception ex)
        {
            if (!keepExistingOnFailure)
                UsersView.ItemsSource = null;

            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private string GetContextKey() =>
        string.Join("|",
            _settings.AuthenticatedUserId,
            _settings.Mode,
            _settings.LocalUrl,
            _settings.RemoteUrl,
            _settings.HistoryRange);

    private async void UserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not UserPlaybackSummary user) return;
        UsersView.SelectedItem = null;
        await Shell.Current.GoToAsync($"{nameof(UserActivityPage)}?userId={Uri.EscapeDataString(user.Id)}&userName={Uri.EscapeDataString(user.Name)}");
    }
}
