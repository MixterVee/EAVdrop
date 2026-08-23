using EAVdrop.Pages;
using EAVdrop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EAVdrop;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(UserActivityPage), typeof(UserActivityPage));

        Loaded += async (_, _) =>
        {
            var settings = MauiProgram.Services.GetRequiredService<SettingsService>();
            if (!await settings.HasMinimumConfigurationAsync())
                await GoToAsync("//settings");
        };
    }
}
