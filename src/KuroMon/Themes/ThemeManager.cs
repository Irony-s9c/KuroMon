using System.Windows;
using System.Windows.Media;

namespace KuroMon.Themes;

/// <summary>
/// Color palette in the Fluent look: opaque neutral layers (no Mica / acrylic) plus the Windows accent color.
/// Cards, popups and the selection are slightly lighter than the window. Every brush is published as an application resource
/// and used through DynamicResource, so a theme switch recolors all windows at once.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the resources changed; windows re-apply their background and title bar colors.</summary>
    public static event Action? Changed;

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static readonly Dictionary<string, Color> Dark = new()
    {
        ["Brush.SidebarTint"] = C(0xFF1A1A1D),
        ["Brush.SurfaceTint"] = C(0xFF212124),
        ["Brush.CardTint"] = C(0xFF2A2A2E),
        ["Brush.CardTintHover"] = C(0xFF303035),
        ["Brush.PopupTint"] = C(0xFF2C2C30),
        ["Brush.SelectionPlate"] = C(0xFF38383E),
        ["Brush.HoverPlate"] = C(0xFF2C2C31),
        ["Brush.ControlFill"] = C(0xFF2E2E33),
        ["Brush.ControlFillHover"] = C(0xFF36363C),
        ["Brush.ControlFillPressed"] = C(0xFF29292D),
        ["Brush.ControlStroke"] = C(0xFF45454C),
        ["Brush.Divider"] = C(0xFF35353A),
        ["Brush.Track"] = C(0xFF45454C),
        ["Brush.ScrollThumb"] = C(0xFF5A5A62),
        ["Brush.TextPrimary"] = C(0xFFF3F3F5),
        ["Brush.TextSecondary"] = C(0xFFB4B4BC),
        ["Brush.TextTertiary"] = C(0xFF85858E),
        ["Brush.Accent"] = C(0xFF7AAAF0),
        ["Brush.AccentHover"] = C(0xFF8DB7F3),
        ["Brush.OnAccent"] = C(0xFF0E1A2B),
        ["Brush.Thumb"] = C(0xFFF3F3F5),
        ["Brush.ThumbOff"] = C(0xFFB4B4BC),
        ["Brush.Shadow"] = C(0x66000000),
        ["Brush.Menu"] = C(0xFF2B2B30),
    };

    private static readonly Dictionary<string, Color> Light = new()
    {
        ["Brush.SidebarTint"] = C(0xFFE8E8EC),
        ["Brush.SurfaceTint"] = C(0xFFF5F5F7),
        ["Brush.CardTint"] = C(0xFFFFFFFF),
        ["Brush.CardTintHover"] = C(0xFFFFFFFF),
        ["Brush.PopupTint"] = C(0xFFFBFBFC),
        ["Brush.SelectionPlate"] = C(0xFFFFFFFF),
        ["Brush.HoverPlate"] = C(0xFFF0F0F3),
        ["Brush.ControlFill"] = C(0xFFFFFFFF),
        ["Brush.ControlFillHover"] = C(0xFFF5F5F7),
        ["Brush.ControlFillPressed"] = C(0xFFEDEDF0),
        ["Brush.ControlStroke"] = C(0xFFD3D3D9),
        ["Brush.Divider"] = C(0xFFE3E3E8),
        ["Brush.Track"] = C(0xFFC9C9D0),
        ["Brush.ScrollThumb"] = C(0xFFB0B0B8),
        ["Brush.TextPrimary"] = C(0xFF1B1B1F),
        ["Brush.TextSecondary"] = C(0xFF5C5C64),
        ["Brush.TextTertiary"] = C(0xFF8A8A92),
        ["Brush.Accent"] = C(0xFF2860C0),
        ["Brush.AccentHover"] = C(0xFF3470D2),
        ["Brush.OnAccent"] = C(0xFFFFFFFF),
        ["Brush.Thumb"] = C(0xFFFFFFFF),
        ["Brush.ThumbOff"] = C(0xFF6E6E76),
        ["Brush.Shadow"] = C(0x29000000),
        ["Brush.Menu"] = C(0xFFFBFBFC),
    };

    /// <summary>Window background (opaque).</summary>
    public static Color WindowColor(bool dark) => dark ? C(0xFF1C1C1F) : C(0xFFEFEFF2);

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var res = Application.Current.Resources;

        foreach (var (key, color) in dark ? Dark : Light)
        {
            res[key] = Frozen(new SolidColorBrush(color));
        }

        // The user's Windows accent color, in the shades Windows 11 apps use for each theme.
        if (SystemAccent.Read(dark) is { } accent)
        {
            res["Brush.Accent"] = Frozen(new SolidColorBrush(accent.Fill));
            res["Brush.AccentHover"] = Frozen(new SolidColorBrush(accent.Hover));
            res["Brush.OnAccent"] = Frozen(new SolidColorBrush(accent.OnFill));
        }

        // Card stroke (WinUI CardStrokeColorDefault).
        res["Brush.Edge"] = Frozen(new SolidColorBrush(dark ? C(0x19000000) : C(0x0F000000)));

        Converters.LevelPalette.ApplyTheme(dark);
        Changed?.Invoke();
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Motion durations. Zero when Windows "Animation effects" is off (read once at startup).</summary>
public static class Motion
{
    public static bool Enabled { get; } = SystemVisuals.AnimationsEnabled;

    /// <summary>Selection plate / hover plate / switch thumb (150-250 ms).</summary>
    public static Duration Fast { get; } = new(TimeSpan.FromMilliseconds(Enabled ? 180 : 0));

    public static Duration Medium { get; } = new(TimeSpan.FromMilliseconds(Enabled ? 240 : 0));

    public static System.Windows.Media.Animation.IEasingFunction Ease { get; } =
        new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
}
