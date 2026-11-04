using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace RevitJournalViewer;

/// <summary>
/// One tab: a running Revit session, or a journal opened from a file. Everything the window
/// shows for it is kept here, and its journal keeps being read while another tab is in front.
/// </summary>
public sealed class SessionTab : IDisposable
{
    public const int MaxSamples = 300;   // performance history: 5 minutes at one sample a second

    public required string Path { get; init; }
    public int? Pid { get; init; }
    public string Version { get; init; } = "Revit";
    public string? Model { get; set; }
    public DateTime? Started { get; init; }

    public JournalTailer? Tailer { get; set; }
    public ProcessMonitor? Monitor { get; set; }

    public List<JLine> Lines { get; } = new();
    public Dictionary<JKind, int> Counts { get; } = new();
    public JournalAnalysis Analysis { get; } = new();
    public HashSet<long> Bookmarks { get; } = new();
    public DateTime? LastJournalTime { get; set; }
    public bool Ended { get; set; }
    public long? SelectedNumber { get; set; }

    /// <summary>Performance samples, with Revit's own RAM figure at that moment.</summary>
    public List<(PerfSample Sample, int? RevitRamMB)> Samples { get; } = new();

    /// <summary>New journal lines arrived while another tab was in front.</summary>
    public bool Unseen { get; set; }

    // the tab's controls
    public ToggleButton? Button { get; set; }
    public Ellipse? Dot { get; set; }
    public TextBlock? Label { get; set; }
    public TextBlock? Badge { get; set; }

    public bool IsLive => Pid != null && !Ended;

    /// <summary>Tab text: "Revit 2026 · Model.rvt", or the journal's file name for a file tab.</summary>
    public string Title =>
        Pid == null ? System.IO.Path.GetFileName(Path)
        : Model != null ? $"{Version} · {Model}"
        : $"{Version} · {System.IO.Path.GetFileName(Path)}";

    public string Description
    {
        get
        {
            var lines = new List<string>();
            if (Pid is int pid)
            {
                lines.Add($"{Version}, PID {pid}" + (Started is DateTime s ? $", started {s.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}" : ""));
                if (Model != null) lines.Add($"Model: {Model}");
                lines.Add(Ended ? "This Revit has closed." : "Running.");
            }
            else lines.Add("Journal opened from a file.");
            lines.Add(Path);
            lines.Add("Middle-click or × closes the tab.");
            return string.Join("\n", lines);
        }
    }

    public void AddSample(PerfSample sample)
    {
        Samples.Add((sample, Analysis.RevitRamMB));
        if (Samples.Count > MaxSamples) Samples.RemoveRange(0, Samples.Count - MaxSamples);
    }

    public void Dispose()
    {
        Tailer?.Dispose();
        Tailer = null;
        Monitor?.Dispose();
        Monitor = null;
    }
}
