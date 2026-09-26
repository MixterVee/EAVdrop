namespace EAVdrop.Services;

public static class TvNavigation
{
#if ANDROID
    private static WeakReference<Android.Views.View>? _activeTabButton;

    public static bool IsNavigationFocused(Android.Views.View? view)
    {
        var current = view;

        while (current is not null)
        {
            if (string.Equals(
                    current.Tag?.ToString(),
                    "EAVdropTvNav",
                    StringComparison.Ordinal))
                return true;

            current = current.Parent as Android.Views.View;
        }

        return false;
    }

    public static bool FocusActiveTab()
    {
        if (_activeTabButton is null ||
            !_activeTabButton.TryGetTarget(out var button))
            return false;

        return button.RequestFocus();
    }
#endif

    private static readonly (string Title, string Route)[] Items =
    [
        ("Dashboard", "dashboard"),
        ("Activity", "activity"),
        ("Users", "users"),
        ("Devices", "devices"),
        ("Sync'EM", "sync"),
        ("Settings", "settings")
    ];

    public static void Attach(ContentPage page, string currentRoute)
    {
        var idiom = DeviceInfo.Current.Idiom;
        var isTv = idiom == DeviceIdiom.TV;
        var isPhone = idiom == DeviceIdiom.Phone;

        // Desktop keeps the native Shell tabs. Android TV gets D-pad-aware
        // navigation and phones get a compact six-item touch bar so MAUI does
        // not collapse the sixth tab into "More".
        if ((!isTv && !isPhone) || page.Content is null)
            return;

        Shell.SetTabBarIsVisible(page, false);

        var originalContent = page.Content;
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            }
        };

        Grid.SetRow(originalContent, 0);
        root.Children.Add(originalContent);

        var navGrid = new Grid
        {
            ColumnSpacing = isTv ? 4 : 0,
            Padding = isTv
                ? new Thickness(8, 6)
                : new Thickness(2, 4)
        };

        var buttons = new List<Button>();
        var selectedIndex = -1;

        for (var i = 0; i < Items.Length; i++)
        {
            navGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var item = Items[i];
            var route = item.Route;
            var selected = string.Equals(
                route,
                currentRoute,
                StringComparison.OrdinalIgnoreCase);

            if (selected)
                selectedIndex = i;

            var button = new Button
            {
                Text = item.Title,
                FontSize = isTv ? 14 : 10,
                FontAttributes = selected
                    ? FontAttributes.Bold
                    : FontAttributes.None,
                BackgroundColor = selected
                    ? Color.FromArgb(isTv ? "#1E293B" : "#164E63")
                    : Colors.Transparent,
                BorderColor = Colors.Transparent,
                BorderWidth = 0,
                CornerRadius = isTv ? 8 : 6,
                Padding = isTv
                    ? new Thickness(4, 8)
                    : new Thickness(1, 6),
                MinimumHeightRequest = isTv ? 44 : 46,
                MinimumWidthRequest = 0,
                HorizontalOptions = LayoutOptions.Fill,
                LineBreakMode = LineBreakMode.NoWrap
            };

            if (selected)
            {
                button.TextColor = Colors.White;
            }
            else
            {
                button.SetDynamicResource(
                    Button.TextColorProperty,
                    "EavTabUnselected");
            }

            if (isTv)
            {
                button.Focused += (_, _) =>
                {
                    button.BackgroundColor = Color.FromArgb("#22D3EE");
                    button.BorderColor = Colors.White;
                    button.BorderWidth = 2;
                    button.TextColor = Color.FromArgb("#0F172A");
                    button.FontAttributes = FontAttributes.Bold;
                };

                button.Unfocused += (_, _) =>
                {
                    button.BackgroundColor = selected
                        ? Color.FromArgb("#1E293B")
                        : Colors.Transparent;
                    button.BorderColor = Colors.Transparent;
                    button.BorderWidth = 0;

                    if (selected)
                        button.TextColor = Colors.White;
                    else
                        button.SetDynamicResource(
                            Button.TextColorProperty,
                            "EavTabUnselected");

                    button.FontAttributes = selected
                        ? FontAttributes.Bold
                        : FontAttributes.None;
                };
            }

            button.Clicked += async (_, _) =>
            {
                if (!selected)
                    await Shell.Current.GoToAsync($"//{route}");
            };

            Grid.SetColumn(button, i);
            navGrid.Children.Add(button);
            buttons.Add(button);
        }

#if ANDROID
        if (isTv)
        {
            navGrid.Loaded += (_, _) =>
            {
                var nativeButtons = new List<Android.Views.View>();

                foreach (var button in buttons)
                {
                    if (button.Handler?.PlatformView is not Android.Views.View nativeButton)
                        continue;

                    if (nativeButton.Id == Android.Views.View.NoId)
                        nativeButton.Id = Android.Views.View.GenerateViewId();

                    nativeButton.Tag = new Java.Lang.String("EAVdropTvNav");
                    nativeButton.Focusable = true;
                    nativeButtons.Add(nativeButton);
                }

                for (var i = 0; i < nativeButtons.Count; i++)
                {
                    var nativeButton = nativeButtons[i];
                    var buttonIndex = i;
                    var route = Items[i].Route;

                    nativeButton.NextFocusLeftId =
                        nativeButtons[(i - 1 + nativeButtons.Count) % nativeButtons.Count].Id;
                    nativeButton.NextFocusRightId =
                        nativeButtons[(i + 1) % nativeButtons.Count].Id;

                    nativeButton.KeyPress += (_, e) =>
                    {
                        if (e.Event?.Action != Android.Views.KeyEventActions.Down ||
                            e.Event.RepeatCount != 0)
                            return;

                        if (e.KeyCode == Android.Views.Keycode.DpadLeft ||
                            e.KeyCode == Android.Views.Keycode.DpadRight)
                        {
                            var delta = e.KeyCode == Android.Views.Keycode.DpadLeft
                                ? -1
                                : 1;
                            var targetIndex =
                                (buttonIndex + delta + nativeButtons.Count) %
                                nativeButtons.Count;

                            nativeButtons[targetIndex].RequestFocus();
                            e.Handled = true;
                            return;
                        }

                        if (e.KeyCode == Android.Views.Keycode.DpadCenter ||
                            e.KeyCode == Android.Views.Keycode.Enter ||
                            e.KeyCode == Android.Views.Keycode.NumpadEnter ||
                            e.KeyCode == Android.Views.Keycode.ButtonA ||
                            e.KeyCode == Android.Views.Keycode.ButtonSelect)
                        {
                            if (!string.Equals(
                                    route,
                                    currentRoute,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                var targetRoute = route;
                                MainThread.BeginInvokeOnMainThread(async () =>
                                    await Shell.Current.GoToAsync($"//{targetRoute}"));
                            }

                            e.Handled = true;
                            return;
                        }

                        if (e.KeyCode == Android.Views.Keycode.DpadUp)
                        {
                            var next = nativeButton.FocusSearch(
                                Android.Views.FocusSearchDirection.Up);

                            if (next is not null &&
                                !nativeButtons.Contains(next))
                            {
                                next.RequestFocus();
                                e.Handled = true;
                            }
                        }
                    };
                }
            };

            // Hidden Shell pages can be created before they are visible.
            // Give focus only when the actual page appears.
            page.Appearing += (_, _) =>
            {
                var index = selectedIndex;
                if (index < 0 || index >= buttons.Count)
                    return;

                page.Dispatcher.DispatchDelayed(
                    TimeSpan.FromMilliseconds(200),
                    () =>
                    {
                        var button = buttons[index];

                        if (button.Handler?.PlatformView is Android.Views.View nativeButton)
                        {
                            _activeTabButton =
                                new WeakReference<Android.Views.View>(nativeButton);
                            nativeButton.RequestFocus();
                        }
                        else
                        {
                            button.Focus();
                        }
                    });
            };
        }
#endif

        var navBorder = new Border
        {
            StrokeThickness = 1,
            Margin = isTv
                ? new Thickness(12, 0, 12, 12)
                : new Thickness(0),
            Padding = 0,
            Content = navGrid
        };

        if (isTv)
        {
            navBorder.BackgroundColor = Color.FromArgb("#0F172A");
            navBorder.Stroke = Color.FromArgb("#334155");
        }
        else
        {
            navBorder.SetDynamicResource(
                Border.BackgroundColorProperty,
                "EavTabBarBackground");
            navBorder.SetDynamicResource(
                Border.StrokeProperty,
                "EavCardStroke");
        }

        Grid.SetRow(navBorder, 1);
        root.Children.Add(navBorder);
        page.Content = root;
    }
}
