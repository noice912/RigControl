using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RigControl.App.Install;

/// <summary>The setup windows' look: the app's own dark colours (src/ui styles.css), built in code like the overlay.</summary>
internal static class SetupUi
{
    public static readonly Brush Bg = Hex(0x16, 0x1C, 0x23);
    public static readonly Brush Panel = Hex(0x1C, 0x24, 0x2D);
    public static readonly Brush Raised = Hex(0x24, 0x2E, 0x39);
    public static readonly Brush Line = Hex(0x2E, 0x3A, 0x47);
    public static readonly Brush Text = Hex(0xE4, 0xE9, 0xEE);
    public static readonly Brush Muted = Hex(0x95, 0xA3, 0xB2);
    public static readonly Brush Coolant = Hex(0x5C, 0xC6, 0xE8);
    public static readonly Brush CoolantInk = Hex(0x0E, 0x2B, 0x36);
    public static readonly Brush Warm = Hex(0xF0, 0xB4, 0x4C);
    public static readonly Brush Hot = Hex(0xEE, 0x5A, 0x42);
    public static readonly FontFamily Font = new("Segoe UI");

    private static SolidColorBrush Hex(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static Window Window(string title, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 420,
            MinHeight = 300,
            Background = Bg,
            Foreground = Text,
            FontFamily = Font,
            FontSize = 14,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResize,
            ShowInTaskbar = true,
        };
        try
        {
            if (System.Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                w.Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            // Windows' default icon then.
        }
        return w;
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Foreground = Text,
        Margin = new Thickness(0, 0, 0, 10),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Para(string text, Brush? colour = null) => new()
    {
        Text = text,
        Foreground = colour ?? Muted,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };

    public static Button Button(string text, bool primary = false, bool danger = false)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(16, 7, 16, 7));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var b = new Button
        {
            Content = text,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            Background = primary ? Coolant : danger ? Hot : Raised,
            Foreground = primary ? CoolantInk : danger ? Brushes.White : Text,
            BorderBrush = primary ? Coolant : danger ? Hot : Line,
            FontWeight = primary || danger ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 96,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1 : 0.5;
        return b;
    }

    public static CheckBox Tick(string text, bool on) => new()
    {
        Content = new TextBlock { Text = text, Foreground = Text, TextWrapping = TextWrapping.Wrap },
        IsChecked = on,
        Margin = new Thickness(0, 4, 0, 8),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>A window body: the page above, the buttons in a row at the bottom right.</summary>
    public static Grid Frame(UIElement page, params Button[] buttons)
    {
        var grid = new Grid { Margin = new Thickness(28, 24, 28, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(page, 0);
        grid.Children.Add(page);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        Grid.SetRow(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>A box that scrolls long text (the terms).</summary>
    public static Border Scroller(UIElement content) => new()
    {
        Background = Panel,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 10, 6, 10),
        Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) },
    };
}
