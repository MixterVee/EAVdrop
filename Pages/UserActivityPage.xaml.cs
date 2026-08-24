using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class UserActivityPage : ContentPage, IQueryAttributable
{
    private readonly EmbyApiClient _api;
    private string _userId = "";
    private string _userName = "User";

    public UserActivityPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("userId", out var id)) _userId = Uri.UnescapeDataString(id?.ToString() ?? "");
        if (query.TryGetValue("userName", out var name)) _userName = Uri.UnescapeDataString(name?.ToString() ?? "User");
        UserTitle.Text = _userName;
        Title = _userName;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_userId)) return;
        StatusLabel.Text = "Loading 30-day playback history…";

        try
        {
            var sessionsTask = _api.GetSessionsAsync();
            var recentPlayedTask = _api.GetRecentPlayedItemsAsync(_userId, 500);
            await Task.WhenAll(sessionsTask, recentPlayedTask);

            var cutoff = DateTimeOffset.Now.AddDays(-30);
            var playback = (await recentPlayedTask).Items
                .Select(item => new { Item = item, Date = item.UserData?.LastPlayedDate })
                .Where(x => x.Date.HasValue && x.Date.Value >= cutoff)
                .Select(x => new PlaybackHistoryItem
                {
                    UserId = _userId,
                    UserName = _userName,
                    Title = x.Item.DisplayName,
                    Type = x.Item.Type ?? "Media",
                    PlayedDate = x.Date!.Value
                })
                .OrderByDescending(x => x.PlayedDate)
                .ToList();

            var playing = (await sessionsTask)
                .FirstOrDefault(s => string.Equals(s.UserId, _userId, StringComparison.OrdinalIgnoreCase) && s.NowPlayingItem is not null);

            if (playing is not null)
            {
                SummaryTitleLabel.Text = "Now Playing";
                SummaryMainLabel.Text = playing.MediaDisplay;
                SummaryDetailLabel.Text = JoinParts(playing.PlaybackMethod, playing.DeviceDisplay, playing.ProgressText);
            }
            else if (playback.FirstOrDefault() is PlaybackHistoryItem lastPlayed)
            {
                SummaryTitleLabel.Text = "Last Played";
                SummaryMainLabel.Text = lastPlayed.Title;
                SummaryDetailLabel.Text = lastPlayed.DateDisplay;
            }
            else
            {
                SummaryTitleLabel.Text = "Last Played";
                SummaryMainLabel.Text = "Nothing played in the last 30 days.";
                SummaryDetailLabel.Text = "";
            }

            ActivityView.ItemsSource = playback;
            StatusLabel.Text = playback.Count == 1
                ? "1 item played in the last 30 days"
                : $"{playback.Count} items played in the last 30 days";
        }
        catch (Exception ex)
        {
            ActivityView.ItemsSource = null;
            StatusLabel.Text = ex.Message;
        }
    }

    private static string JoinParts(params string?[] parts) =>
        string.Join(" • ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
