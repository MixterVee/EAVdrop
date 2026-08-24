using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsService _settings;
    private readonly EmbyApiClient _api;
    private bool _loaded;

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
        if (_loaded) return;
        LocalUrlEntry.Text = _settings.LocalUrl;
        RemoteUrlEntry.Text = _settings.RemoteUrl;
        ModePicker.SelectedItem = _settings.Mode.ToString();
        HistoryRangePicker.SelectedIndex = (int)_settings.HistoryRange;
        ApiKeyEntry.Text = await _settings.GetApiKeyAsync();
        _loaded = true;
    }

    private async void SaveClicked(object sender, EventArgs e)
    {
        await SaveAsync();
        StatusLabel.Text = "Settings saved.";
    }

    private async void TestClicked(object sender, EventArgs e)
    {
        BusyIndicator.IsVisible = BusyIndicator.IsRunning = true;
        StatusLabel.Text = "Testing connection…";
        try
        {
            await SaveAsync();
            var info = await _api.GetSystemInfoAsync();
            var sessions = await _api.GetSessionsAsync();
            StatusLabel.Text = $"Connected to {info.ServerName ?? "Emby"} {info.Version}. Sessions endpoint OK ({sessions.Count} session records). Using {_api.LastConnectedBaseUrl}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Test failed: {ex.Message}";
        }
        finally
        {
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = false;
        }
    }

    private async Task SaveAsync()
    {
        _settings.LocalUrl = LocalUrlEntry.Text ?? "";
        _settings.RemoteUrl = RemoteUrlEntry.Text ?? "";
        if (Enum.TryParse<ConnectionMode>(ModePicker.SelectedItem?.ToString(), out var mode))
            _settings.Mode = mode;
        if (HistoryRangePicker.SelectedIndex >= 0)
            _settings.HistoryRange = (PlaybackHistoryRange)HistoryRangePicker.SelectedIndex;
        await _settings.SaveApiKeyAsync(ApiKeyEntry.Text ?? "");
    }
}
