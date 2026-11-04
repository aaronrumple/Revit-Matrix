using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitJournalViewer;

/// <summary>
/// A two-handle slider for a range between 0 and 1. Drag a handle to resize the range,
/// drag between the handles to move it.
/// </summary>
public sealed class RangeBar : FrameworkElement
{
    private const double Inset = 8;
    private const double ThumbRadius = 7;
    private const double MinGap = 0.002;

    private enum Drag { None, Lower, Upper, Both }

    private double _lower, _upper = 1;
    private Drag _drag;
    private double _grabOffset;
    private Theme _theme = Theme.Dark;

    public double Lower => _lower;
    public double Upper => _upper;

    /// <summary>The user dragged the range.</summary>
    public event Action? RangeChangedByUser;

    public Theme Theme
    {
        get => _theme;
        set { _theme = value; InvalidateVisual(); }
    }

    public RangeBar()
    {
        Height = 18;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public void Set(double lower, double upper)
    {
        lower = Math.Clamp(lower, 0, 1);
        upper = Math.Clamp(upper, 0, 1);
        if (upper - lower < MinGap) upper = Math.Min(1, lower + MinGap);
        if (lower == _lower && upper == _upper) return;
        _lower = lower;
        _upper = upper;
        InvalidateVisual();
    }

    private double TrackWidth => Math.Max(1, ActualWidth - 2 * Inset);
    private double ToX(double v) => Inset + v * TrackWidth;
    private double ToValue(double x) => Math.Clamp((x - Inset) / TrackWidth, 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        double mid = ActualHeight / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var track = new Pen(_theme.Ui["Border"], 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        track.Freeze();
        dc.DrawLine(track, new Point(Inset, mid), new Point(ActualWidth - Inset, mid));

        var accent = _theme.Ui["Accent"];
        var range = new Pen(accent, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        range.Freeze();
        dc.DrawLine(range, new Point(ToX(_lower), mid), new Point(ToX(_upper), mid));

        var thumbPen = new Pen(accent, 2); thumbPen.Freeze();
        var fill = _theme.Ui["Fg"];
        dc.DrawEllipse(fill, thumbPen, new Point(ToX(_lower), mid), ThumbRadius - 1, ThumbRadius - 1);
        dc.DrawEllipse(fill, thumbPen, new Point(ToX(_upper), mid), ThumbRadius - 1, ThumbRadius - 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        double x = e.GetPosition(this).X;
        double dLo = Math.Abs(x - ToX(_lower)), dHi = Math.Abs(x - ToX(_upper));
        if (Math.Min(dLo, dHi) <= ThumbRadius + 3) _drag = dLo <= dHi ? Drag.Lower : Drag.Upper;
        else if (x > ToX(_lower) && x < ToX(_upper)) { _drag = Drag.Both; _grabOffset = ToValue(x) - _lower; }
        else { _drag = x < ToX(_lower) ? Drag.Lower : Drag.Upper; Move(x); }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != Drag.None) Move(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    private void Move(double x)
    {
        double v = ToValue(x);
        switch (_drag)
        {
            case Drag.Lower: _lower = Math.Min(v, _upper - MinGap); break;
            case Drag.Upper: _upper = Math.Max(v, _lower + MinGap); break;
            case Drag.Both:
                double len = _upper - _lower;
                _lower = Math.Clamp(v - _grabOffset, 0, 1 - len);
                _upper = _lower + len;
                break;
        }
        InvalidateVisual();
        RangeChangedByUser?.Invoke();
    }
}
