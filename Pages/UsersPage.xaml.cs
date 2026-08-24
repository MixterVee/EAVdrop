using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class UsersPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private bool _loading;

    public UsersPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (UsersView.ItemsSource is null)
            await LoadAsync();
    }

    private async void RefreshClicked(object sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
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
            var cutoff = DateTimeOffset.Now.AddDays(-30);

            var summaryTasks = users.Select(async user =>
            {
                var playing = sessions.FirstOrDefault(s =>
                    string.Equals(s.UserId, user.Id, StringComparison.OrdinalIgnoreCase) &&
                    s.NowPlayingItem is not null);

                if (playing is not null)
                {
                    return new UserPlaybackSummary
                    {
                        Id = user.Id,
                        Name = user.Name,
                        PlaybackSummary = $"Now playing {playing.MediaDisplay}"
                    };
                }

                var recent = (await _api.GetRecentPlayedItemsAsync(user.Id, 1)).Items
                    .FirstOrDefault(i => i.UserData?.LastPlayedDate is not null);

                string summary;
                if (recent?.UserData?.LastPlayedDate is DateTimeOffset playedDate && playedDate >= cutoff)
                    summary = $"Last played {recent.DisplayName} • {playedDate.LocalDateTime:g}";
                else
                    summary = "Nothing played in the last 30 days";

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
        }
        catch (Exception ex)
        {
            UsersView.ItemsSource = null;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private async void UserSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not UserPlaybackSummary user) return;
        UsersView.SelectedItem = null;
        await Shell.Current.GoToAsync($"{nameof(UserActivityPage)}?userId={Uri.EscapeDataString(user.Id)}&userName={Uri.EscapeDataString(user.Name)}");
    }
}
