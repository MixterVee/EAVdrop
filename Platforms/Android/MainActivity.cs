using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using EAVdrop.Services;

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
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null &&
            e.Action == KeyEventActions.Down &&
            e.RepeatCount == 0 &&
            TvNavigation.TryHandleRemoteSelect(this, e.KeyCode))
        {
            return true;
        }

        return base.DispatchKeyEvent(e);
    }
}
