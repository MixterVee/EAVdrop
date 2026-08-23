using EAVdrop.Models;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class ActivityPage : ContentPage
{
    private readonly EmbyApiClient _api;
    private List<ActivityLogEntryDto> _all = [];
    private List<UserFilterItem> _users = [];
    private bool _loading;

    public ActivityPage()
    {
        InitializeComponent();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_all.Count == 0)
            await LoadAsync();
    }

    private async void RefreshClicked(object sender, EventArgs e) => await LoadAsync();
    private void FilterChanged(object sender, EventArgs e) => ApplyFilter();
    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        StatusLabel.Text = "Loading activity…";

        try
        {
            var usersTask = _api.GetUsersAsync();
            var activityTask = _api.GetActivityAsync(250);
            await Task.WhenAll(usersTask, activityTask);

            var users = (await usersTask).Items;
            var lookup = users.ToDictionary(u => u.Id, u => u.Name, StringComparer.OrdinalIgnoreCase);

            _all = (await activityTask).Items
                .OrderByDescending(a => a.Date)
                .ToList();

            foreach (var entry in _all)
                entry.UserName = !string.IsNullOrWhiteSpace(entry.UserId) && lookup.TryGetValue(entry.UserId, out var name) ? name : "System";

            _users = [new UserFilterItem("", "All users"), .. users.OrderBy(u => u.Name).Select(u => new UserFilterItem(u.Id, u.Name))];
            UserPicker.ItemsSource = _users;
            UserPicker.ItemDisplayBinding = new Binding(nameof(UserFilterItem.Name));
            UserPicker.SelectedIndex = 0;
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
        if (_all.Count == 0) return;

        var selected = UserPicker.SelectedItem as UserFilterItem;
        var search = SearchBox.Text?.Trim();

        IEnumerable<ActivityLogEntryDto> query = _all;
        if (selected is not null && !string.IsNullOrWhiteSpace(selected.Id))
            query = query.Where(a => string.Equals(a.UserId, selected.Id, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(a =>
                (a.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (a.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (a.Type?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = query.ToList();
        ActivityView.ItemsSource = list;
        StatusLabel.Text = $"Showing {list.Count} of {_all.Count} recent entries";
    }

    private sealed record UserFilterItem(string Id, string Name);
}
