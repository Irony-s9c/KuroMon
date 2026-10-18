using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace KuroMon.Themes;

/// <summary>Windows accessibility settings that the liquid design must respect.</summary>
public static class SystemVisuals
{
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

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}

/// <summary>DWM window effects: opaque background (no Mica / acrylic), dark title bar, rounded corners.</summary>
public static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int GwlStyle = -16;
    private const int WsSysMenu = 0x00080000;
    private const int DwmwcpRound = 2;
    private const int DwmwcpRoundSmall = 3;
    private const int DwmsbtNone = 1;

    /// <summary>Applies the background and title bar colors to a window (call again after a theme change).</summary>
    public static void Apply(Window window, bool dark, bool smallCorners = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // DWM must not paint its own minimize / maximize / close buttons over the custom title bar.
            int style = GetWindowLong(hwnd, GwlStyle);
            if ((style & WsSysMenu) != 0) SetWindowLong(hwnd, GwlStyle, style & ~WsSysMenu);

            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            int corner = smallCorners ? DwmwcpRoundSmall : DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            int none = DwmsbtNone;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));

            var color = ThemeManager.WindowColor(dark);
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = color;
            window.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        catch (Exception ex)
        {
            Services.AppLog.Error("Applying the window colors failed.", ex);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
