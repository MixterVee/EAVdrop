using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class UsersPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;

    private List<UserPlaybackSummary> _summaries = [];
    private bool _favoritesOnly;
    private bool _loading;
    private bool _hasLoaded;
    private string _loadedContextKey = "";

    public UsersPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
        UpdateFavoritesButton();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync(force: false);
    }

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadAsync(force: true);

    private void FavoritesOnlyClicked(object sender, EventArgs e)
    {
        _favoritesOnly = !_favoritesOnly;
        UpdateFavoritesButton();
        ApplyVisibleUsers();
    }

    private void FavoriteClicked(object sender, EventArgs e)
    {
        if (sender is not Button button ||
            button.CommandParameter is not UserPlaybackSummary user)
            return;

        user.IsFavorite = _settings.ToggleFavoriteUser(user.Id);
        ApplyVisibleUsers();
    }

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

            RefreshFavoriteFlags();
            ApplyVisibleUsers();
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
            var favorites = _settings.FavoriteUserIds
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
                    IsFavorite = favorites.Contains(user.Id),
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

            _summaries = (await Task.WhenAll(summaryTasks)).ToList();

            _loadedContextKey = contextKey;
            _hasLoaded = true;
            ApplyVisibleUsers();
        }
        catch (Exception ex)
        {
            if (!keepExistingOnFailure)
            {
                _summaries = [];
                UsersView.ItemsSource = null;
            }

            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshFavoriteFlags()
    {
        var favorites = _settings.FavoriteUserIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var user in _summaries)
            user.IsFavorite = favorites.Contains(user.Id);
    }

    private void ApplyVisibleUsers()
    {
        IEnumerable<UserPlaybackSummary> query = _summaries;

        if (_favoritesOnly)
            query = query.Where(u => u.IsFavorite);

        var visible = query
            .OrderByDescending(u => u.IsFavorite)
            .ThenByDescending(u => u.IsNowPlaying)
            .ThenBy(u => u.Name)
            .ToList();

        UsersView.ItemsSource = visible;

        var favoriteCount = _summaries.Count(u => u.IsFavorite);
        var activeCount = _summaries.Count(u => u.IsNowPlaying);

        if (_favoritesOnly)
        {
            StatusLabel.Text = activeCount > 0
                ? $"Showing {visible.Count} favorite user{(visible.Count == 1 ? "" : "s")} • {activeCount} playing now"
                : $"Showing {visible.Count} favorite user{(visible.Count == 1 ? "" : "s")}";

            EmptyLabel.Text =
                "No favorite users yet. Turn off Favorites only, then tap ☆ beside a user.";
        }
        else
        {
            StatusLabel.Text = activeCount > 0
                ? $"{_summaries.Count} Emby user{(_summaries.Count == 1 ? "" : "s")} • {favoriteCount} favorite{(favoriteCount == 1 ? "" : "s")} • {activeCount} playing now"
                : $"{_summaries.Count} Emby user{(_summaries.Count == 1 ? "" : "s")} • {favoriteCount} favorite{(favoriteCount == 1 ? "" : "s")}";

            EmptyLabel.Text = "No Emby users to show.";
        }
    }

    private void UpdateFavoritesButton()
    {
        FavoritesOnlyButton.Text =
            _favoritesOnly
                ? "★ Favorites only"
                : "☆ Favorites only";

        FavoritesOnlyButton.Opacity =
            _favoritesOnly ? 1.0 : 0.7;
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
