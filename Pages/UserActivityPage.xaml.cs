using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class UserActivityPage : ContentPage, IQueryAttributable
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private string _userId = "";
    private string _userName = "User";
    private bool _loading;

    public UserActivityPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("userId", out var id))
            _userId = Uri.UnescapeDataString(id?.ToString() ?? "");

        if (query.TryGetValue("userName", out var name))
            _userName = Uri.UnescapeDataString(name?.ToString() ?? "User");

        UserTitle.Text = _userName;
        Title = _userName;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async void RefreshClicked(object sender, EventArgs e) =>
        await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading || string.IsNullOrWhiteSpace(_userId))
            return;

        _loading = true;
        HistoryRangeLabel.Text =
            $"Playback history — {_settings.HistoryRangeCaption}";
        StatusLabel.Text = "Loading playback history…";

        try
        {
            var cutoff = _settings.GetPlaybackHistoryCutoff();
            var sessionsTask = _api.GetSessionsAsync();
            var playedItemsTask =
                _api.GetPlaybackHistoryItemsAsync(_userId, cutoff);

            await Task.WhenAll(sessionsTask, playedItemsTask);

            var playback = (await playedItemsTask)
                .Where(item => item.UserData?.LastPlayedDate is not null)
                .Select(item => new PlaybackHistoryItem
                {
                    UserId = _userId,
                    UserName = _userName,
                    Title = item.DisplayName,
                    Type = item.Type ?? "Media",
                    PlayedDate = item.UserData!.LastPlayedDate!.Value
                })
                .OrderByDescending(x => x.PlayedDate)
                .ToList();

            var playing = (await sessionsTask)
                .Where(s =>
                    string.Equals(
                        s.UserId,
                        _userId,
                        StringComparison.OrdinalIgnoreCase) &&
                    s.NowPlayingItem is not null)
                .OrderByDescending(s => s.LastActivityDate)
                .FirstOrDefault();

            if (playing is not null)
            {
                SummaryTitleLabel.Text = "NOW PLAYING";
                SummaryMainLabel.Text = playing.MediaDisplay;
                SummaryDetailLabel.Text = JoinParts(
                    playing.PlaybackMethod,
                    playing.DeviceDisplay,
                    playing.ProgressText);
                NowPlayingProgressBar.Progress = playing.Progress;
                NowPlayingProgressBar.IsVisible = true;
            }
            else if (playback.FirstOrDefault() is PlaybackHistoryItem lastPlayed)
            {
                SummaryTitleLabel.Text = "LAST PLAYED";
                SummaryMainLabel.Text = lastPlayed.Title;
                SummaryDetailLabel.Text =
                    $"{lastPlayed.TypeDisplay} • {lastPlayed.DateDisplay}";
                NowPlayingProgressBar.IsVisible = false;
            }
            else
            {
                SummaryTitleLabel.Text = "LAST PLAYED";
                SummaryMainLabel.Text = _settings.NoPlaybackText;
                SummaryDetailLabel.Text = "";
                NowPlayingProgressBar.IsVisible = false;
            }

            ActivityView.ItemsSource = playback;

            StatusLabel.Text = playback.Count == 1
                ? $"1 item from {_settings.HistoryRangeCaption}"
                : $"{playback.Count} items from {_settings.HistoryRangeCaption}";
        }
        catch (Exception ex)
        {
            ActivityView.ItemsSource = null;
            NowPlayingProgressBar.IsVisible = false;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private static string JoinParts(params string?[] parts) =>
        string.Join(
            " • ",
            parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
