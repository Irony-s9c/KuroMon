using System.Windows;

namespace KuroMon.Themes;

/// <summary>Gives a window its system backdrop and keeps it in sync with the app theme.</summary>
public static class WindowBackdrop
{
    public static void Attach(Window window, bool smallCorners = false)
    {
        void Apply() => WindowEffects.Apply(window, ThemeManager.IsDark, smallCorners);
        void OnThemeChanged() => window.Dispatcher.BeginInvoke(Apply);

        window.SourceInitialized += (_, _) => Apply();
        ThemeManager.Changed += OnThemeChanged;
        window.Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }
}
