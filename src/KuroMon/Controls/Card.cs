using System.Windows;
using System.Windows.Controls;

namespace KuroMon.Controls;

/// <summary>
/// A card surface (card fill, 1px stroke, 8px corners). The look comes from the default style in Themes\Controls.xaml.
/// </summary>
public class Card : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(Card), new PropertyMetadata(new CornerRadius(8)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static Card()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));
    }
}
