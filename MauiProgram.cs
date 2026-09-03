using EAVdrop.Services;
using Microsoft.Maui.LifecycleEvents;

namespace EAVdrop;

public static class MauiProgram
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(_ => { })
            .ConfigureLifecycleEvents(events =>
            {
#if WINDOWS
                events.AddWindows(windows => windows.OnWindowCreated(window =>
                {
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
                    if (File.Exists(iconPath))
                        window.AppWindow.SetIcon(iconPath);
                }));
#endif
            });

        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<EmbyApiClient>();

        var app = builder.Build();
        Services = app.Services;
        return app;
    }
}
