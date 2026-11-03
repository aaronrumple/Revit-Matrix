using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitJournalViewer;

/// <summary>
/// A line as shown in the viewer. Number is the line number in the journal file (0 = added by the viewer).
/// Time is the line's journal time; TimeExact is false when it is carried forward from an earlier
/// timestamped line, or is the time the viewer received it.
/// </summary>
public readonly record struct JLine(long Number, string Text, JKind Category, DateTime? Time, bool TimeExact);

/// <summary>Kinds of text in a Revit journal, each drawn in its own color.</summary>
public enum JKind
{
    Default,
    Header,       // ' Build: / ' Branch: / ' Release: / Dim Jrn / Set Jrn
    Command,      // Jrn.Command "Ribbon" , "...": the user's commands
    Action,       // other Jrn.* replay lines (MouseMove, PushButton, Directive, Data, ...)
    Argument,     // continuation lines:   "Name" , value
    String,       // "quoted" values inside action lines
    Timestamp,    // 'C 25-Feb-2026 21:31:27.120;
    Comment,      // ordinary ' comment text
    Arrow,        // ->Enter / <-Exit markers on 'C lines
    JrnData,      // ' [Jrn.AddInManifest] ...
    Timing,       // '  5.596119   1:<<<wait for ...
    Stats,        // memory / GDI / perf table noise
    Success,      // API_SUCCESS
    Warning,
    Error,        // API_ERROR, exception, failed, ...
    Marker        // lines the viewer itself adds (session ended)
}

public readonly record struct JSpan(int Start, int Length, JKind Kind);

/// <summary>Result for one line: the filter category of the whole line, plus colored spans.</summary>
public readonly record struct JLineInfo(JKind Category, List<JSpan> Spans);

/// <summary>
/// Splits one journal line into colored spans and assigns it one filter category.
/// Pure text logic, no WPF, so it is easy to test. Based on real Revit 2026 journals.
/// </summary>
public static class JournalClassifier
{
    public const string MarkerPrefix = "========";

    /// <summary>Categories offered as filter buttons, in display order, with labels.</summary>
    public static readonly (JKind Kind, string Label, string Tip)[] FilterCategories =
    {
        (JKind.Command,   "Commands",   "Jrn.Command lines: commands you ran"),
        (JKind.Action,    "Actions",    "Other Jrn.* replay lines (mouse, buttons, dialogs, data) and their arguments"),
        (JKind.Timestamp, "Events",     "'C / 'E / 'H timestamped entries, ->enter / <-exit markers"),
        (JKind.Comment,   "Comments",   "Other ' comment and diagnostic lines"),
        (JKind.JrnData,   "Jrn data",   "' [Jrn.*] recorded data, e.g. add-in manifests"),
        (JKind.Timing,    "Timing",     "Elapsed-time lines (  5.596119   1:<<<…)"),
        (JKind.Success,   "API OK",     "API_SUCCESS add-in messages"),
        (JKind.Warning,   "Warnings",   "Lines mentioning warnings"),
        (JKind.Error,     "Errors",     "API_ERROR, error, exception, failed, crash…"),
        (JKind.Stats,     "Mem stats",  "Delta VM / GDI usage / perf tables"),
        (JKind.Header,    "Header",     "Build, branch, release, Dim/Set Jrn"),
    };

    private static readonly Regex TimestampRx = new(
        @"^\s*'[CEH] (?<ts>\d{1,2}-[A-Za-z]{3}-\d{4} \d{2}:\d{2}:\d{2}\.\d{3});",
        RegexOptions.Compiled);

    /// <summary>Reads the time from a 'C / 'E / 'H line, e.g. 'C 25-Feb-2026 21:31:27.120;</summary>
    public static bool TryGetTimestamp(string line, out DateTime time)
    {
        time = default;
        var m = TimestampRx.Match(line);
        return m.Success && DateTime.TryParseExact(m.Groups["ts"].Value, "d-MMM-yyyy HH:mm:ss.fff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>12:00:00::00 – hours, minutes, seconds, hundredths.</summary>
    public static string FormatTime(DateTime t) =>
        t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "::" +
        (t.Millisecond / 10).ToString("00", CultureInfo.InvariantCulture);

    private static readonly Regex ArrowRx = new(@"(?<=;\s+)(->|<-)\S+", RegexOptions.Compiled);

    private static readonly Regex StringRx = new("\"[^\"]*\"", RegexOptions.Compiled);

    private static readonly Regex TimingRx = new(@"^\s*'\s+\d+\.\d+\s+\d+:<", RegexOptions.Compiled);

    // ' 0:<        TaskFileExists exec= 36174 (parallel= 36166)
    private static readonly Regex PerfRowRx = new(@"^\s*' \d+:<\s+\S+ exec=", RegexOptions.Compiled);

    private static readonly Regex HeaderRx = new(
        @"^(' (Build|Branch|Release):|Dim Jrn\b|Set Jrn\b)", RegexOptions.Compiled);

    private static readonly Regex ErrorRx = new(
        @"\b(API_ERROR|error|errors|exception|fatal|crash(ed)?|failed|failure|abort(ed)?|unhandled)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarningRx = new(
        @"\b(warning|warnings|API_WARNING)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Colored spans for a line (used by the colorizer).</summary>
    public static List<JSpan> Classify(string line) => ClassifyLine(line).Spans;

    /// <summary>Filter category of a line (used by the category buttons).</summary>
    public static JKind Category(string line) => ClassifyLine(line).Category;

    public static JLineInfo ClassifyLine(string line)
    {
        var spans = new List<JSpan>();
        if (line.Length == 0) return new(JKind.Default, spans);

        string t = line.TrimStart();
        int indent = line.Length - t.Length;

        // Lines added by the viewer.
        if (t.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            return Whole(line, JKind.Marker);

        // ---- Non-comment lines are the VBScript that replays the session.
        if (!t.StartsWith('\''))
        {
            if (HeaderRx.IsMatch(line)) return Whole(line, JKind.Header);

            JKind kind =
                t.StartsWith("Jrn.Command", StringComparison.Ordinal) ? JKind.Command :
                t.StartsWith("Jrn.", StringComparison.Ordinal) ? JKind.Action :
                t.StartsWith('"') || t.StartsWith(',') ? JKind.Argument :
                JKind.Default;

            spans.Add(new JSpan(indent, t.Length, kind));
            // Quoted values stand out, except on Command lines, which are already highlighted.
            if (kind != JKind.Command)
                foreach (Match m in StringRx.Matches(line))
                    spans.Add(new JSpan(m.Index, m.Length, JKind.String));

            // Argument and other script lines filter together with Actions.
            return new(kind == JKind.Command ? JKind.Command : JKind.Action, spans);
        }

        // ---- Comment lines (everything starting with ').

        // Memory / GDI statistics and perf tables: noise, draw dim.
        if (t.Contains("Delta VM:", StringComparison.Ordinal) ||
            t.Contains("GUI Resource Usage", StringComparison.Ordinal) ||
            t.Contains("Initial VM:", StringComparison.Ordinal) ||
            PerfRowRx.IsMatch(line))
            return Whole(line, JKind.Stats);

        if (HeaderRx.IsMatch(line)) return Whole(line, JKind.Header);

        // Status coloring wins over structure.
        JKind? status =
            t.Contains("API_ERROR", StringComparison.Ordinal) ? JKind.Error :
            t.Contains("API_SUCCESS", StringComparison.Ordinal) ? JKind.Success :
            ErrorRx.IsMatch(t) ? JKind.Error :
            WarningRx.IsMatch(t) ? JKind.Warning :
            null;

        var ts = TimestampRx.Match(line);
        if (ts.Success)
        {
            spans.Add(new JSpan(indent, ts.Length - indent, JKind.Timestamp));
            int restStart = ts.Length;
            spans.Add(new JSpan(restStart, line.Length - restStart, status ?? JKind.Comment));
            if (status == null)
                foreach (Match m in ArrowRx.Matches(line))
                    spans.Add(new JSpan(m.Index, m.Length, JKind.Arrow));
            return new(status ?? JKind.Timestamp, spans);
        }

        if (status is JKind s) return Whole(line, s);

        if (t.StartsWith("' [Jrn.", StringComparison.Ordinal)) return Whole(line, JKind.JrnData);
        if (TimingRx.IsMatch(line)) return Whole(line, JKind.Timing);

        return Whole(line, JKind.Comment);
    }

    private static JLineInfo Whole(string line, JKind kind) =>
        new(kind, new List<JSpan> { new(0, line.Length, kind) });
}
