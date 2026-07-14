using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Jampanion.Controls;

public sealed class VolumeSlider : Control
{
    private const double ThumbRadius = 7;
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xC5, 0xC7));
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromRgb(0x5F, 0x7C, 0x86));
    private static readonly IBrush FocusBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x6E, 0x69));

    public static readonly StyledProperty<int> ValueProperty = AvaloniaProperty.Register<VolumeSlider, int>(
        nameof(Value),
        defaultValue: 100,
        coerce: (_, value) => Math.Clamp(value, 0, 100));

    static VolumeSlider()
    {
        AffectsRender<VolumeSlider>(ValueProperty);
    }

    public VolumeSlider()
    {
        Focusable = true;
        MinHeight = 24;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var centerY = Bounds.Height / 2;
        var startX = ThumbRadius + 1;
        var endX = Math.Max(startX, Bounds.Width - ThumbRadius - 1);
        var thumbX = startX + (endX - startX) * Value / 100.0;

        context.DrawLine(new Pen(TrackBrush, 2), new Point(startX, centerY), new Point(endX, centerY));
        context.DrawLine(new Pen(FillBrush, 3), new Point(startX, centerY), new Point(thumbX, centerY));
        context.DrawEllipse(FillBrush, null, new Point(thumbX, centerY), ThumbRadius, ThumbRadius);

        if (IsFocused)
        {
            context.DrawEllipse(null, new Pen(FocusBrush, 1), new Point(thumbX, centerY), ThumbRadius + 3, ThumbRadius + 3);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Pointer.Capture(this);
        UpdateFromPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this)
        {
            UpdateFromPointer(e.GetPosition(this).X);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this)
        {
            UpdateFromPointer(e.GetPosition(this).X);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var nextValue = e.Key switch
        {
            Key.Left or Key.Down => Value - 5,
            Key.Right or Key.Up => Value + 5,
            Key.Home => 0,
            Key.End => 100,
            _ => Value
        };
        if (nextValue != Value)
        {
            SetCurrentValue(ValueProperty, Math.Clamp(nextValue, 0, 100));
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private void UpdateFromPointer(double pointerX)
    {
        var startX = ThumbRadius + 1;
        var endX = Math.Max(startX + 1, Bounds.Width - ThumbRadius - 1);
        var normalized = Math.Clamp((pointerX - startX) / (endX - startX), 0, 1);
        SetCurrentValue(ValueProperty, (int)Math.Round(normalized * 100));
    }
}
