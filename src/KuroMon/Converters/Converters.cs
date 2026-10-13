using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using KuroMon.ViewModels;

namespace KuroMon.Converters;

/// <summary>
/// Status colors. The brushes are shared and recolored in place on a theme change, so every bound
/// element updates at once. Light theme uses darker shades to keep text readable on white (WCAG 4.5:1).
/// Never put these brushes directly into a Style setter or a frozen Pen: that freezes them
/// (use a binding through <see cref="LevelToBrushConverter"/>, or CloneCurrentValue()).
/// </summary>
public static class LevelPalette
{
    public static readonly SolidColorBrush Normal = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    public static readonly SolidColorBrush Warning = new(Color.FromRgb(0xFF, 0xB3, 0x00));
    public static readonly SolidColorBrush Critical = new(Color.FromRgb(0xEF, 0x53, 0x50));
    public static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x9E, 0x9E, 0x9E));

    /// <summary>Numbers in the normal state use the plain text color; only warning / critical values are colored.</summary>
    public static readonly SolidColorBrush TextNormal = new(Color.FromRgb(0xF3, 0xF3, 0xF5));
    public static readonly SolidColorBrush TextUnknown = new(Color.FromRgb(0x85, 0x85, 0x8E));
    /// <summary>Gauge fill in the normal state (neutral, so only problems stand out).</summary>
    public static readonly SolidColorBrush BarNormal = new(Color.FromRgb(0xB4, 0xB4, 0xBC));

    public static void ApplyTheme(bool dark)
    {
        if (Normal.IsFrozen || Warning.IsFrozen || Critical.IsFrozen || Unknown.IsFrozen || TextNormal.IsFrozen || TextUnknown.IsFrozen || BarNormal.IsFrozen)
        {
            Services.AppLog.Warn("Status brushes were frozen; theme colors cannot be updated.");
            return;
        }
        Normal.Color = dark ? Color.FromRgb(0x4C, 0xAF, 0x50) : Color.FromRgb(0x2E, 0x7D, 0x32);
        Warning.Color = dark ? Color.FromRgb(0xFF, 0xB3, 0x00) : Color.FromRgb(0xA1, 0x5C, 0x00);
        Critical.Color = dark ? Color.FromRgb(0xEF, 0x53, 0x50) : Color.FromRgb(0xC6, 0x28, 0x28);
        Unknown.Color = dark ? Color.FromRgb(0x9E, 0x9E, 0x9E) : Color.FromRgb(0x75, 0x75, 0x75);
        TextNormal.Color = dark ? Color.FromRgb(0xF3, 0xF3, 0xF5) : Color.FromRgb(0x1B, 0x1B, 0x1F);
        TextUnknown.Color = dark ? Color.FromRgb(0x85, 0x85, 0x8E) : Color.FromRgb(0x8A, 0x8A, 0x92);
        BarNormal.Color = dark ? Color.FromRgb(0xA8, 0xA8, 0xB0) : Color.FromRgb(0x6E, 0x6E, 0x76);
    }

    /// <summary>Gauge fill: neutral when normal, status color when warning / critical.</summary>
    public static Brush ForBar(MetricLevel level) => level switch
    {
        MetricLevel.Warning => Warning,
        MetricLevel.Critical => Critical,
        MetricLevel.Unknown => Unknown,
        _ => BarNormal,
    };

    /// <summary>Status word: muted when normal, status color otherwise.</summary>
    public static Brush ForStatus(MetricLevel level) => level is MetricLevel.Normal or MetricLevel.Unknown ? TextUnknown : For(level);

    /// <summary>Color for a value text: neutral when normal, status color when warning / critical.</summary>
    public static Brush ForText(MetricLevel level) => level switch
    {
        MetricLevel.Warning => Warning,
        MetricLevel.Critical => Critical,
        MetricLevel.Unknown => TextUnknown,
        _ => TextNormal,
    };

    public static Brush For(MetricLevel level) => level switch
    {
        MetricLevel.Normal => Normal,
        MetricLevel.Warning => Warning,
        MetricLevel.Critical => Critical,
        _ => Unknown,
    };
}

/// <summary>
/// Maps a <see cref="MetricLevel"/> to a status color (green / amber / red).
/// ConverterParameter "text" / "bar" / "status": neutral unless the value needs attention.
/// </summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is MetricLevel l ? l : MetricLevel.Unknown;
        return (parameter as string) switch
        {
            "text" => LevelPalette.ForText(level),
            "bar" => LevelPalette.ForBar(level),
            "status" => LevelPalette.ForStatus(level),
            _ => LevelPalette.For(level),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Status colors for the overlay, which always has a dark translucent background.</summary>
public sealed class OverlayLevelBrushConverter : IValueConverter
{
    private static readonly Brush Normal = Frozen(Color.FromRgb(0x81, 0xC7, 0x84));
    private static readonly Brush Warning = Frozen(Color.FromRgb(0xFF, 0xCA, 0x28));
    private static readonly Brush Critical = Frozen(Color.FromRgb(0xFF, 0x6E, 0x6E));
    private static readonly Brush Unknown = Frozen(Color.FromRgb(0xBD, 0xBD, 0xBD));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MetricLevel.Normal => Normal,
        MetricLevel.Warning => Warning,
        MetricLevel.Critical => Critical,
        _ => Unknown,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Status as words, so the state is not conveyed by color alone.</summary>
public sealed class LevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MetricLevel.Normal => "正常",
        MetricLevel.Warning => "注意",
        MetricLevel.Critical => "危険",
        _ => "取得不可",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound index equals the converter parameter (simple page switching).</summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && int.TryParse(parameter as string, out var wanted) && index == wanted ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Hardware sub-tab index to the key used by the Copy command.</summary>
public sealed class HardwareTabKeyConverter : IValueConverter
{
    private static readonly string[] Keys = ["cpu", "gpu", "display", "memory", "board"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i >= 0 && i < Keys.Length ? Keys[i] : "all";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapses the element when the bound string is null or empty.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
