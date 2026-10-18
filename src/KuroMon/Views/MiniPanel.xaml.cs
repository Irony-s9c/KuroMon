using System.Windows;

namespace KuroMon.Views;

/// <summary>Small flyout shown above the notification area when the tray icon is clicked.</summary>
public partial class MiniPanel : Window
{
    private DateTime _hiddenAt = DateTime.MinValue;

    public MiniPanel()
    {
        InitializeComponent();
        Themes.WindowBackdrop.Attach(this);
        Deactivated += (_, _) => HidePanel();
    }

    public event Action? OpenMainRequested;

    /// <summary>Shows the panel near the bottom-right corner of the work area, or hides it if visible.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }

        // Clicking the tray icon deactivates the panel first; ignore that same click.
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 300) return;

        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        Activate();
    }

    private void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.Now;
        Hide();
    }

    private void OnOpenMainClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        OpenMainRequested?.Invoke();
    }
}
