using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace RevitJournalViewer;

/// <summary>
/// Left gutter for the output view: the journal file line number and the line's time (12:00:00::00).
/// Document line N of the output corresponds to <c>lines[N-1]</c>.
/// Times copied from an earlier timestamped line are drawn dimmer than the line's own timestamp.
/// </summary>
public sealed class LineInfoMargin : AbstractMargin
{
    private const string TimeSample = "00:00:00::00";
    private const double Gap = 12;
    private const double LeftPad = 6;

    private readonly IReadOnlyList<JLine> _lines;
    private bool _showNumbers = true;
    private bool _showTimes = true;
    private int _digits = 6;
    private Theme _theme = Theme.Dark;

    public LineInfoMargin(IReadOnlyList<JLine> lines) => _lines = lines;

    public bool ShowNumbers
    {
        get => _showNumbers;
        set { _showNumbers = value; Refresh(); }
    }

    public bool ShowTimes
    {
        get => _showTimes;
        set { _showTimes = value; Refresh(); }
    }

    /// <summary>File line numbers with a bookmark (drawn as a bar at the gutter's left edge).</summary>
    public ISet<long>? Bookmarks { get; set; }

    public Theme Theme
    {
        get => _theme;
        set { _theme = value; InvalidateVisual(); }
    }

    /// <summary>Widen the number column when line numbers get longer.</summary>
    public void EnsureDigits(long maxNumber)
    {
        int d = Math.Max(6, maxNumber.ToString(CultureInfo.InvariantCulture).Length);
        if (d != _digits) { _digits = d; InvalidateMeasure(); }
    }

    private void Refresh()
    {
        Visibility = _showNumbers || _showTimes ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- text helpers

    private Typeface Typeface => new(
        (FontFamily)GetValue(TextBlock.FontFamilyProperty),
        (FontStyle)GetValue(TextBlock.FontStyleProperty),
        (FontWeight)GetValue(TextBlock.FontWeightProperty),
        (FontStretch)GetValue(TextBlock.FontStretchProperty));

    private FormattedText Text(string s, Brush brush) => new(
        s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface,
        (double)GetValue(TextBlock.FontSizeProperty), brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private double NumberWidth => Text(new string('9', _digits), Brushes.Black).WidthIncludingTrailingWhitespace;
    private double TimeWidth => Text(TimeSample, Brushes.Black).WidthIncludingTrailingWhitespace;

    // ---------------------------------------------------------------- layout & drawing

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_showNumbers && !_showTimes) return new Size(0, 0);
        double w = LeftPad;
        if (_showNumbers) w += NumberWidth + Gap;
        if (_showTimes) w += TimeWidth + Gap;
        return new Size(w, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(_theme.Ui["PanelBg"], null, new Rect(size));

        // Separator between the gutter and the text.
        var pen = new Pen(_theme.Ui["ChipBorder"], 1);
        double sx = Math.Floor(size.Width) - 0.5;
        dc.DrawLine(pen, new Point(sx, 0), new Point(sx, size.Height));

        var tv = TextView;
        if (tv == null || !tv.VisualLinesValid) return;

        Brush numberBrush = _theme.Ui["MutedFg"];
        Brush timeBrush = _theme.StyleFor(JKind.Timestamp).Brush;
        Brush exactBrush = _theme.Ui["Fg"];
        double numberWidth = NumberWidth;

        foreach (var vl in tv.VisualLines)
        {
            int docLine = vl.FirstDocumentLine.LineNumber;
            if (docLine < 1 || docLine > _lines.Count) continue;
            var line = _lines[docLine - 1];

            double y = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - tv.VerticalOffset;
            double x = LeftPad;

            if (line.Number > 0 && Bookmarks?.Contains(line.Number) == true)
                dc.DrawRoundedRectangle(_theme.Ui["Accent"], null, new Rect(1, y + 1, 4, Math.Max(4, vl.Height - 2)), 1.5, 1.5);

            if (_showNumbers)
            {
                if (line.Number > 0)
                {
                    var ft = Text(line.Number.ToString(CultureInfo.InvariantCulture), numberBrush);
                    dc.DrawText(ft, new Point(x + numberWidth - ft.WidthIncludingTrailingWhitespace, y));
                }
                x += numberWidth + Gap;
            }

            if (_showTimes && line.Time is DateTime t)
            {
                var ft = Text(JournalClassifier.FormatTime(t), line.TimeExact ? exactBrush : timeBrush);
                if (!line.TimeExact) dc.PushOpacity(0.6);
                dc.DrawText(ft, new Point(x, y));
                if (!line.TimeExact) dc.Pop();
            }
        }
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null)
        {
            oldTextView.VisualLinesChanged -= OnViewChanged;
            oldTextView.ScrollOffsetChanged -= OnViewChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
        {
            newTextView.VisualLinesChanged += OnViewChanged;
            newTextView.ScrollOffsetChanged += OnViewChanged;
        }
        InvalidateVisual();
    }

    private void OnViewChanged(object? sender, EventArgs e) => InvalidateVisual();
}
