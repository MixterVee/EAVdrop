using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsService _settings;
    private readonly EmbyApiClient _api;

    public SettingsPage()
    {
        InitializeComponent();
        _settings = MauiProgram.Services.GetRequiredService<SettingsService>();
        _api = MauiProgram.Services.GetRequiredService<EmbyApiClient>();
        ModePicker.ItemsSource = Enum.GetNames<ConnectionMode>();
        HistoryRangePicker.ItemsSource = Enum.GetValues<PlaybackHistoryRange>()
            .Select(SettingsService.GetHistoryRangeSettingLabel)
            .ToList();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        LocalUrlEntry.Text = _settings.LocalUrl;
        RemoteUrlEntry.Text = _settings.RemoteUrl;
        ModePicker.SelectedItem = _settings.Mode.ToString();
        HistoryRangePicker.SelectedIndex = (int)_settings.HistoryRange;

        UsernameEntry.Text = _settings.AuthenticatedUserName;
        PasswordEntry.Text = "";
        AccountStatusLabel.Text = "";
        await RefreshSignedInStatusAsync();
    }

    private async void SaveClicked(object sender, EventArgs e)
    {
        SaveSettings();
        StatusLabel.Text = "Settings saved.";
        await Task.CompletedTask;
    }

    private async void SignInClicked(object sender, EventArgs e) => await SignInAsync();

    private async void PasswordCompleted(object sender, EventArgs e) => await SignInAsync();

    private async Task SignInAsync()
    {
        SetBusy(true);
        AccountStatusLabel.Text = "Signing in…";
        StatusLabel.Text = "";

        try
        {
            SaveSettings();

            var result = await _api.AuthenticateAsync(
                UsernameEntry.Text ?? "",
                PasswordEntry.Text ?? "");

            PasswordEntry.Text = "";
            UsernameEntry.Text = result.User?.Name ?? UsernameEntry.Text;
            AccountStatusLabel.Text = $"Signed in successfully using {_api.LastConnectedBaseUrl}";
            StatusLabel.Text = "Emby sign-in successful.";
            await RefreshSignedInStatusAsync();
            await DisplayAlert("Emby sign-in", $"Signed in as {UsernameEntry.Text}.", "OK");
        }
        catch (Exception ex)
        {
            // Keep the password in place on failure so the user can correct a typo
            // without having to re-enter the whole value.
            AccountStatusLabel.Text = $"Sign-in failed: {ex.Message}";
            StatusLabel.Text = AccountStatusLabel.Text;
            await RefreshSignedInStatusAsync();
            await DisplayAlert("Emby sign-in failed", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SignOutClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        AccountStatusLabel.Text = "Signing out…";

        try
        {
            await _api.LogoutAsync();
            UsernameEntry.Text = "";
            PasswordEntry.Text = "";
            AccountStatusLabel.Text = "Signed out.";
            await RefreshSignedInStatusAsync();
        }
        catch (Exception ex)
        {
            AccountStatusLabel.Text = $"Sign out failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestClicked(object sender, EventArgs e)
    {
        SetBusy(true);
        StatusLabel.Text = "Testing connection…";

        try
        {
            SaveSettings();

            if (!await _settings.IsSignedInAsync())
            {
                StatusLabel.Text = "Sign in to Emby first.";
                return;
            }

            var info = await _api.GetSystemInfoAsync();
            var sessions = await _api.GetSessionsAsync();
            StatusLabel.Text = $"Connected to {info.ServerName ?? "Emby"} {info.Version}. Sessions endpoint OK ({sessions.Count} session records). Using {_api.LastConnectedBaseUrl}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Test failed: {ex.Message}";
            await RefreshSignedInStatusAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveSettings()
    {
        _settings.LocalUrl = LocalUrlEntry.Text ?? "";
        _settings.RemoteUrl = RemoteUrlEntry.Text ?? "";

        if (Enum.TryParse<ConnectionMode>(ModePicker.SelectedItem?.ToString(), out var mode))
            _settings.Mode = mode;

        if (HistoryRangePicker.SelectedIndex >= 0)
            _settings.HistoryRange = (PlaybackHistoryRange)HistoryRangePicker.SelectedIndex;
    }

    private async Task RefreshSignedInStatusAsync()
    {
        if (await _settings.IsSignedInAsync())
        {
            var name = string.IsNullOrWhiteSpace(_settings.AuthenticatedUserName)
                ? "Emby user"
                : _settings.AuthenticatedUserName;
            SignedInLabel.Text = $"Signed in as {name}";
        }
        else
        {
            SignedInLabel.Text = "Not signed in";
        }
    }

    private void SetBusy(bool busy)
    {
        BusyIndicator.IsVisible = busy;
        BusyIndicator.IsRunning = busy;
    }
}
