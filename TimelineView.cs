using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitJournalViewer;

/// <summary>
/// Every journal entry plotted by time: one row per category (a colored square on the left),
/// one dot per entry. Hover a dot for its line; click it to jump there. Ctrl + wheel zooms,
/// the wheel pans. Hidden categories are drawn faded.
/// </summary>
public sealed class TimelineView : FrameworkElement
{
    /// <summary>The rows, top to bottom (the category chips' order, without the header).</summary>
    public static readonly JKind[] Lanes =
    {
        JKind.Command, JKind.Action, JKind.Timestamp, JKind.Comment, JKind.JrnData,
        JKind.Timing, JKind.Success, JKind.Warning, JKind.Error, JKind.Stats
    };

    private const double SquareColumn = 22;
    private const double AxisHeight = 20;
    private const double DotRadius = 3.5;
    private const double HiddenOpacity = 0.35;

    private readonly record struct Hit(double X, int Lane, int Index);

    private readonly List<Hit> _hits = new();
    private readonly Dictionary<JKind, int> _laneOf = new();
    private IReadOnlyList<JLine> _lines = Array.Empty<JLine>();
    private ISet<JKind> _hidden = new HashSet<JKind>();
    private Theme _theme = Theme.Dark;
    private DateTime _viewStart, _viewEnd;
    private bool _hasView;
    private Hit? _hover;
    private int _hoverSquare = -1;
    private Point _mouse;

    // Middle-button drag: grab the timeline and slide it through time.
    private bool _panning;
    private double _panStartX;
    private DateTime _panViewStart, _panViewEnd;

    /// <summary>File line number of the selected entry (drawn with a ring).</summary>
    public long? SelectedNumber { get; set; }

    /// <summary>True while the watched Revit is running: draws a "now" line.</summary>
    public bool LiveNow { get; set; }

    public DateTime ViewStart => _viewStart;
    public DateTime ViewEnd => _viewEnd;
    public bool HasView => _hasView;

    /// <summary>A dot was clicked.</summary>
    public event Action<JLine>? EntryClicked;

    /// <summary>The user zoomed or panned with the mouse wheel.</summary>
    public event Action? ViewChangedByUser;

    public Theme Theme
    {
        get => _theme;
        set { _theme = value; InvalidateVisual(); }
    }

    public TimelineView()
    {
        for (int i = 0; i < Lanes.Length; i++) _laneOf[Lanes[i]] = i;
        ClipToBounds = true;
        Focusable = false;
    }

    public void SetData(IReadOnlyList<JLine> lines, ISet<JKind> hidden)
    {
        _lines = lines;
        _hidden = hidden;
        InvalidateVisual();
    }

    public void SetView(DateTime start, DateTime end)
    {
        if (end <= start) end = start.AddSeconds(1);
        _viewStart = start;
        _viewEnd = end;
        _hasView = true;
        InvalidateVisual();
    }

    public void ClearView()
    {
        _hasView = false;
        InvalidateVisual();
    }

    /// <summary>Times of the first and last entries that have one.</summary>
    public (DateTime? First, DateTime? Last) Extent()
    {
        DateTime? first = null, last = null;
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Time is DateTime t) { first = t; break; }
        for (int i = _lines.Count - 1; i >= 0; i--)
            if (_lines[i].Time is DateTime t) { last = t; break; }
        return (first, last);
    }

    // ---------------------------------------------------------------- geometry

    private Rect PlotRect => new(SquareColumn, 0, Math.Max(1, ActualWidth - SquareColumn - 1), Math.Max(1, ActualHeight - AxisHeight));
    private double LaneHeight => PlotRect.Height / Lanes.Length;

    private FormattedText Text(string s, double size, Brush brush, bool bold = false, bool mono = false) => new(
        s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily(mono ? "Cascadia Mono, Consolas" : "Segoe UI"),
            FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatTime(DateTime t) => JournalClassifier.FormatTime(t);

    private static string LabelOf(JKind kind) =>
        JournalClassifier.FilterCategories.FirstOrDefault(c => c.Kind == kind).Label ?? kind.ToString();

    private static readonly int[] TickSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };

    // ---------------------------------------------------------------- drawing

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // so the whole area takes the mouse
        if (w < 80 || h < AxisHeight + 40) return;

        var plot = PlotRect;
        double laneH = LaneHeight;
        var border = _theme.Ui["Border"];
        var grid = _theme.Ui["ChartGrid"];
        var muted = _theme.Ui["MutedFg"];

        dc.DrawRoundedRectangle(_theme.Ui["InputBg"], new Pen(border, 1),
            new Rect(plot.Left + 0.5, plot.Top + 0.5, plot.Width - 1, plot.Height - 1), 3, 3);

        // category squares and lane separators
        var sepPen = new Pen(grid, 1); sepPen.Freeze();
        for (int i = 0; i < Lanes.Length; i++)
        {
            double top = plot.Top + i * laneH;
            if (i > 0) dc.DrawLine(sepPen, new Point(plot.Left + 1, Math.Round(top) + 0.5), new Point(plot.Right - 1, Math.Round(top) + 0.5));
            bool hidden = _hidden.Contains(Lanes[i]);
            if (hidden) dc.PushOpacity(HiddenOpacity);
            dc.DrawRoundedRectangle(_theme.StyleFor(Lanes[i]).Brush, null, new Rect(4, top + laneH / 2 - 5, 10, 10), 2, 2);
            if (hidden) dc.Pop();
        }

        _hits.Clear();
        if (!_hasView)
        {
            var msg = Text("No timestamped journal entries yet.", 12, muted);
            dc.DrawText(msg, new Point(plot.Left + (plot.Width - msg.Width) / 2, plot.Top + (plot.Height - msg.Height) / 2));
            DrawSquareLabel(dc);
            return;
        }

        double span = Math.Max(0.001, (_viewEnd - _viewStart).TotalSeconds);
        double X(DateTime t) => plot.Left + 4 + (t - _viewStart).TotalSeconds / span * (plot.Width - 8);

        // time axis: pick a step that leaves ~90 px between labels
        int step = TickSteps.FirstOrDefault(s => s / span * plot.Width >= 90);
        if (step == 0) step = TickSteps[^1];
        var startTick = new DateTime(_viewStart.Ticks - _viewStart.Ticks % TimeSpan.FromSeconds(step).Ticks);
        string fmt = step < 60 ? "HH:mm:ss" : "HH:mm";
        for (var t = startTick; t <= _viewEnd; t = t.AddSeconds(step))
        {
            if (t < _viewStart) continue;
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(sepPen, new Point(x, plot.Top + 1), new Point(x, plot.Bottom - 1));
            var label = Text(t.ToString(fmt, CultureInfo.InvariantCulture), 11, muted);
            double lx = Math.Clamp(x - label.Width / 2, plot.Left, w - label.Width);
            dc.DrawText(label, new Point(lx, plot.Bottom + 3));
        }

        // dots (one per pixel per lane is enough to draw; every one stays clickable via its first hit)
        var brushes = new Brush[Lanes.Length];
        for (int i = 0; i < Lanes.Length; i++)
        {
            var b = _theme.StyleFor(Lanes[i]).Brush.Clone();
            if (_hidden.Contains(Lanes[i])) b.Opacity = HiddenOpacity;
            b.Freeze();
            brushes[i] = b;
        }
        var drawn = new HashSet<long>();
        Hit? selected = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            if (line.Time is not DateTime t || t < _viewStart || t > _viewEnd) continue;
            if (!_laneOf.TryGetValue(line.Category, out int lane)) continue;
            double x = X(t);
            var hit = new Hit(x, lane, i);
            if (SelectedNumber is long sel && line.Number == sel && line.Number > 0) selected = hit;
            if (!drawn.Add(lane * 100000L + (long)Math.Round(x))) continue;
            _hits.Add(hit);
            dc.DrawEllipse(brushes[lane], null, new Point(x, plot.Top + lane * laneH + laneH / 2), DotRadius, DotRadius);
        }

        // "now" line while the session is live
        if (LiveNow)
        {
            var now = DateTime.Now;
            if (now >= _viewStart && now <= _viewEnd)
            {
                var nowPen = new Pen(_theme.Ui["Accent"], 2); nowPen.Freeze();
                double x = X(now);
                dc.PushOpacity(0.7);
                dc.DrawLine(nowPen, new Point(x, plot.Top + 1), new Point(x, plot.Bottom - 1));
                dc.Pop();
            }
        }

        // selection ring
        if (selected is Hit s)
        {
            var ring = new Pen(_theme.Ui["Fg"], 2); ring.Freeze();
            dc.DrawEllipse(null, ring, new Point(s.X, plot.Top + s.Lane * laneH + laneH / 2), DotRadius + 4, DotRadius + 4);
        }

        // hover card
        if (_hover is Hit hv && hv.Index < _lines.Count) DrawCard(dc, hv, plot, laneH);
        DrawSquareLabel(dc);
    }

    private void DrawCard(DrawingContext dc, Hit hv, Rect plot, double laneH)
    {
        var line = _lines[hv.Index];
        var kind = Lanes[hv.Lane];
        var fg = _theme.Ui["Fg"];
        var muted = _theme.Ui["MutedFg"];
        var color = _theme.StyleFor(kind).Brush;

        string where = line.Number > 0 ? $"line {line.Number:N0}" : "added by the viewer";
        string time = line.Time is DateTime t ? FormatTime(t) : "";
        var head = Text(LabelOf(kind), 12, fg, bold: true);
        var meta = Text($"  {where}  ·  {time}", 12, muted);
        string body = line.Text.Trim();
        if (body.Length > 90) body = body[..90] + "…";
        var text = Text(body, 11, color, mono: true);
        var hint = Text(_hidden.Contains(kind) ? "Hidden category: turn it on under Show to see this line." : "Click to jump to this line.", 11, muted);

        double width = Math.Min(Math.Max(head.Width + 16 + meta.Width, Math.Max(text.Width, hint.Width)) + 22, plot.Width - 8);
        text.MaxTextWidth = width - 22; text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis;
        double height = 8 + 17 + 17 + 16 + 8;
        double dotY = plot.Top + hv.Lane * laneH + laneH / 2;
        double x = Math.Clamp(hv.X - width / 2, plot.Left + 2, ActualWidth - width - 2);
        double y = dotY + 12 + height <= plot.Bottom ? dotY + 12 : dotY - 12 - height;
        y = Math.Max(1, y);

        var card = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(_theme.Ui["ButtonBg"], new Pen(_theme.Ui["Border"], 1), card, 6, 6);
        dc.DrawRoundedRectangle(color, null, new Rect(x + 11, y + 11, 10, 10), 2, 2);
        dc.DrawText(head, new Point(x + 27, y + 7));
        dc.DrawText(meta, new Point(x + 27 + head.Width, y + 7));
        dc.DrawText(text, new Point(x + 11, y + 26));
        dc.DrawText(hint, new Point(x + 11, y + 43));
    }

    private void DrawSquareLabel(DrawingContext dc)
    {
        if (_hoverSquare < 0) return;
        var kind = Lanes[_hoverSquare];
        var label = Text(LabelOf(kind) + (_hidden.Contains(kind) ? "  (hidden)" : ""), 12, _theme.Ui["Fg"]);
        double y = PlotRect.Top + _hoverSquare * LaneHeight + LaneHeight / 2 - 11;
        var r = new Rect(SquareColumn + 4, Math.Max(1, y), label.Width + 16, 22);
        dc.DrawRoundedRectangle(_theme.Ui["ButtonBg"], new Pen(_theme.Ui["Border"], 1), r, 5, 5);
        dc.DrawText(label, new Point(r.Left + 8, r.Top + 3));
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        var plot = PlotRect;
        if (_panning)
        {
            // The entries follow the pointer: drag right to go back in time, left to go forward.
            double span = (_panViewEnd - _panViewStart).TotalSeconds;
            double shift = -(_mouse.X - _panStartX) / Math.Max(1, plot.Width - 8) * span;
            SetView(_panViewStart.AddSeconds(shift), _panViewEnd.AddSeconds(shift));
            ViewChangedByUser?.Invoke();
            return;
        }
        Hit? found = null;
        int square = -1;
        int lane = (int)((_mouse.Y - plot.Top) / LaneHeight);
        if (lane >= 0 && lane < Lanes.Length && _mouse.Y < plot.Bottom)
        {
            if (_mouse.X < SquareColumn) square = lane;
            else
            {
                double best = 6;
                foreach (var hit in _hits)
                {
                    if (hit.Lane != lane) continue;
                    double d = Math.Abs(hit.X - _mouse.X);
                    if (d < best) { best = d; found = hit; }
                }
            }
        }
        if (found != _hover || square != _hoverSquare)
        {
            _hover = found;
            _hoverSquare = square;
            Cursor = found != null ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || !_hasView) return;
        _panning = true;
        _panStartX = e.GetPosition(this).X;
        _panViewStart = _viewStart;
        _panViewEnd = _viewEnd;
        _hover = null;
        _hoverSquare = -1;
        Cursor = Cursors.ScrollWE;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || !_panning) return;
        ReleaseMouseCapture();   // EndPan runs from OnLostMouseCapture
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_panning) return;
        _panning = false;
        Cursor = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_panning) return;
        if (_hover != null || _hoverSquare >= 0)
        {
            _hover = null;
            _hoverSquare = -1;
            Cursor = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_hover is not Hit hit || hit.Index >= _lines.Count) return;
        var line = _lines[hit.Index];
        SelectedNumber = line.Number;
        InvalidateVisual();
        EntryClicked?.Invoke(line);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_hasView) return;
        var plot = PlotRect;
        double span = (_viewEnd - _viewStart).TotalSeconds;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // zoom around the mouse
            double f = Math.Clamp((_mouse.X - plot.Left) / plot.Width, 0, 1);
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double newSpan = Math.Clamp(span * factor, 5, 7 * 86400);
            var anchor = _viewStart.AddSeconds(span * f);
            SetView(anchor.AddSeconds(-newSpan * f), anchor.AddSeconds(newSpan * (1 - f)));
        }
        else
        {
            double shift = span * 0.1 * (e.Delta > 0 ? -1 : 1);
            SetView(_viewStart.AddSeconds(shift), _viewEnd.AddSeconds(shift));
        }
        ViewChangedByUser?.Invoke();
        e.Handled = true;
    }
}
