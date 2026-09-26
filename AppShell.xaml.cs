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
            var eventMonitor = MauiProgram.Services.GetRequiredService<EventMonitorService>();

            if (!await settings.HasMinimumConfigurationAsync())
            {
                eventMonitor.Stop();
                await GoToAsync("//settings");
                return;
            }

            eventMonitor.Start();
        };
    }
}
