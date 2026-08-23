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
        StatusLabel.Text = "Loading users…";
        try
        {
            var users = (await _api.GetUsersAsync()).Items
                .Where(u => u.Policy?.IsDisabled != true)
                .OrderBy(u => u.Name)
                .ToList();
            UsersView.ItemsSource = users;
            StatusLabel.Text = users.Count == 1 ? "1 Emby user" : $"{users.Count} Emby users";
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
        if (e.CurrentSelection.FirstOrDefault() is not UserDto user) return;
        UsersView.SelectedItem = null;
        await Shell.Current.GoToAsync($"{nameof(UserActivityPage)}?userId={Uri.EscapeDataString(user.Id)}&userName={Uri.EscapeDataString(user.Name)}");
    }
}
