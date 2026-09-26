using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using EAVdrop.Services;
using Microsoft.Maui.ApplicationModel;

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
    private bool _exitPromptShowing;

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null &&
            e.Action == KeyEventActions.Down &&
            e.RepeatCount == 0 &&
            (e.KeyCode == Keycode.Back || e.KeyCode == Keycode.Escape))
        {
            // First Back/Escape from page content is a shortcut straight to
            // the persistent TV tab bar, no matter how far down a list we are.
            if (!TvNavigation.IsNavigationFocused(CurrentFocus) &&
                TvNavigation.FocusActiveTab())
            {
                return true;
            }

            // If focus is already on the tab bar, Back/Escape means "leave
            // EAVdrop" — but ask first instead of exiting immediately.
            if (TvNavigation.IsNavigationFocused(CurrentFocus))
            {
                ShowExitPrompt();
                return true;
            }
        }

        return base.DispatchKeyEvent(e);
    }

    private void ShowExitPrompt()
    {
        if (_exitPromptShowing)
            return;

        _exitPromptShowing = true;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var shell = Shell.Current;
                if (shell is null)
                {
                    Finish();
                    return;
                }

                var exit = await shell.DisplayAlert(
                    "Exit EAVdrop?",
                    "Do you want to exit the app?",
                    "Exit",
                    "Cancel");

                if (exit)
                    Finish();
            }
            finally
            {
                _exitPromptShowing = false;
            }
        });
    }
}
