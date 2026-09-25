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

        for (var i = 0; i < Items.Length; i++)
        {
            navGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var item = Items[i];
            var selected = string.Equals(item.Route, currentRoute, StringComparison.OrdinalIgnoreCase);
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

            button.Clicked += async (_, _) =>
            {
                if (!selected)
                    await Shell.Current.GoToAsync($"//{item.Route}");
            };

            var buttonIndex = i;
            button.HandlerChanged += (_, _) =>
            {
#if ANDROID
                if (button.Handler?.PlatformView is Android.Views.View nativeButton)
                {
                    nativeButton.NextFocusLeftId = buttonIndex > 0
                        ? buttons[buttonIndex - 1].Handler?.PlatformView is Android.Views.View left
                            ? left.Id
                            : Android.Views.View.NoId
                        : Android.Views.View.NoId;
                }
#endif
            };

            Grid.SetColumn(button, i);
            navGrid.Children.Add(button);
            buttons.Add(button);
        }

#if ANDROID
        navGrid.Loaded += (_, _) =>
        {
            var nativeButtons = new List<Android.Views.View>();

            // MAUI-created Android views commonly have View.NoId. Android's
            // nextFocus* APIs require real view IDs, so assign stable runtime IDs
            // before wiring the D-pad focus graph.
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

                // Explicit horizontal focus loop for Android TV remotes.
                nativeButton.NextFocusLeftId =
                    nativeButtons[(i - 1 + nativeButtons.Count) % nativeButtons.Count].Id;
                nativeButton.NextFocusRightId =
                    nativeButtons[(i + 1) % nativeButtons.Count].Id;

                // Do not rely only on Android's geometric focus search. MAUI's
                // nested handler layout can make that inconsistent on TV, so
                // consume LEFT/RIGHT ourselves and move focus directly.
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

                    if (e.KeyCode == Android.Views.Keycode.DpadUp)
                    {
                        // Let the user leave the nav bar and reach the page controls.
                        var next = nativeButton.FocusSearch(Android.Views.FocusSearchDirection.Up);
                        if (next is not null && !nativeButtons.Contains(next))
                        {
                            next.RequestFocus();
                            e.Handled = true;
                            return;
                        }

                        if (originalContent.Handler?.PlatformView is Android.Views.View pageRoot &&
                            pageRoot.RequestFocus(Android.Views.FocusSearchDirection.Up))
                        {
                            e.Handled = true;
                        }
                    }
                };
            }

            // Put initial TV focus on the currently selected destination.
            var selectedIndex = Array.FindIndex(
                Items,
                x => string.Equals(x.Route, currentRoute, StringComparison.OrdinalIgnoreCase));

            if (selectedIndex >= 0 && selectedIndex < nativeButtons.Count)
                nativeButtons[selectedIndex].RequestFocus();
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
