using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class ActivityPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private readonly SettingsService _settings;
    private List<PlaybackHistoryItem> _all = [];
    private List<UserFilterItem> _users = [];
    private bool _loading;

    public ActivityPage()
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
    private void FilterChanged(object sender, EventArgs e) => ApplyFilter();
    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        RangeCaptionLabel.Text = $"Playback history — {_settings.HistoryRangeCaption}";
        StatusLabel.Text = "Loading playback history…";

        try
        {
            var users = (await _api.GetUsersAsync()).Items
                .Where(u => u.Policy?.IsDisabled != true)
                .OrderBy(u => u.Name)
                .ToList();

            var cutoff = _settings.GetPlaybackHistoryCutoff();
            var historyTasks = users.Select(async user =>
            {
                var items = (await _api.GetRecentPlayedItemsAsync(user.Id)).Items;
                return items
                    .Select(item => new { Item = item, Date = item.UserData?.LastPlayedDate })
                    .Where(x => x.Date.HasValue && (!cutoff.HasValue || x.Date.Value >= cutoff.Value))
                    .Select(x => new PlaybackHistoryItem
                    {
                        UserId = user.Id,
                        UserName = user.Name,
                        Title = x.Item.DisplayName,
                        Type = x.Item.Type ?? "Media",
                        PlayedDate = x.Date!.Value
                    })
                    .ToList();
            });

            _all = (await Task.WhenAll(historyTasks))
                .SelectMany(x => x)
                .OrderByDescending(x => x.PlayedDate)
                .ToList();

            var previousUserId = (UserPicker.SelectedItem as UserFilterItem)?.Id ?? "";
            _users = [new UserFilterItem("", "All users"), .. users.Select(u => new UserFilterItem(u.Id, u.Name))];
            UserPicker.ItemsSource = _users;
            UserPicker.ItemDisplayBinding = new Binding(nameof(UserFilterItem.Name));
            UserPicker.SelectedItem = _users.FirstOrDefault(u => string.Equals(u.Id, previousUserId, StringComparison.OrdinalIgnoreCase)) ?? _users[0];
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ActivityView.ItemsSource = null;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void ApplyFilter()
    {
        var selected = UserPicker.SelectedItem as UserFilterItem;
        var search = SearchBox.Text?.Trim();

        IEnumerable<PlaybackHistoryItem> query = _all;
        if (selected is not null && !string.IsNullOrWhiteSpace(selected.Id))
            query = query.Where(a => string.Equals(a.UserId, selected.Id, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(a =>
                a.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.Type.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.ToList();
        ActivityView.ItemsSource = list;
        StatusLabel.Text = $"Showing {list.Count} of {_all.Count} items from {_settings.HistoryRangeCaption}";
    }

    private sealed record UserFilterItem(string Id, string Name);
}
