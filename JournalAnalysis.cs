using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitJournalViewer;

/// <summary>
/// One timed operation from a journal timing line, e.g.
/// <c>'  5.375523   1:&lt;&lt;FormOrAbandon::openFromModelPath [C:\…\model.rvt]</c>.
/// Revit writes these when an operation ends (it logs anything over about 0.1 s).
/// </summary>
public sealed record SlowOp(DateTime? End, double Seconds, int Depth, string Kind, string Name, string Detail, bool UiWait, long Line)
{
    public DateTime? Start => End?.AddSeconds(-Seconds);
    public string TimeText => Start is DateTime s ? s.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";
    public string SecondsText => Seconds.ToString("0.00", CultureInfo.InvariantCulture);
    public string DepthText => Depth == 1 ? "top" : new string('·', Math.Min(Depth - 1, 8));
    public string NameText => (UiWait ? "Waiting: " : "") + Name;
    public string LineText => Line > 0 ? Line.ToString("N0", CultureInfo.InvariantCulture) : "";
}

/// <summary>
/// Add-ins that finished loading between two journal timestamps. Revit only timestamps now and
/// then while add-ins load, so add-ins it didn't time separately share one row.
/// </summary>
public sealed record AddinGroup(double Seconds, IReadOnlyList<string> Names, int? RamMB, long Line)
{
    public string NamesText => string.Join(", ", Names);
    public string SecondsText => Seconds.ToString("0.0", CultureInfo.InvariantCulture);
    public string RamText => RamMB is int r ? (r >= 0 ? "+" : "") + r.ToString("N0", CultureInfo.InvariantCulture) + " MB" : "";
    public int Count => Names.Count;
    public string LineText => Line.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>Reads journal lines as they arrive and collects what the flyout tabs and the summary show.</summary>
public sealed class JournalAnalysis
{
    private static readonly Regex TimingRx = new(
        @"^\s*'\s+(?<s>\d+\.\d+)(?<big>!!!)?\s+(?<d>\d+):(?:!!!BIG_GAP\s*|<+)\s*(?<rest>.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex GapsRx = new(@"/(ALL_GAPS|TurnOff_GAPs|Threshold_GAPs).*$", RegexOptions.Compiled);
    // a bracketed file path ("[C:\…\model.rvt]"); tags like "[Cloud]" stay part of the name
    private static readonly Regex DetailRx = new(@"\s*\[(?<p>[^\]]*[\\/.][^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex PerfTagRx = new(@";?PERF;[A-Z]+;", RegexOptions.Compiled);
    private static readonly Regex CommandRx = new(@"Jrn\.Command\s+""(?<src>[^""]*)""\s*,\s*""(?<desc>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex StartingRx = new(@"API_SUCCESS \{ Starting External (?<db>DB )?Application: (?<name>[^,]+),", RegexOptions.Compiled);
    private static readonly Regex RamRx = new(@"RAM: Avail [^,;]*, Used (?:[+-]\d+ -> )?(?<used>\d+) MB(?:, Peak (?:[+-]\d+ -> )?(?<peak>\d+))?", RegexOptions.Compiled);
    private static readonly Regex VmRx = new(@"(?:Delta|Initial) VM: Avail [^,;]*, Used (?:[+-]\d+ -> )?(?<used>\d+) MB", RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"^' (?<k>Build|Release): (?<v>.+?)\s*$", RegexOptions.Compiled);

    public List<SlowOp> SlowOps { get; } = new();
    public List<AddinGroup> Addins { get; } = new();
    public Dictionary<string, int> Commands { get; } = new();
    public int CommandCount { get; private set; }
    public List<JLine> Warnings { get; } = new();
    public List<JLine> Errors { get; } = new();
    public string? Build { get; private set; }
    public string? Release { get; private set; }
    public DateTime? First { get; private set; }
    public DateTime? Last { get; private set; }
    public int? RevitRamMB { get; private set; }
    public int? RevitRamPeakMB { get; private set; }
    public int? RevitVmMB { get; private set; }

    /// <summary>Goes up whenever something the tabs show has changed.</summary>
    public int Version { get; private set; }

    // add-in startup timing state
    private bool _addinsCounting, _addinsDone;
    private DateTime? _lastStamp;
    private double _pendingSeconds;
    private readonly List<string> _group = new();
    private long _groupLine;
    private int? _groupRamStart;

    public void Reset()
    {
        SlowOps.Clear(); Addins.Clear(); Commands.Clear(); Warnings.Clear(); Errors.Clear();
        CommandCount = 0; Build = Release = null; First = Last = null;
        RevitRamMB = RevitRamPeakMB = RevitVmMB = null;
        _addinsCounting = _addinsDone = false; _lastStamp = null; _pendingSeconds = 0;
        _group.Clear(); _groupLine = 0; _groupRamStart = null;
        Version++;
    }

    /// <summary>Take in one line. Returns the timed operation it completes, if it is a timing line.</summary>
    public SlowOp? Add(JLine line)
    {
        string text = line.Text;
        if (line.Time is DateTime t) { First ??= t; Last = t; }

        switch (line.Category)
        {
            case JKind.Warning: Warnings.Add(line); Version++; break;
            case JKind.Error: Errors.Add(line); Version++; break;
            case JKind.Command:
                var cm = CommandRx.Match(text);
                if (cm.Success)
                {
                    string desc = cm.Groups["desc"].Value;
                    int comma = desc.LastIndexOf(" , ", StringComparison.Ordinal);
                    string name = (comma > 0 ? desc[..comma] : desc.TrimStart(' ', ',')).Trim();
                    if (name.Length == 0) name = cm.Groups["src"].Value;
                    Commands[name] = Commands.GetValueOrDefault(name) + 1;
                    CommandCount++;
                    Version++;
                }
                break;
            case JKind.Header:
                var hm = HeaderRx.Match(text);
                if (hm.Success)
                {
                    if (hm.Groups["k"].Value == "Build") Build = hm.Groups["v"].Value; else Release = hm.Groups["v"].Value;
                }
                break;
        }

        // Revit's own memory figures
        if (text.Contains("VM:", StringComparison.Ordinal))
        {
            var r = RamRx.Match(text);
            if (r.Success)
            {
                RevitRamMB = int.Parse(r.Groups["used"].Value, CultureInfo.InvariantCulture);
                if (r.Groups["peak"].Success) RevitRamPeakMB = Math.Max(RevitRamPeakMB ?? 0, int.Parse(r.Groups["peak"].Value, CultureInfo.InvariantCulture));
                RevitRamPeakMB = Math.Max(RevitRamPeakMB ?? 0, RevitRamMB.Value);
            }
            var v = VmRx.Match(text);
            if (v.Success) RevitVmMB = int.Parse(v.Groups["used"].Value, CultureInfo.InvariantCulture);
        }

        TrackAddins(line);

        if (line.Category != JKind.Timing) return null;
        var m = TimingRx.Match(text);
        if (!m.Success) return null;

        string rest = GapsRx.Replace(m.Groups["rest"].Value, "");
        string detail = "";
        var dm = DetailRx.Match(rest);
        if (dm.Success) { detail = dm.Groups["p"].Value; rest = rest.Remove(dm.Index, dm.Length); }
        string opName = PerfTagRx.Replace(rest, " ").Trim(' ', ';', ':');
        if (opName.Length == 0) opName = "(unnamed)";

        var op = new SlowOp(
            line.Time,
            double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
            KindOf(opName + " " + detail),
            opName, detail,
            m.Groups["big"].Success || m.Value.Contains("BIG_GAP", StringComparison.Ordinal),
            line.Number);
        SlowOps.Add(op);
        Version++;
        return op;
    }

    /// <summary>
    /// Time between journal timestamps goes to the add-ins that finished loading in that span
    /// ("Starting External Application" is written when an add-in's start-up has finished).
    /// </summary>
    private void TrackAddins(JLine line)
    {
        if (_addinsDone) return;
        string text = line.Text;

        if (line.TimeExact && line.Time is DateTime stamp)
        {
            if (_addinsCounting && _lastStamp is DateTime prev)
            {
                double interval = Math.Max(0, (stamp - prev).TotalSeconds);
                if (_group.Count > 0) CloseGroup(_pendingSeconds + interval);
                else _pendingSeconds += interval;
            }
            _lastStamp = stamp;
            if (text.Contains("->DesktopMFCApp::doStartupWarnings", StringComparison.Ordinal))
            {
                if (_group.Count > 0) CloseGroup(_pendingSeconds);
                _addinsCounting = false;
                _addinsDone = Addins.Count > 0;
            }
        }

        if (!_addinsCounting && !_addinsDone && _lastStamp != null && text.Contains("API_SUCCESS", StringComparison.Ordinal))
        {
            _addinsCounting = true;
            _pendingSeconds = 0;
            _groupRamStart = RevitRamMB;
        }

        if (!_addinsCounting) return;
        var sm = StartingRx.Match(text);
        if (sm.Success)
        {
            if (_group.Count == 0) _groupLine = line.Number;
            _group.Add(sm.Groups["name"].Value.Trim() + (sm.Groups["db"].Success ? " (DB)" : ""));
        }
    }

    private void CloseGroup(double seconds)
    {
        int? ram = RevitRamMB is int now && _groupRamStart is int start ? now - start : null;
        Addins.Add(new AddinGroup(seconds, _group.ToList(), ram, _groupLine));
        _group.Clear();
        _pendingSeconds = 0;
        _groupRamStart = RevitRamMB;
        Version++;
    }

    public static string KindOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("synch") || n.Contains("reloadlatest") || n.Contains("swc")) return "Sync";
        if (n.Contains("save")) return "Save";
        if (n.Contains("open") || n.Contains("loadfromstorage") || n.Contains("loadserialize")) return "Open";
        if (n.Contains("regen") || n.Contains("greps")) return "Regenerate";
        if (n.Contains("draw") || n.Contains("view")) return "View";
        if (n.Contains("upgrade")) return "Upgrade";
        if (n.Contains("export") || n.Contains("print")) return "Export";
        if (n.Contains("transaction") || n.Contains("undo")) return "Transaction";
        if (n.Contains("worker") || n.Contains("initializ") || n.Contains("configure") || n.Contains("terminat")) return "Start/exit";
        return "Other";
    }

    // ---------------------------------------------------------------- session summary

    private static string Dur(double s) =>
        s >= 3600 ? $"{(int)(s / 3600)} h {(int)(s % 3600 / 60)} min" :
        s >= 60 ? $"{(int)(s / 60)} min {(int)(s % 60)} s" :
        $"{s.ToString("0.0", CultureInfo.InvariantCulture)} s";

    private static string Clip(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>Plain text, for the Summary tab and for copying into a support ticket.</summary>
    public string BuildSummary(string? journalPath, string state, double? windowsPeakMB, string appVersion)
    {
        var sb = new StringBuilder();
        string Mb(double mb) => mb >= 1024 ? $"{(mb / 1024).ToString("0.0", CultureInfo.InvariantCulture)} GB" : $"{mb:N0} MB";

        sb.AppendLine("REVIT SESSION SUMMARY");
        sb.AppendLine($"Made by The Revit Matrix {appVersion} on {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"Journal:   {journalPath ?? "(none)"}");
        if (Release != null || Build != null) sb.AppendLine($"Revit:     {Release ?? "?"} (build {Build ?? "?"})");
        if (First is DateTime f && Last is DateTime l)
            sb.AppendLine($"Session:   {f:yyyy-MM-dd HH:mm:ss} to {l:HH:mm:ss} ({Dur((l - f).TotalSeconds)}), {state}");
        sb.AppendLine();

        var top = SlowOps.Where(o => o.Depth == 1).ToList();
        void Section(string title, IEnumerable<SlowOp> ops)
        {
            var list = ops.ToList();
            sb.AppendLine($"{title} ({list.Count})");
            if (list.Count == 0) sb.AppendLine("  none");
            foreach (var o in list)
                sb.AppendLine($"  {o.TimeText}  {Dur(o.Seconds),9}  {(o.Detail.Length > 0 ? System.IO.Path.GetFileName(o.Detail) : o.Name)}");
            sb.AppendLine();
        }
        Section("Files opened", top.Where(o => o.Kind == "Open"));
        Section("Saves", top.Where(o => o.Kind == "Save"));
        Section("Syncs with central", top.Where(o => o.Kind == "Sync"));

        sb.AppendLine($"Commands run ({CommandCount:N0})");
        foreach (var c in Commands.OrderByDescending(c => c.Value).Take(10))
            sb.AppendLine($"  {c.Value,4} x  {Clip(c.Key, 90)}");
        if (CommandCount == 0) sb.AppendLine("  none");
        sb.AppendLine();

        void Problems(string title, List<JLine> lines)
        {
            sb.AppendLine($"{title} ({lines.Count})");
            if (lines.Count == 0) sb.AppendLine("  none");
            foreach (var p in lines.Take(25))
                sb.AppendLine($"  line {p.Number,6:N0}  {(p.Time is DateTime t ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "        ")}  {Clip(p.Text.TrimStart('\'', ' '), 120)}");
            if (lines.Count > 25) sb.AppendLine($"  … and {lines.Count - 25} more");
            sb.AppendLine();
        }
        Problems("Errors", Errors);
        Problems("Warnings", Warnings);

        sb.AppendLine("Slowest operations");
        foreach (var o in SlowOps.OrderByDescending(o => o.Seconds).Take(8))
            sb.AppendLine($"  {o.TimeText}  {Dur(o.Seconds),9}  {o.Kind,-11} {Clip(o.NameText + (o.Detail.Length > 0 ? " [" + System.IO.Path.GetFileName(o.Detail) + "]" : ""), 90)}");
        if (SlowOps.Count == 0) sb.AppendLine("  none recorded");
        sb.AppendLine();

        if (Addins.Count > 0)
        {
            sb.AppendLine($"Add-in startup ({Addins.Sum(a => a.Count)} add-ins, {Dur(Addins.Sum(a => a.Seconds))} in all)");
            foreach (var a in Addins.OrderByDescending(a => a.Seconds).Take(6))
                sb.AppendLine($"  {Dur(a.Seconds),9}  {Clip(a.NamesText, 100)}");
            sb.AppendLine();
        }

        sb.AppendLine("Memory");
        if (RevitRamPeakMB is int rp) sb.AppendLine($"  Peak RAM in use, as Revit reports it:  {Mb(rp)}");
        if (RevitRamMB is int rn) sb.AppendLine($"  RAM in use at the last report:         {Mb(rn)}");
        if (windowsPeakMB is double wp) sb.AppendLine($"  Peak committed memory, from Windows:   {Mb(wp)}");
        if (RevitRamPeakMB == null && windowsPeakMB == null) sb.AppendLine("  no figures yet");
        return sb.ToString();
    }
}
