using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KuroMon.Themes;
using KuroMon.ViewModels;

namespace KuroMon;

public partial class MainWindow : Window
{
    private readonly FrameworkElement[] _pages;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _pages = [PageDashboard, PageMemory, PageHardware, PageSettings];

        WindowGlass.Attach(this, Backdrop.Mica);
        Nav.SelectionChanged += (_, _) => ShowPage(Nav.SelectedIndex);
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            MaxButton.Content = max ? "" : "";
            MaxButton.ToolTip = max ? "元に戻す" : "最大化";
        };
    }

    /// <summary>Selects a page by index (0 = dashboard, 1 = memory, 2 = hardware, 3 = settings).</summary>
    public void SelectPage(int index) => Nav.SelectedIndex = index;

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            var page = _pages[i];
            if (i != index)
            {
                page.Visibility = Visibility.Collapsed;
                continue;
            }

            page.Visibility = Visibility.Visible;
            if (!Motion.Enabled) continue;

            // Short fade and rise of the new page.
            var shift = new TranslateTransform(0, 8);
            page.RenderTransform = shift;
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Medium) { EasingFunction = Motion.Ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Motion.Medium) { EasingFunction = Motion.Ease });
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Closing is handled by App: the window is hidden and KuroMon keeps running in the notification area.
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        await App.Current.CheckForUpdateAsync(silent: false, automatic: false);
}
