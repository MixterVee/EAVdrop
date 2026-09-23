using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace EAVdrop;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    new[] { Intent.ActionMain },
    Categories = new[] { Intent.CategoryLeanbackLauncher })]
public class MainActivity : MauiAppCompatActivity
{
    private bool _showingFatalError;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidEnvironment.UnhandledExceptionRaiser += OnUnhandledException;

        try
        {
            base.OnCreate(savedInstanceState);
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
        }
    }

    protected override void OnDestroy()
    {
        AndroidEnvironment.UnhandledExceptionRaiser -= OnUnhandledException;
        base.OnDestroy();
    }

    private void OnUnhandledException(object? sender, RaiseThrowableEventArgs e)
    {
        // Diagnostic build: keep the process alive long enough to show the actual
        // managed exception instead of silently dropping back to the launcher.
        e.Handled = true;
        RunOnUiThread(() => ShowFatalError(e.Exception));
    }

    private void ShowFatalError(Exception exception)
    {
        if (_showingFatalError) return;
        _showingFatalError = true;

        try
        {
            var details = exception.ToString();

            new AlertDialog.Builder(this)
                .SetTitle("EAVdrop Android error")
                .SetMessage(details)
                .SetPositiveButton("Copy", (_, _) =>
                {
                    try
                    {
                        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                        clipboard?.SetPrimaryClip(ClipData.NewPlainText("EAVdrop error", details));
                    }
                    catch
                    {
                    }

                    _showingFatalError = false;
                })
                .SetNegativeButton("Close", (_, _) => FinishAndRemoveTask())
                .SetCancelable(false)
                .Show();
        }
        catch
        {
            FinishAndRemoveTask();
        }
    }
}
