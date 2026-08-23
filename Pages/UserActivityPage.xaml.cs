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
            await Task.WhenAll(activityTask, sessionsTask);

            var activity = (await activityTask).Items
                .Where(a => string.Equals(a.UserId, _userId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Date)
                .ToList();

            var playing = (await sessionsTask)
                .FirstOrDefault(s => string.Equals(s.UserId, _userId, StringComparison.OrdinalIgnoreCase) && s.NowPlayingItem is not null);

            if (playing is null)
            {
                NowPlayingLabel.Text = "Nothing playing";
                NowPlayingDeviceLabel.Text = "";
            }
            else
            {
                NowPlayingLabel.Text = $"{playing.MediaDisplay} — {playing.PlaybackMethod}";
                NowPlayingDeviceLabel.Text = $"{playing.DeviceDisplay} • {playing.ProgressText}";
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
}
