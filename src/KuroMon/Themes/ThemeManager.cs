using System.Windows;
using System.Windows.Media;

namespace KuroMon.Themes;

/// <summary>
/// Liquid design palette. Neutral glass layers with one accent color:
/// the further back a layer is (window, sidebar), the darker and more transparent; the closer
/// (cards, popups, selection), the lighter. Every brush is published as an application resource
/// and used through DynamicResource, so a theme switch recolors all windows at once.
/// When Windows "Transparency effects" is off (or the backdrop is unavailable) opaque colors are used instead.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; } = true;
    public static bool IsTranslucent { get; private set; }

    /// <summary>Raised after the resources changed; windows re-apply their DWM backdrop.</summary>
    public static event Action? Changed;

    // (translucent over Mica / acrylic, opaque fallback)
    private sealed record Tone(Color Glass, Color Solid);

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static readonly Dictionary<string, Tone> Dark = new()
    {
        ["Brush.SidebarTint"] = new(C(0x40000000), C(0xFF1A1A1D)),
        ["Brush.SurfaceTint"] = new(C(0x0AFFFFFF), C(0xFF212124)),
        ["Brush.CardTint"] = new(C(0x12FFFFFF), C(0xFF2A2A2E)),
        ["Brush.CardTintHover"] = new(C(0x1AFFFFFF), C(0xFF303035)),
        ["Brush.PopupTint"] = new(C(0x14FFFFFF), C(0xFF2C2C30)),
        ["Brush.SelectionPlate"] = new(C(0x1FFFFFFF), C(0xFF38383E)),
        ["Brush.HoverPlate"] = new(C(0x0DFFFFFF), C(0xFF2C2C31)),
        ["Brush.ControlFill"] = new(C(0x0FFFFFFF), C(0xFF2E2E33)),
        ["Brush.ControlFillHover"] = new(C(0x1AFFFFFF), C(0xFF36363C)),
        ["Brush.ControlFillPressed"] = new(C(0x08FFFFFF), C(0xFF29292D)),
        ["Brush.ControlStroke"] = new(C(0x24FFFFFF), C(0xFF45454C)),
        ["Brush.Divider"] = new(C(0x14FFFFFF), C(0xFF35353A)),
        ["Brush.Track"] = new(C(0x29FFFFFF), C(0xFF45454C)),
        ["Brush.ScrollThumb"] = new(C(0x4DFFFFFF), C(0xFF5A5A62)),
        ["Brush.TextPrimary"] = new(C(0xFFF3F3F5), C(0xFFF3F3F5)),
        ["Brush.TextSecondary"] = new(C(0xFFB4B4BC), C(0xFFB4B4BC)),
        ["Brush.TextTertiary"] = new(C(0xFF85858E), C(0xFF85858E)),
        ["Brush.Accent"] = new(C(0xFF7AAAF0), C(0xFF7AAAF0)),
        ["Brush.AccentHover"] = new(C(0xFF8DB7F3), C(0xFF8DB7F3)),
        ["Brush.OnAccent"] = new(C(0xFF0E1A2B), C(0xFF0E1A2B)),
        ["Brush.Thumb"] = new(C(0xFFF3F3F5), C(0xFFF3F3F5)),
        ["Brush.ThumbOff"] = new(C(0xFFB4B4BC), C(0xFFB4B4BC)),
        ["Brush.Shadow"] = new(C(0x66000000), C(0x66000000)),
        // Popups (menus, drop-downs, tooltips) cannot get a system backdrop, so they are nearly opaque.
        ["Brush.Menu"] = new(C(0xF52B2B30), C(0xFF2B2B30)),
    };

    private static readonly Dictionary<string, Tone> Light = new()
    {
        ["Brush.SidebarTint"] = new(C(0x0D000000), C(0xFFE8E8EC)),
        ["Brush.SurfaceTint"] = new(C(0x59FFFFFF), C(0xFFF5F5F7)),
        ["Brush.CardTint"] = new(C(0xA6FFFFFF), C(0xFFFFFFFF)),
        ["Brush.CardTintHover"] = new(C(0xCCFFFFFF), C(0xFFFFFFFF)),
        ["Brush.PopupTint"] = new(C(0x8CFFFFFF), C(0xFFFBFBFC)),
        ["Brush.SelectionPlate"] = new(C(0xD9FFFFFF), C(0xFFFFFFFF)),
        ["Brush.HoverPlate"] = new(C(0x66FFFFFF), C(0xFFF0F0F3)),
        ["Brush.ControlFill"] = new(C(0xB3FFFFFF), C(0xFFFFFFFF)),
        ["Brush.ControlFillHover"] = new(C(0xE6FFFFFF), C(0xFFF5F5F7)),
        ["Brush.ControlFillPressed"] = new(C(0x80FFFFFF), C(0xFFEDEDF0)),
        ["Brush.ControlStroke"] = new(C(0x24000000), C(0xFFD3D3D9)),
        ["Brush.Divider"] = new(C(0x14000000), C(0xFFE3E3E8)),
        ["Brush.Track"] = new(C(0x29000000), C(0xFFC9C9D0)),
        ["Brush.ScrollThumb"] = new(C(0x4D000000), C(0xFFB0B0B8)),
        ["Brush.TextPrimary"] = new(C(0xFF1B1B1F), C(0xFF1B1B1F)),
        ["Brush.TextSecondary"] = new(C(0xFF5C5C64), C(0xFF5C5C64)),
        ["Brush.TextTertiary"] = new(C(0xFF8A8A92), C(0xFF8A8A92)),
        ["Brush.Accent"] = new(C(0xFF2860C0), C(0xFF2860C0)),
        ["Brush.AccentHover"] = new(C(0xFF3470D2), C(0xFF3470D2)),
        ["Brush.OnAccent"] = new(C(0xFFFFFFFF), C(0xFFFFFFFF)),
        ["Brush.Thumb"] = new(C(0xFFFFFFFF), C(0xFFFFFFFF)),
        ["Brush.ThumbOff"] = new(C(0xFF6E6E76), C(0xFF6E6E76)),
        ["Brush.Shadow"] = new(C(0x29000000), C(0x29000000)),
        ["Brush.Menu"] = new(C(0xF7FBFBFC), C(0xFFFBFBFC)),
    };

    /// <summary>Window background when no system backdrop is drawn.</summary>
    public static Color FallbackWindowColor(bool dark) => dark ? C(0xFF1C1C1F) : C(0xFFEFEFF2);

    public static void Apply(bool dark)
    {
        IsDark = dark;
        IsTranslucent = SystemVisuals.BackdropSupported && SystemVisuals.TransparencyEnabled;
        var res = Application.Current.Resources;

        foreach (var (key, tone) in dark ? Dark : Light)
        {
            res[key] = Frozen(new SolidColorBrush(IsTranslucent ? tone.Glass : tone.Solid));
        }

        // The user's Windows accent color, in the shades Windows 11 apps use for each theme.
        if (SystemAccent.Read(dark) is { } accent)
        {
            res["Brush.Accent"] = Frozen(new SolidColorBrush(accent.Fill));
            res["Brush.AccentHover"] = Frozen(new SolidColorBrush(accent.Hover));
            res["Brush.OnAccent"] = Frozen(new SolidColorBrush(accent.OnFill));
        }

        // Thin edge line of a glass surface: slightly brighter at the top.
        res["Brush.Edge"] = Frozen(new LinearGradientBrush(
            dark ? C(0x1FFFFFFF) : C(0xE6FFFFFF),
            dark ? C(0x0FFFFFFF) : C(0x14000000),
            90));
        // Very faint top sheen (selection plate only).
        res["Brush.Gloss"] = Frozen(new LinearGradientBrush(
            [
                new GradientStop(dark ? C(0x08FFFFFF) : C(0x33FFFFFF), 0),
                new GradientStop(C(0x00FFFFFF), 0.5),
            ],
            90));
        // Accent edge for the primary button.
        res["Brush.AccentEdge"] = Frozen(new LinearGradientBrush(C(0x40FFFFFF), C(0x14000000), 90));

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
