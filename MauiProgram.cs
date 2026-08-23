using EAVdrop.Services;

namespace EAVdrop;

public static class MauiProgram
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(_ => { });

        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<EmbyApiClient>();

        var app = builder.Build();
        Services = app.Services;
        return app;
    }
}
