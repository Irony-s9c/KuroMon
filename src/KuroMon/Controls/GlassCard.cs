using System.Windows;
using System.Windows.Controls;

namespace KuroMon.Controls;

/// <summary>
/// A glass surface: translucent tint, thin light edge line and a slight gloss on the upper part.
/// The look comes from the default style in Themes\Controls.xaml.
/// </summary>
public class GlassCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassCard), new PropertyMetadata(new CornerRadius(10)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static GlassCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassCard), new FrameworkPropertyMetadata(typeof(GlassCard)));
    }
}
