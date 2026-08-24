using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class UsersPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private bool _loading;

    public UsersPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async void RefreshClicked(object sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
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
                var history = (await _api.GetRecentPlayedItemsAsync(user.Id)).Items
                    .Select(item => new { Item = item, Date = item.UserData?.LastPlayedDate })
                    .Where(x => x.Date.HasValue && (!cutoff.HasValue || x.Date.Value >= cutoff.Value))
                    .OrderByDescending(x => x.Date)
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
                    summary = $"{countText} • Last played {recent.Item.DisplayName} • {recent.Date!.Value.LocalDateTime:g}";
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
