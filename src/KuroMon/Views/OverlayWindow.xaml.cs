using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using KuroMon.Services;

namespace KuroMon.Views;

/// <summary>
/// Small always-on-top bar for checking values during games and streams.
/// It never takes focus from the game (WS_EX_NOACTIVATE) and can let clicks pass through (WS_EX_TRANSPARENT).
/// It shows over windowed / borderless games; exclusive fullscreen games draw over every window.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyClickThrough(AppSettings.Current.OverlayClickThrough);
        Loaded += (_, _) => PlaceWindow();
    }

    public void ApplyClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate;
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private void PlaceWindow()
    {
        var s = AppSettings.Current;
        var work = SystemParameters.WorkArea;
        double left = s.OverlayLeft ?? work.Left + 12;
        double top = s.OverlayTop ?? work.Top + 12;

        // Reset when the saved position is off-screen (e.g. a monitor was disconnected).
        bool visible = left >= SystemParameters.VirtualScreenLeft - 20
                       && top >= SystemParameters.VirtualScreenTop - 20
                       && left + 40 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
                       && top + 20 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Left = visible ? left : work.Left + 12;
        Top = visible ? top : work.Top + 12;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }

        AppSettings.Current.OverlayLeft = Left;
        AppSettings.Current.OverlayTop = Top;
        AppSettings.Current.Save();
    }

    private void OnClickThroughClick(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.OverlayClickThrough = true;
        AppSettings.Current.Save();
        App.Current.ShowTrayBalloon("KuroMon - オーバーレイ", "クリック透過にしました。元に戻すには設定タブの「オーバーレイ」でオフにしてください。");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.OverlayEnabled = false;
        AppSettings.Current.Save();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
