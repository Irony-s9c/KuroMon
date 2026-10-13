using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace KuroMon.Themes;

/// <summary>Windows accessibility settings that the liquid design must respect.</summary>
public static class SystemVisuals
{
    /// <summary>"Transparency effects" in Settings &gt; Personalization &gt; Colors. Off = opaque surfaces.</summary>
    public static bool TransparencyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int v || v != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>"Animation effects" in Settings &gt; Accessibility &gt; Visual effects. Off = no motion.</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) ? enabled : true;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>System backdrops (Mica / acrylic) for any window: Windows 11 22H2 (build 22621) or later.</summary>
    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}

public enum Backdrop
{
    /// <summary>Main window: Mica (tinted by the desktop wallpaper, the deepest layer).</summary>
    Mica = 2,
    /// <summary>Transient surfaces in front (mini panel, dialogs): acrylic, lighter and less opaque.</summary>
    Acrylic = 3,
}

/// <summary>DWM window effects: system backdrop, dark title bar, rounded corners.</summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int GwlStyle = -16;
    private const int WsSysMenu = 0x00080000;
    private const int DwmwcpRound = 2;
    private const int DwmwcpRoundSmall = 3;

    /// <summary>
    /// Applies the backdrop to a window (call again after a theme change). Returns false when the backdrop is not used
    /// (transparency off, old Windows or an error); the window then keeps an opaque background.
    /// </summary>
    public static bool Apply(Window window, Backdrop backdrop, bool dark, bool smallCorners = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            // With the frame extended into the client area (needed for the backdrop), DWM paints its own
            // minimize / maximize / close buttons over the custom title bar. Without WS_SYSMENU it does not.
            int style = GetWindowLong(hwnd, GwlStyle);
            if ((style & WsSysMenu) != 0) SetWindowLong(hwnd, GwlStyle, style & ~WsSysMenu);

            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            int corner = smallCorners ? DwmwcpRoundSmall : DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            bool useBackdrop = SystemVisuals.BackdropSupported && SystemVisuals.TransparencyEnabled;
            var source = HwndSource.FromHwnd(hwnd);

            if (useBackdrop)
            {
                // Let DWM draw behind the whole client area, then make WPF's own background transparent.
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                int type = (int)backdrop;
                if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) != 0) useBackdrop = false;
            }

            if (source?.CompositionTarget is { } target)
            {
                target.BackgroundColor = useBackdrop ? System.Windows.Media.Colors.Transparent : ThemeManager.FallbackWindowColor(dark);
            }
            window.Background = useBackdrop
                ? System.Windows.Media.Brushes.Transparent
                : new System.Windows.Media.SolidColorBrush(ThemeManager.FallbackWindowColor(dark));
            return useBackdrop;
        }
        catch (Exception ex)
        {
            Services.AppLog.Error("Applying the window backdrop failed.", ex);
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
