namespace EAVdrop.Services;

public static class TvNavigation
{
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
        if (DeviceInfo.Current.Idiom != DeviceIdiom.TV || page.Content is null)
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
            ColumnSpacing = 4,
            Padding = new Thickness(8, 6)
        };

        var buttons = new List<Button>();
        var selectedIndex = -1;

        for (var i = 0; i < Items.Length; i++)
        {
            navGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var item = Items[i];
            var route = item.Route;
            var selected = string.Equals(route, currentRoute, StringComparison.OrdinalIgnoreCase);
            if (selected)
                selectedIndex = i;

            var button = new Button
            {
                Text = item.Title,
                FontSize = 14,
                FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None,
                TextColor = selected ? Colors.White : Color.FromArgb("#CBD5E1"),
                BackgroundColor = selected ? Color.FromArgb("#0891B2") : Colors.Transparent,
                BorderColor = selected ? Color.FromArgb("#22D3EE") : Colors.Transparent,
                BorderWidth = selected ? 2 : 0,
                CornerRadius = 8,
                Padding = new Thickness(4, 8),
                MinimumHeightRequest = 44,
                HorizontalOptions = LayoutOptions.Fill
            };

            button.Focused += (_, _) =>
            {
                button.BackgroundColor = Color.FromArgb("#0891B2");
                button.BorderColor = Color.FromArgb("#67E8F9");
                button.BorderWidth = 2;
                button.TextColor = Colors.White;
                button.FontAttributes = FontAttributes.Bold;
            };

            button.Unfocused += (_, _) =>
            {
                button.BackgroundColor = selected ? Color.FromArgb("#0891B2") : Colors.Transparent;
                button.BorderColor = selected ? Color.FromArgb("#22D3EE") : Colors.Transparent;
                button.BorderWidth = selected ? 2 : 0;
                button.TextColor = selected ? Colors.White : Color.FromArgb("#CBD5E1");
                button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
            };

            // Keep normal MAUI click support for touch/mouse.
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
        navGrid.Loaded += (_, _) =>
        {
            var nativeButtons = new List<Android.Views.View>();

            foreach (var button in buttons)
            {
                if (button.Handler?.PlatformView is not Android.Views.View nativeButton)
                    continue;

                if (nativeButton.Id == Android.Views.View.NoId)
                    nativeButton.Id = Android.Views.View.GenerateViewId();

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

                // This handler is the important part for TV:
                // LEFT/RIGHT and SELECT are all handled on the same focused
                // native view. If LEFT/RIGHT reaches us, SELECT will too.
                nativeButton.KeyPress += (_, e) =>
                {
                    if (e.Event?.Action != Android.Views.KeyEventActions.Down ||
                        e.Event.RepeatCount != 0)
                        return;

                    if (e.KeyCode == Android.Views.Keycode.DpadLeft ||
                        e.KeyCode == Android.Views.Keycode.DpadRight)
                    {
                        var delta = e.KeyCode == Android.Views.Keycode.DpadLeft ? -1 : 1;
                        var targetIndex =
                            (buttonIndex + delta + nativeButtons.Count) % nativeButtons.Count;

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
                        if (!string.Equals(route, currentRoute, StringComparison.OrdinalIgnoreCase))
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
                        var next = nativeButton.FocusSearch(Android.Views.FocusSearchDirection.Up);
                        if (next is not null && !nativeButtons.Contains(next))
                        {
                            next.RequestFocus();
                            e.Handled = true;
                        }
                    }
                };
            }
        };

        // Hidden Shell pages can be created/loaded before they are visible.
        // Do NOT request focus from Loaded. Request it only when this page
        // actually becomes the active page, after Android has finished laying it out.
        page.Appearing += (_, _) =>
        {
            var index = selectedIndex;
            if (index < 0 || index >= buttons.Count)
                return;

            page.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () =>
            {
                var button = buttons[index];

                if (button.Handler?.PlatformView is Android.Views.View nativeButton)
                    nativeButton.RequestFocus();
                else
                    button.Focus();
            });
        };
#endif

        var navBorder = new Border
        {
            BackgroundColor = Color.FromArgb("#0F172A"),
            Stroke = Color.FromArgb("#334155"),
            StrokeThickness = 1,
            Margin = new Thickness(12, 0, 12, 12),
            Padding = 0,
            Content = navGrid
        };

        Grid.SetRow(navBorder, 1);
        root.Children.Add(navBorder);
        page.Content = root;
    }
}
