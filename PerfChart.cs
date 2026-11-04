using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RevitJournalViewer;

/// <summary>
/// A small running graph: title, current value and peak on top, the last few minutes below
/// (newest at the right). Gaps (null readings) break the line.
/// </summary>
public sealed class PerfChart : FrameworkElement
{
    public const int Capacity = 300;               // 5 minutes at one sample a second
    private const double HeaderHeight = 40;

    private readonly double?[] _values = new double?[Capacity];
    private int _start, _count;
    private Theme _theme = Theme.Dark;

    public string Title { get; init; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Color key in Theme.Ui for this chart's line.</summary>
    public string ColorKey { get; init; } = "Accent";
    /// <summary>Fixed top of the scale (e.g. 100 for percentages); null scales to the data.</summary>
    public double? FixedMax { get; init; }
    /// <summary>Smallest automatic top of the scale.</summary>
    public double MinAutoMax { get; init; } = 10;
    public Func<double, string> Format { get; init; } = v => v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Shown in place of the graph when the value can't be read.</summary>
    public string? Unavailable { get; private set; }

    public double? Current { get; private set; }
    public double? Peak { get; private set; }

    public Theme Theme
    {
        get => _theme;
        set { _theme = value; InvalidateVisual(); }
    }

    public PerfChart()
    {
        Height = 128;
        SnapsToDevicePixels = true;
    }

    public void Push(double? value)
    {
        if (_count < Capacity) _values[(_start + _count++) % Capacity] = value;
        else { _values[_start] = value; _start = (_start + 1) % Capacity; }

        Current = value;
        if (value is double v) Peak = Peak is double p ? Math.Max(p, v) : v;
        InvalidateVisual();
    }

    public void SetUnavailable(string? reason)
    {
        if (reason == Unavailable) return;
        Unavailable = reason;
        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_values);
        _start = _count = 0;
        Current = Peak = null;
        Unavailable = null;
        InvalidateVisual();
    }

    private double? At(int i) => _values[(_start + i) % Capacity];

    // ---------------------------------------------------------------- drawing

    private FormattedText Text(string s, double size, Brush brush, FontWeight weight) => new(
        s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static double NiceCeiling(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var step in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
            if (step * mag >= v) return step * mag;
        return 10 * mag;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < HeaderHeight + 20) return;

        var fg = _theme.Ui["Fg"];
        var muted = _theme.Ui["MutedFg"];
        var line = (SolidColorBrush)_theme.Ui[ColorKey];
        var grid = _theme.Ui["ChartGrid"];
        var plotBg = _theme.Ui["InputBg"];
        var border = _theme.Ui["Border"];

        // header: title / subtitle on the left, current value / peak on the right
        dc.DrawText(Text(Title, 12, fg, FontWeights.SemiBold), new Point(0, 0));
        var sub = Text(Subtitle, 10.5, muted, FontWeights.Normal);
        sub.MaxTextWidth = Math.Max(10, w - 90);
        sub.MaxLineCount = 1;
        sub.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(sub, new Point(0, 19));

        var cur = Text(Current is double c ? Format(c) : "–", 16, line, FontWeights.Bold);
        dc.DrawText(cur, new Point(w - cur.Width, -2));
        if (Peak is double pk)
        {
            var peak = Text("peak " + Format(pk), 10.5, muted, FontWeights.Normal);
            dc.DrawText(peak, new Point(w - peak.Width, 20));
        }

        // plot area
        var plot = new Rect(0.5, HeaderHeight + 0.5, w - 1, h - HeaderHeight - 1);
        dc.DrawRoundedRectangle(plotBg, new Pen(border, 1), plot, 3, 3);

        if (Unavailable != null)
        {
            var msg = Text(Unavailable, 11, muted, FontWeights.Normal);
            msg.MaxTextWidth = plot.Width - 16;
            msg.TextAlignment = TextAlignment.Center;
            dc.DrawText(msg, new Point(plot.Left + 8, plot.Top + (plot.Height - msg.Height) / 2));
            return;
        }

        // scale
        double max = FixedMax ?? 0;
        if (FixedMax == null)
        {
            for (int i = 0; i < _count; i++) if (At(i) is double v) max = Math.Max(max, v);
            max = NiceCeiling(Math.Max(MinAutoMax, max * 1.15));
        }

        var gridPen = new Pen(grid, 1);
        gridPen.Freeze();
        for (int g = 1; g <= 3; g++)
        {
            double y = Math.Round(plot.Top + plot.Height * g / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left + 1, y), new Point(plot.Right - 1, y));
        }
        dc.DrawText(Text(Format(max), 10, muted, FontWeights.Normal), new Point(plot.Left + 4, plot.Top + 2));

        if (_count < 2) return;

        // series, newest at the right edge; break the line at missing readings
        double step = (plot.Width - 2) / (Capacity - 1);
        double X(int i) => plot.Right - 1 - (_count - 1 - i) * step;
        double Y(double v) => plot.Bottom - 1 - Math.Clamp(v / max, 0, 1) * (plot.Height - 4);

        var fillColor = line.Color; fillColor.A = 0x38;
        var fill = new SolidColorBrush(fillColor); fill.Freeze();
        var pen = new Pen(line, 1.5) { LineJoin = PenLineJoin.Round }; pen.Freeze();

        dc.PushClip(new RectangleGeometry(plot, 3, 3));
        int iStart = 0;
        while (iStart < _count)
        {
            while (iStart < _count && At(iStart) == null) iStart++;
            int iEnd = iStart;
            while (iEnd < _count && At(iEnd) != null) iEnd++;
            if (iEnd - iStart >= 2)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(X(iStart), plot.Bottom - 1), true, true);
                    for (int i = iStart; i < iEnd; i++) ctx.LineTo(new Point(X(i), Y(At(i)!.Value)), false, false);
                    ctx.LineTo(new Point(X(iEnd - 1), plot.Bottom - 1), false, false);
                }
                area.Freeze();
                dc.DrawGeometry(fill, null, area);

                var stroke = new StreamGeometry();
                using (var ctx = stroke.Open())
                {
                    ctx.BeginFigure(new Point(X(iStart), Y(At(iStart)!.Value)), false, false);
                    for (int i = iStart + 1; i < iEnd; i++) ctx.LineTo(new Point(X(i), Y(At(i)!.Value)), true, true);
                }
                stroke.Freeze();
                dc.DrawGeometry(null, pen, stroke);
            }
            iStart = iEnd;
        }
        dc.Pop();
    }
}
