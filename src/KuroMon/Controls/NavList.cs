using System.Windows;
using System.Windows.Controls;

namespace KuroMon.Controls;

public enum IndicatorPlacement
{
    None,
    Left,
    Bottom,
}

/// <summary>
/// List of choices drawn like the WinUI navigation view: the selected item gets a subtle fill and a short
/// accent bar (left for the sidebar, bottom for tabs). The look comes from Themes\Controls.xaml.
/// </summary>
public class NavList : ListBox
{
    public static readonly DependencyProperty IndicatorProperty = DependencyProperty.Register(
        nameof(Indicator), typeof(IndicatorPlacement), typeof(NavList), new PropertyMetadata(IndicatorPlacement.None));

    public IndicatorPlacement Indicator
    {
        get => (IndicatorPlacement)GetValue(IndicatorProperty);
        set => SetValue(IndicatorProperty, value);
    }

    static NavList()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(NavList), new FrameworkPropertyMetadata(typeof(NavList)));
    }
}
