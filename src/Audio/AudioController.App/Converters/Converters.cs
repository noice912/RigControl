using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AudioController.App.Converters;

/// <summary>true → Visible, false → Collapsed (or the reverse when <see cref="Invert"/> is set).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is Visibility.Visible;
        return Invert ? !visible : visible;
    }
}

/// <summary>non-null (and non-empty string) → Visible, null → Collapsed (reversed with <see cref="Invert"/>).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value is not null && !(value is string s && s.Length == 0);
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Count (int or collection) of zero → Visible, otherwise Collapsed. Used for empty-state hints.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int i => i,
            ICollection c => c.Count,
            _ => 0,
        };
        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>"#RRGGBB" → frozen SolidColorBrush (falls back to the accent blue on bad input).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        GetBrush(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    internal static SolidColorBrush GetBrush(string? hex)
    {
        hex ??= "#4F8EF7";
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;

            var brush = new SolidColorBrush(ParseColor(hex));
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
    }

    internal static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch (FormatException)
        {
        }

        return Color.FromRgb(0x4F, 0x8E, 0xF7);
    }
}

/// <summary>Picks a readable text color (near-black or white) for a "#RRGGBB" background.</summary>
public sealed class HexToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Dark = Frozen(Color.FromRgb(0x14, 0x16, 0x1B));
    private static readonly SolidColorBrush Light = Frozen(Colors.White);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var c = HexToBrushConverter.ParseColor(value as string);
        double luminance = (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));
        return luminance > 0.42 ? Dark : Light;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>True when all bound values are equal (case-insensitive for strings). Used for selected swatches.</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        var first = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            var other = values[i];
            bool equal = first is string a && other is string b
                ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                : Equals(first, other);
            if (!equal) return false;
        }

        return true;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
