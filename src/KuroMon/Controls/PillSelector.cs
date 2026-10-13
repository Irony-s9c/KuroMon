using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KuroMon.Themes;

namespace KuroMon.Controls;

public enum IndicatorPlacement
{
    None,
    Left,
    Bottom,
}

/// <summary>
/// List of choices with a glass selection plate that slides to the selected item,
/// and a faint hover plate that follows the mouse. Used for the sidebar and segmented tabs.
/// </summary>
[TemplatePart(Name = PartSelection, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartHover, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartCanvas, Type = typeof(Canvas))]
public class PillSelector : ListBox
{
    private const string PartSelection = "PART_Selection";
    private const string PartHover = "PART_Hover";
    private const string PartCanvas = "PART_Canvas";

    public static readonly DependencyProperty IndicatorProperty = DependencyProperty.Register(
        nameof(Indicator), typeof(IndicatorPlacement), typeof(PillSelector), new PropertyMetadata(IndicatorPlacement.None));

    public static readonly DependencyProperty PlateCornerRadiusProperty = DependencyProperty.Register(
        nameof(PlateCornerRadius), typeof(CornerRadius), typeof(PillSelector), new PropertyMetadata(new CornerRadius(8)));

    /// <summary>Small accent bar on the selection plate (left for vertical lists, bottom for tabs).</summary>
    public IndicatorPlacement Indicator
    {
        get => (IndicatorPlacement)GetValue(IndicatorProperty);
        set => SetValue(IndicatorProperty, value);
    }

    public CornerRadius PlateCornerRadius
    {
        get => (CornerRadius)GetValue(PlateCornerRadiusProperty);
        set => SetValue(PlateCornerRadiusProperty, value);
    }

    private FrameworkElement? _selection;
    private FrameworkElement? _hover;
    private Canvas? _canvas;
    private bool _placed;

    static PillSelector()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PillSelector), new FrameworkPropertyMetadata(typeof(PillSelector)));
    }

    public PillSelector()
    {
        SelectionChanged += (_, _) => MoveSelection(animate: _placed);
        SizeChanged += (_, _) => MoveSelection(animate: false);
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => MoveSelection(animate: false), System.Windows.Threading.DispatcherPriority.Loaded);
        LayoutUpdated += (_, _) => { if (!_placed) MoveSelection(animate: false); };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _selection = GetTemplateChild(PartSelection) as FrameworkElement;
        _hover = GetTemplateChild(PartHover) as FrameworkElement;
        _canvas = GetTemplateChild(PartCanvas) as Canvas;
        if (_hover is not null) _hover.Opacity = 0;
        _placed = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var item = ItemsControl.ContainerFromElement(this, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null || _hover is null) return;

        bool wasHidden = _hover.Opacity < 0.01;
        MovePlate(_hover, item, animate: !wasHidden);
        Fade(_hover, 1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is not null) Fade(_hover, 0);
    }

    private void MoveSelection(bool animate)
    {
        if (_selection is null || _canvas is null) return;
        if (SelectedIndex < 0 || ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not ListBoxItem item || !item.IsLoaded || item.ActualWidth <= 0)
        {
            _selection.Visibility = SelectedIndex < 0 ? Visibility.Collapsed : _selection.Visibility;
            return;
        }

        _selection.Visibility = Visibility.Visible;
        MovePlate(_selection, item, animate);
        _placed = true;
    }

    private void MovePlate(FrameworkElement plate, FrameworkElement item, bool animate)
    {
        if (_canvas is null) return;
        Point p;
        try { p = item.TranslatePoint(new Point(0, 0), _canvas); }
        catch (InvalidOperationException) { return; } // not in the same visual tree yet

        Animate(plate, Canvas.LeftProperty, p.X, animate);
        Animate(plate, Canvas.TopProperty, p.Y, animate);
        Animate(plate, WidthProperty, item.ActualWidth, animate);
        Animate(plate, HeightProperty, item.ActualHeight, animate);
    }

    private static void Animate(FrameworkElement target, DependencyProperty property, double to, bool animate)
    {
        if (!animate || !Motion.Enabled || double.IsNaN((double)target.GetValue(property)))
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }
        var anim = new DoubleAnimation(to, Motion.Fast) { EasingFunction = Motion.Ease };
        target.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Fade(UIElement target, double to)
    {
        if (!Motion.Enabled)
        {
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = to;
            return;
        }
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(to, Motion.Fast), HandoffBehavior.SnapshotAndReplace);
    }
}
