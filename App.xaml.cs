namespace EAVdrop;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());

#if WINDOWS
        window.HandlerChanged += (_, _) =>
        {
            try
            {
                if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    var iconPath = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
                    if (File.Exists(iconPath))
                    {
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                        appWindow.SetIcon(iconPath);
                    }
                }
            }
            catch
            {
                // The executable still carries appicon.ico via ApplicationIcon.
            }
        };
#endif

        return window;
    }
}
