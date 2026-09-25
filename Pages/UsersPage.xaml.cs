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

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadAsync(force: true);

    private async Task LoadAsync(bool force)
    {
        if (_loading)
            return;

        var contextKey = GetContextKey();

        if (!force &&
            _hasLoaded &&
            string.Equals(_loadedContextKey, contextKey, StringComparison.Ordinal))
        {
            RangeCaptionLabel.Text =
                $"Playback overview — {_settings.HistoryRangeCaption}";
            return;
        }

        _loading = true;
        var keepExistingOnFailure =
            _hasLoaded &&
            string.Equals(_loadedContextKey, contextKey, StringComparison.Ordinal);

        RangeCaptionLabel.Text =
            $"Playback overview — {_settings.HistoryRangeCaption}";
        StatusLabel.Text = "Loading users and recent playback…";

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

                var playing = sessions
                    .Where(s =>
                        string.Equals(
                            s.UserId,
                            user.Id,
                            StringComparison.OrdinalIgnoreCase) &&
                        s.NowPlayingItem is not null)
                    .OrderByDescending(s => s.LastActivityDate)
                    .FirstOrDefault();

                var recentLines = history
                    .Take(3)
                    .Select(item =>
                    {
                        var when = item.UserData!.LastPlayedDate!.Value.LocalDateTime;
                        return $"{item.DisplayName} • {when:g}";
                    })
                    .ToList();

                if (recentLines.Count == 0)
                    recentLines.Add(_settings.NoPlaybackText);

                var countText = history.Count == 1
                    ? $"1 item in {_settings.HistoryRangeCaption}"
                    : $"{history.Count} items in {_settings.HistoryRangeCaption}";

                return new UserPlaybackSummary
                {
                    Id = user.Id,
                    Name = user.Name,
                    IsNowPlaying = playing is not null,
                    NowPlayingTitle = playing?.MediaDisplay ?? "",
                    NowPlayingDetail = playing is null
                        ? ""
                        : JoinParts(
                            playing.PlaybackMethod,
                            playing.DeviceDisplay,
                            playing.ProgressText),
                    NowPlayingProgress = playing?.Progress ?? 0,
                    ActivityCountText = countText,
                    RecentHeading = playing is null ? "Recently watched" : "Before that",
                    RecentLine1 = recentLines.ElementAtOrDefault(0) ?? "",
                    RecentLine2 = recentLines.ElementAtOrDefault(1) ?? "",
                    RecentLine3 = recentLines.ElementAtOrDefault(2) ?? ""
                };
            });

            var summaries = (await Task.WhenAll(summaryTasks))
                .OrderByDescending(u => u.IsNowPlaying)
                .ThenBy(u => u.Name)
                .ToList();

            UsersView.ItemsSource = summaries;

            var activeCount = summaries.Count(u => u.IsNowPlaying);
            StatusLabel.Text = activeCount > 0
                ? $"{summaries.Count} Emby user{(summaries.Count == 1 ? "" : "s")} • {activeCount} playing now"
                : $"{summaries.Count} Emby user{(summaries.Count == 1 ? "" : "s")}";

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
        string.Join(
            "|",
            _settings.AuthenticatedUserId,
            _settings.Mode,
            _settings.LocalUrl,
            _settings.RemoteUrl,
            _settings.HistoryRange);

    private async void UserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not UserPlaybackSummary user)
            return;

        UsersView.SelectedItem = null;

        await Shell.Current.GoToAsync(
            $"{nameof(UserActivityPage)}?userId={Uri.EscapeDataString(user.Id)}&userName={Uri.EscapeDataString(user.Name)}");
    }

    private static string JoinParts(params string?[] parts) =>
        string.Join(
            " • ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
