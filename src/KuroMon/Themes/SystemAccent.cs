using System.Windows.Media;
using Microsoft.Win32;

namespace KuroMon.Themes;

/// <summary>
/// Windows accent color (Settings &gt; Personalization &gt; Colors), read from the accent palette that
/// Windows itself generates: 8 colors from Light3 to Dark3. Windows 11 apps fill accent controls with
/// Light2 in the dark theme and Dark1 in the light theme.
/// </summary>
public static class SystemAccent
{
    public sealed record Shades(Color Fill, Color Hover, Color OnFill);

    public static Shades? Read(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is not byte[] p || p.Length < 32) return null;

            Color At(int i) => Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            // 0 Light3, 1 Light2, 2 Light1, 3 Base, 4 Dark1, 5 Dark2, 6 Dark3
            return dark
                ? new Shades(At(1), At(0), Color.FromRgb(0x10, 0x10, 0x12))
                : new Shades(At(4), At(3), Colors.White);
        }
        catch
        {
            return null;
        }
    }
}
