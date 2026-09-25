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

            Grid.SetColumn(button, i);
            navGrid.Children.Add(button);
        }

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
