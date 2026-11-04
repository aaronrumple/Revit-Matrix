using System.Diagnostics;
using System.IO;

namespace RevitJournalViewer;

/// <summary>A running Revit process and the journal file it is writing.</summary>
public sealed class RevitSession
{
    public required int ProcessId { get; init; }
    public required string Label { get; init; }
    public string? JournalPath { get; init; }
    /// <summary>"Revit 2026", or just "Revit" when the version can't be read.</summary>
    public string Version { get; init; } = "Revit";
    /// <summary>The active model's name from Revit's window title, if one is open.</summary>
    public string? Model { get; init; }
    public DateTime? Started { get; init; }

    public override string ToString() =>
        JournalPath is null
            ? $"{Label}  (journal not found)"
            : $"{Label}  →  {Path.GetFileName(JournalPath)}";
}

public static class RevitSessionFinder
{
    /// <summary>
    /// Root that holds "Autodesk Revit 20XX\Journals" folders for the current user.
    /// </summary>
    public static string JournalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Autodesk", "Revit");

    public static List<RevitSession> FindSessions()
    {
        var sessions = new List<RevitSession>();
        var journals = AllJournals();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Oldest process first, so each session claims the journal created right after it started.
        var procs = Process.GetProcessesByName("Revit")
            .Select(p => (Proc: p, Start: SafeStartTime(p)))
            .OrderBy(x => x.Start ?? DateTime.MaxValue)
            .ToList();

        foreach (var (p, start) in procs)
        {
            string? journal = null;
            if (start is DateTime s)
            {
                // Revit creates its journal a moment after the process starts.
                // Pick the first unclaimed journal created at/after the start time.
                journal = journals
                    .Where(j => !claimed.Contains(j.FullName))
                    .Where(j => j.CreationTime >= s.AddSeconds(-10))
                    .OrderBy(j => j.CreationTime)
                    .Select(j => j.FullName)
                    .FirstOrDefault();
            }

            if (journal != null) claimed.Add(journal);

            sessions.Add(new RevitSession
            {
                ProcessId = p.Id,
                Label = BuildLabel(p, start),
                JournalPath = journal,
                Version = VersionOf(p),
                Model = ModelOf(p),
                Started = start
            });
        }

        return sessions;
    }

    private static List<FileInfo> AllJournals()
    {
        var list = new List<FileInfo>();
        if (!Directory.Exists(JournalRoot)) return list;

        foreach (var versionDir in Directory.EnumerateDirectories(JournalRoot))
        {
            var jdir = Path.Combine(versionDir, "Journals");
            if (!Directory.Exists(jdir)) continue;
            try
            {
                list.AddRange(new DirectoryInfo(jdir).EnumerateFiles("journal.*.txt"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    private static DateTime? SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch { return null; }
    }

    private static string VersionOf(Process p)
    {
        try
        {
            var fvi = p.MainModule?.FileVersionInfo;
            if (fvi?.FileMajorPart is int major && major > 0)
                return $"Revit {2000 + major}";   // 25.x -> 2025
        }
        catch { /* access denied for elevated processes – keep generic name */ }
        return "Revit";
    }

    /// <summary>Everything after "Autodesk Revit 20XX - " in the window title, e.g. "[Model.rvt - Floor Plan: L1]".</summary>
    private static string TitleOf(Process p)
    {
        string title = "";
        try { title = p.MainWindowTitle; } catch { }
        int i = title.IndexOf(" - ", StringComparison.Ordinal);
        return i >= 0 ? title[(i + 3)..].Trim() : "";
    }

    /// <summary>The model name alone: "Model.rvt" from "[Model.rvt - Floor Plan: L1]".</summary>
    private static string? ModelOf(Process p)
    {
        string t = TitleOf(p).Trim('[', ']', ' ');
        int i = t.IndexOf(" - ", StringComparison.Ordinal);
        if (i > 0) t = t[..i];
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    private static string BuildLabel(Process p, DateTime? start)
    {
        string version = VersionOf(p), title = TitleOf(p);
        string started = start?.ToString("HH:mm:ss") ?? "?";
        return string.IsNullOrWhiteSpace(title)
            ? $"{version}  [PID {p.Id}, started {started}]"
            : $"{version}  [PID {p.Id}, started {started}]  {title}";
    }
}
