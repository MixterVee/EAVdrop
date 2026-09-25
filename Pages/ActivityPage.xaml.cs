using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class ActivityPage : ContentPage
{
    private static readonly TimeSpan AutoRefreshAge = TimeSpan.FromMinutes(1);

    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;

    private List<ActivityFeedItem> _all = [];
    private List<UserFilterItem> _users = [];
    private ActivityFilter _activityFilter = ActivityFilter.All;

    private bool _loading;
    private bool _hasLoaded;
    private bool _suppressFilterChanged;
    private string _loadedContextKey = "";
    private DateTimeOffset? _lastLoadedAt;

    public ActivityPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
        UpdateQuickFilterButtons();
        TvNavigation.Attach(this, "activity");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var stale = !_lastLoadedAt.HasValue ||
                    DateTimeOffset.UtcNow - _lastLoadedAt.Value >= AutoRefreshAge;

        await LoadAsync(force: stale);
    }

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadAsync(force: true);

    private void FilterChanged(object sender, EventArgs e)
    {
        if (!_suppressFilterChanged)
            ApplyFilter();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter();

    private void QuickFilterClicked(object sender, EventArgs e)
    {
        if (sender is not Button button ||
            button.CommandParameter is not string raw ||
            !Enum.TryParse<ActivityFilter>(raw, out var filter))
            return;

        _activityFilter = filter;
        UpdateQuickFilterButtons();
        ApplyFilter();
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
                $"Playback activity — {_settings.HistoryRangeCaption}";
            UpdateLastUpdatedText();
            ApplyFilter();
            return;
        }

        _loading = true;
        var canKeepExistingOnFailure =
            _hasLoaded &&
            string.Equals(_loadedContextKey, contextKey, StringComparison.Ordinal);

        RangeCaptionLabel.Text =
            $"Playback activity — {_settings.HistoryRangeCaption}";
        StatusLabel.Text = "Loading live sessions and playback history…";

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

            var historyTasks = users.Select(async user =>
            {
                var items =
                    await _api.GetPlaybackHistoryItemsAsync(user.Id, cutoff);

                return items
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

            var history = (await Task.WhenAll(historyTasks))
                .SelectMany(x => x)
                .ToList();

            var live = sessions
                .Where(s => s.NowPlayingItem is not null)
                .Select(s => new ActivityFeedItem
                {
                    UserId = s.UserId ?? "",
                    UserName = string.IsNullOrWhiteSpace(s.UserName)
                        ? "Unknown user"
                        : s.UserName,
                    Title = s.MediaDisplay,
                    Type = s.NowPlayingItem?.Type ?? "Media",
                    SortDate = s.LastActivityDate ?? DateTimeOffset.UtcNow,
                    IsNowPlaying = true,
                    DeviceDisplay = s.DeviceDisplay,
                    PlaybackMethod = s.PlaybackMethod,
                    StreamDetails = s.StreamDetails,
                    QualityDisplay = string.Join(" • ", s.QualityBadges),
                    ProgressText = s.ProgressText,
                    Progress = s.Progress
                })
                .ToList();

            _all = live
                .Concat(history)
                .OrderByDescending(x => x.IsNowPlaying)
                .ThenByDescending(x => x.SortDate)
                .ToList();

            var previousUserId =
                (UserPicker.SelectedItem as UserFilterItem)?.Id ?? "";

            _users =
            [
                new UserFilterItem("", "All users"),
                .. users.Select(u => new UserFilterItem(u.Id, u.Name))
            ];

            _suppressFilterChanged = true;
            try
            {
                UserPicker.ItemsSource = _users;
                UserPicker.ItemDisplayBinding =
                    new Binding(nameof(UserFilterItem.Name));

                UserPicker.SelectedItem =
                    _users.FirstOrDefault(u =>
                        string.Equals(
                            u.Id,
                            previousUserId,
                            StringComparison.OrdinalIgnoreCase))
                    ?? _users[0];
            }
            finally
            {
                _suppressFilterChanged = false;
            }

            _loadedContextKey = contextKey;
            _hasLoaded = true;
            _lastLoadedAt = DateTimeOffset.UtcNow;

            UpdateLastUpdatedText();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (!canKeepExistingOnFailure)
                ActivityView.ItemsSource = null;

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

    private void ApplyFilter()
    {
        var selected = UserPicker.SelectedItem as UserFilterItem;
        var search = SearchBox.Text?.Trim() ?? "";

        IEnumerable<ActivityFeedItem> query = _all;

        if (selected is not null &&
            !string.IsNullOrWhiteSpace(selected.Id))
        {
            query = query.Where(a =>
                string.Equals(
                    a.UserId,
                    selected.Id,
                    StringComparison.OrdinalIgnoreCase));
        }

        query = _activityFilter switch
        {
            ActivityFilter.PlayingNow =>
                query.Where(a => a.IsNowPlaying),

            ActivityFilter.Movies =>
                query.Where(a =>
                    string.Equals(
                        a.Type,
                        "Movie",
                        StringComparison.OrdinalIgnoreCase)),

            ActivityFilter.Episodes =>
                query.Where(a =>
                    string.Equals(
                        a.Type,
                        "Episode",
                        StringComparison.OrdinalIgnoreCase)),

            ActivityFilter.Music =>
                query.Where(a =>
                    string.Equals(
                        a.Type,
                        "Audio",
                        StringComparison.OrdinalIgnoreCase)),

            _ => query
        };

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => a.MatchesSearch(search));

        var list = query
            .OrderByDescending(x => x.IsNowPlaying)
            .ThenByDescending(x => x.SortDate)
            .ToList();

        ActivityView.ItemsSource = list;

        var liveCount = list.Count(x => x.IsNowPlaying);
        var filterName = GetFilterDisplayName(_activityFilter);

        StatusLabel.Text = _activityFilter == ActivityFilter.PlayingNow
            ? liveCount == 1
                ? "1 session playing now"
                : $"{liveCount} sessions playing now"
            : $"Showing {list.Count} of {_all.Count} items • {filterName}";

        EmptyLabel.Text = GetEmptyText(search);
    }

    private string GetEmptyText(string search)
    {
        if (!string.IsNullOrWhiteSpace(search))
            return $"No activity matches “{search}”.";

        return _activityFilter switch
        {
            ActivityFilter.PlayingNow => "Nobody is playing anything right now.",
            ActivityFilter.Movies => $"No movies found in {_settings.HistoryRangeCaption}.",
            ActivityFilter.Episodes => $"No episodes found in {_settings.HistoryRangeCaption}.",
            ActivityFilter.Music => $"No music found in {_settings.HistoryRangeCaption}.",
            _ => $"No playback activity found in {_settings.HistoryRangeCaption}."
        };
    }

    private void UpdateLastUpdatedText()
    {
        if (!_lastLoadedAt.HasValue)
        {
            LastUpdatedLabel.Text = "Not refreshed yet";
            return;
        }

        LastUpdatedLabel.Text =
            $"Updated {_lastLoadedAt.Value.ToLocalTime():g} • refreshes automatically after 1 minute";
    }

    private void UpdateQuickFilterButtons()
    {
        var buttons = new (Button Button, ActivityFilter Filter, string Text)[]
        {
            (AllFilterButton, ActivityFilter.All, "All"),
            (PlayingFilterButton, ActivityFilter.PlayingNow, "Playing Now"),
            (MoviesFilterButton, ActivityFilter.Movies, "Movies"),
            (EpisodesFilterButton, ActivityFilter.Episodes, "Episodes"),
            (MusicFilterButton, ActivityFilter.Music, "Music")
        };

        foreach (var entry in buttons)
        {
            var selected = entry.Filter == _activityFilter;
            entry.Button.Text = selected
                ? $"✓ {entry.Text}"
                : entry.Text;
            entry.Button.Opacity = selected ? 1.0 : 0.65;
        }
    }

    private static string GetFilterDisplayName(ActivityFilter filter) =>
        filter switch
        {
            ActivityFilter.PlayingNow => "Playing Now",
            ActivityFilter.Movies => "Movies",
            ActivityFilter.Episodes => "Episodes",
            ActivityFilter.Music => "Music",
            _ => "All"
        };

    private sealed record UserFilterItem(string Id, string Name);

    private enum ActivityFilter
    {
        All,
        PlayingNow,
        Movies,
        Episodes,
        Music
    }
}
