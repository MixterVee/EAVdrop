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
        StatusLabel.Text = "Loading user activity…";

        try
        {
            var activityTask = _api.GetActivityAsync(400);
            var sessionsTask = _api.GetSessionsAsync();
            var recentPlayedTask = _api.GetRecentPlayedItemsAsync(_userId, 50);
            await Task.WhenAll(activityTask, sessionsTask, recentPlayedTask);

            var activity = (await activityTask).Items
                .Where(a => string.Equals(a.UserId, _userId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Date)
                .ToList();

            var playing = (await sessionsTask)
                .FirstOrDefault(s => string.Equals(s.UserId, _userId, StringComparison.OrdinalIgnoreCase) && s.NowPlayingItem is not null);

            if (playing is not null)
            {
                SummaryTitleLabel.Text = "Now Playing";
                SummaryMainLabel.Text = playing.MediaDisplay;
                SummaryDetailLabel.Text = JoinParts(playing.PlaybackMethod, playing.DeviceDisplay, playing.ProgressText);
            }
            else
            {
                var cutoff = DateTimeOffset.Now.AddDays(-30);
                var lastPlayed = (await recentPlayedTask).Items
                    .Where(i => i.UserData?.LastPlayedDate is not null)
                    .OrderByDescending(i => i.UserData!.LastPlayedDate)
                    .FirstOrDefault();

                SummaryTitleLabel.Text = "Last Played";

                if (lastPlayed?.UserData?.LastPlayedDate is not DateTimeOffset lastPlayedDate || lastPlayedDate < cutoff)
                {
                    SummaryMainLabel.Text = "Nothing played in the last 30 days.";
                    SummaryDetailLabel.Text = "";
                }
                else
                {
                    SummaryMainLabel.Text = lastPlayed.DisplayName;
                    SummaryDetailLabel.Text = lastPlayedDate.LocalDateTime.ToString("g");
                }
            }

            ActivityView.ItemsSource = activity;
            StatusLabel.Text = $"{activity.Count} recent activity entries";
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
