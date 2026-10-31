using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RevitJournalViewer;

public partial class MainWindow : Window
{
    private const long ExistingBytesToLoad = 2 * 1024 * 1024; // "Load existing" amount
    private const int MaxStoredLines = 200_000;               // history kept for re-filtering
    private const int MaxVisibleLines = 50_000;               // lines kept in the output view

    private sealed record Chip(ToggleButton Button, Ellipse Dot, TextBlock Count);

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _sessionTimer;
    private readonly List<JLine> _allLines = new();
    private readonly Dictionary<JKind, int> _counts = new();
    private readonly Dictionary<JKind, Chip> _chips = new();
    private readonly HashSet<JKind> _hidden = new();
    private readonly JournalColorizer _colorizer = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private Theme _theme = Theme.Dark;

    // Lines currently in the output document; document line N == _shown[N-1].
    private readonly List<JLine> _shown = new();
    private readonly LineInfoMargin _margin;
    private DateTime? _lastJournalTime;      // last 'C/'E/'H timestamp seen, carried to later lines

    private JournalTailer? _tailer;
    private int? _watchedPid;
    private bool _processEnded;
    private bool _suppressSelection;

    // Status bar state
    private readonly SolidColorBrush _statusBrush = new(Colors.Gray); // animated, so not frozen
    private string? _statusKey;
    private JKind? _flashKind;               // Error / Warning / Default (= plain activity)
    private DateTime _flashUntil;
    private DateTime? _lastActivity;
    private int _pausedNew;
    private bool _loadingExisting;           // don't flash for the backlog loaded on attach

    public MainWindow()
    {
        InitializeComponent();
        StatusBar.Background = _statusBrush;

        // Output: read-only log view, no undo history, no clickable links.
        Output.Document.UndoStack.SizeLimit = 0;
        Output.Options.EnableHyperlinks = false;
        Output.Options.EnableEmailHyperlinks = false;
        Output.TextArea.TextView.LineTransformers.Add(_colorizer);

        // Gutter with journal line numbers and timestamps.
        _margin = new LineInfoMargin(_shown)
        {
            ShowNumbers = _settings.ShowLineNumbers,
            ShowTimes = _settings.ShowTimestamps
        };
        Output.TextArea.LeftMargins.Insert(0, _margin);
        LineNumbersCheck.IsChecked = _settings.ShowLineNumbers;
        TimestampsCheck.IsChecked = _settings.ShowTimestamps;

        foreach (var name in _settings.HiddenCategories)
            if (Enum.TryParse<JKind>(name, out var k)) _hidden.Add(k);

        BuildCategoryChips();
        ApplyTheme(Theme.ByName(_settings.Theme));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += (_, _) => { Poll(); UpdateStatusBar(); };

        // Periodically re-scan so newly launched Revit sessions show up.
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sessionTimer.Tick += (_, _) => { CheckProcessAlive(); RefreshSessions(autoAttach: true); };
    }

    // ---------------------------------------------------------------- lifecycle

    private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyTitleBar();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshSessions(autoAttach: true);
        _pollTimer.Start();
        _sessionTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _pollTimer.Stop();
        _sessionTimer.Stop();
        _tailer?.Dispose();
        SaveSettings();
    }

    // ---------------------------------------------------------------- theme

    private void ApplyTheme(Theme theme)
    {
        _theme = theme;
        theme.ApplyTo(Resources);

        _colorizer.Theme = theme;
        _margin.Theme = theme;
        Output.TextArea.SelectionBrush = theme.Ui["SelectionBg"];
        Output.TextArea.SelectionForeground = null;
        Output.TextArea.SelectionBorder = null;
        Output.TextArea.Caret.CaretBrush = theme.Ui["OutputFg"];
        Output.TextArea.TextView.Redraw();

        foreach (var (kind, chip) in _chips)
            chip.Dot.Fill = theme.StyleFor(kind).Brush;

        ThemeButton.Content = theme == Theme.Dark ? "Light theme" : "Dark theme";

        ApplyTitleBar();
        UpdateStatusBar(force: true);
    }

    // ---------------------------------------------------------------- title bar (Windows DWM)

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 before 20H1
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Windows 10 20H1+ / 11
    private const int DWMWA_CAPTION_COLOR = 35;               // Windows 11
    private const int DWMWA_TEXT_COLOR = 36;                  // Windows 11

    /// <summary>
    /// Color the native title bar to match the theme. Windows 11 gets the exact theme colors;
    /// Windows 10 gets the system dark or light title bar. Older Windows ignores the calls.
    /// </summary>
    private void ApplyTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // not created yet; SourceInitialized calls again

        int dark = _theme.IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

        int caption = ToColorRef(_theme.ColorOf("WindowBg"));
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        int text = ToColorRef(_theme.ColorOf("Fg"));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16); // 0x00BBGGRR

    // ---------------------------------------------------------------- status bar

    private static int Severity(JKind k) => k switch { JKind.Error => 3, JKind.Warning => 2, _ => 1 };

    /// <summary>Briefly color the status bar for new journal activity; stronger flashes aren't overridden.</summary>
    private void Flash(JKind kind, double seconds)
    {
        if (_flashKind is JKind current && DateTime.Now < _flashUntil && Severity(current) > Severity(kind))
            return;
        _flashKind = kind;
        _flashUntil = DateTime.Now.AddSeconds(seconds);
    }

    /// <summary>
    /// Base state: IDLE (nothing attached), LIVE (streaming), PAUSED, ENDED (Revit closed).
    /// Activity flashes on top: green for new lines, amber for warnings, red for errors.
    /// </summary>
    private void UpdateStatusBar(bool force = false)
    {
        string key, label;
        bool paused = PauseToggle.IsChecked == true;

        if (_tailer == null) (key, label) = ("StatusIdle", "IDLE");
        else if (_processEnded) (key, label) = ("StatusEnded", "ENDED");
        else if (paused) (key, label) = ("StatusPaused", _pausedNew > 0 ? $"PAUSED · {_pausedNew:N0} new" : "PAUSED");
        else (key, label) = ("StatusLive", "LIVE");

        bool flashing = _tailer != null && _flashKind != null && DateTime.Now < _flashUntil;
        if (flashing)
        {
            switch (_flashKind)
            {
                case JKind.Error: key = "StatusError"; label = "ERROR"; break;
                case JKind.Warning: key = "StatusWarning"; label = "WARNING"; break;
                default:
                    if (!paused) { key = "StatusActivity"; label = "ACTIVITY"; }
                    break;
            }
        }

        if (_lastActivity is DateTime t && _tailer != null)
            label += $"  ·  last line {t:HH:mm:ss}";
        StatusState.Text = label;

        if (key == _statusKey && !force) return;
        _statusKey = key;
        var anim = new ColorAnimation(_theme.ColorOf(key), TimeSpan.FromMilliseconds(force ? 1 : 300));
        _statusBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(_theme == Theme.Dark ? Theme.Light : Theme.Dark);
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.Theme = _theme.Name;
        _settings.ShowLineNumbers = _margin.ShowNumbers;
        _settings.ShowTimestamps = _margin.ShowTimes;
        _settings.HiddenCategories = _hidden.Select(k => k.ToString()).OrderBy(s => s).ToList();
        _settings.Save();
    }

    // ---------------------------------------------------------------- category filter

    private void BuildCategoryChips()
    {
        foreach (var (kind, label, tip) in JournalClassifier.FilterCategories)
        {
            var dot = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 6, 0),
                                    VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = label };
            var count = new TextBlock { Text = "0", Margin = new Thickness(6, 0, 0, 0), Opacity = 0.65 };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            content.Children.Add(name);
            content.Children.Add(count);

            var button = new ToggleButton
            {
                Style = (Style)FindResource("ChipToggle"),
                Content = content,
                IsChecked = !_hidden.Contains(kind),
                ToolTip = tip + "\nClick: show/hide.  Right-click: show only this.",
                Tag = kind
            };
            button.Click += Chip_Click;
            button.MouseRightButtonUp += Chip_RightClick;

            CategoryPanel.Children.Add(button);
            _chips[kind] = new Chip(button, dot, count);
        }
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: JKind kind } b) return;
        if (b.IsChecked == true) _hidden.Remove(kind); else _hidden.Add(kind);
        RebuildOutput();
        SaveSettings();
    }

    private void Chip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ToggleButton { Tag: JKind kind }) return;
        SetVisibleCategories(new[] { kind });
        e.Handled = true;
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e) =>
        SetVisibleCategories(_chips.Keys);

    private void ShowProblems_Click(object sender, RoutedEventArgs e) =>
        SetVisibleCategories(new[] { JKind.Error, JKind.Warning });

    private void ShowActivity_Click(object sender, RoutedEventArgs e) =>
        SetVisibleCategories(new[] { JKind.Command, JKind.Action, JKind.Timestamp, JKind.Timing,
                                     JKind.Warning, JKind.Error });

    private void SetVisibleCategories(IEnumerable<JKind> visible)
    {
        var show = visible.ToHashSet();
        _hidden.Clear();
        foreach (var (kind, chip) in _chips)
        {
            bool on = show.Contains(kind);
            chip.Button.IsChecked = on;
            if (!on) _hidden.Add(kind);
        }
        RebuildOutput();
        SaveSettings();
    }

    private void UpdateCounts()
    {
        foreach (var (kind, chip) in _chips)
            chip.Count.Text = _counts.TryGetValue(kind, out int n) ? n.ToString("N0") : "0";
    }

    // ---------------------------------------------------------------- sessions

    private void RefreshSessions(bool autoAttach)
    {
        var sessions = RevitSessionFinder.FindSessions();

        bool idle = _tailer == null || _processEnded;
        if (sessions.Count == 0 && _tailer == null)
            SetStatus($"No running Revit found. Waiting… (journals: {RevitSessionFinder.JournalRoot})");

        var current = SessionCombo.SelectedItem as RevitSession;
        string Key(RevitSession s) => $"{s.ProcessId}|{s.JournalPath}";
        var oldKeys = SessionCombo.Items.OfType<RevitSession>().Select(Key);
        if (oldKeys.SequenceEqual(sessions.Select(Key)))
            return; // nothing changed – avoid flicker

        _suppressSelection = true;
        SessionCombo.ItemsSource = sessions;
        SessionCombo.SelectedItem = current == null
            ? null
            : sessions.FirstOrDefault(s => s.ProcessId == current.ProcessId);
        _suppressSelection = false;

        // Attach automatically to the newest session when nothing live is being watched
        // (first launch, or the watched Revit has closed).
        if (autoAttach && idle)
        {
            var target = sessions.LastOrDefault(s => s.JournalPath != null);
            if (target != null) SessionCombo.SelectedItem = target; // triggers attach
        }
    }

    private void SessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (SessionCombo.SelectedItem is not RevitSession s) return;

        if (s.JournalPath == null)
        {
            SetStatus($"Could not find a journal for PID {s.ProcessId}. Use \"Open journal file…\".");
            return;
        }
        Attach(s.JournalPath, s.ProcessId);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSessions(autoAttach: false);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Revit journal",
            Filter = "Journal files (journal*.txt)|journal*.txt|All text files (*.txt)|*.txt|All files|*.*",
            InitialDirectory = Directory.Exists(RevitSessionFinder.JournalRoot)
                ? RevitSessionFinder.JournalRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        if (dlg.ShowDialog(this) == true)
        {
            _suppressSelection = true;
            SessionCombo.SelectedItem = null;
            _suppressSelection = false;
            Attach(dlg.FileName, pid: null);
        }
    }

    private void CheckProcessAlive()
    {
        if (_watchedPid is not int pid || _processEnded) return;
        bool alive;
        try { alive = !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { alive = false; }

        if (!alive)
        {
            Poll(); // grab any final lines
            _processEnded = true;
            AddLines(new[]
            {
                new JournalLine(0, ""),
                new JournalLine(0, $"{JournalClassifier.MarkerPrefix} Revit session (PID {pid}) ended ========")
            });
            SetStatus($"Revit PID {pid} has exited. Pick another session or open a journal.");
            UpdateStatusBar();
        }
    }

    // ---------------------------------------------------------------- tailing

    private void Attach(string path, int? pid)
    {
        _tailer?.Dispose();
        _tailer = null;
        ClearAll();

        try
        {
            long start = LoadExistingCheck.IsChecked == true ? ExistingBytesToLoad : 0;
            _tailer = new JournalTailer(path, start);
            _watchedPid = pid;
            _processEnded = false;
            Title = $"The Revit Matrix — {System.IO.Path.GetFileName(path)}";
            SetStatus($"Streaming {path}" + (pid is int p ? $"  (PID {p})" : ""));
            _loadingExisting = true;
            try { Poll(); } finally { _loadingExisting = false; }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {path}: {ex.Message}");
        }
        UpdateStatusBar();
    }

    private void Poll()
    {
        if (_tailer == null) return;
        List<JournalLine> lines;
        try
        {
            lines = _tailer.ReadNewLines();
        }
        catch (Exception ex)
        {
            SetStatus($"Read error: {ex.Message}");
            return;
        }
        if (lines.Count > 0) AddLines(lines);
    }

    /// <summary>Classify, store, count and (unless paused) display new lines.</summary>
    private void AddLines(IEnumerable<JournalLine> lines)
    {
        var added = new List<JLine>();
        foreach (var (number, text) in lines)
        {
            // Time: the line's own journal timestamp, else the last one seen,
            // else (no timestamp yet, live only) the time we received it.
            DateTime? time;
            bool exact = JournalClassifier.TryGetTimestamp(text, out var ts);
            if (exact) time = _lastJournalTime = ts;
            else time = _lastJournalTime ?? (_loadingExisting ? null : DateTime.Now);

            var line = new JLine(number, text, JournalClassifier.Category(text), time, exact);
            added.Add(line);
            _counts[line.Category] = _counts.GetValueOrDefault(line.Category) + 1;
        }
        _allLines.AddRange(added);
        if (added.Count > 0) _margin.EnsureDigits(added.Max(l => l.Number));

        if (_allLines.Count > MaxStoredLines)
        {
            int drop = _allLines.Count - MaxStoredLines;
            for (int i = 0; i < drop; i++) _counts[_allLines[i].Category]--;
            _allLines.RemoveRange(0, drop);
        }

        UpdateCounts();

        bool paused = PauseToggle.IsChecked == true;
        if (paused) _pausedNew += added.Count;
        else AppendToOutput(added);

        // Status bar activity (skip the backlog loaded on attach and the viewer's own markers).
        if (!_loadingExisting && added.Any(l => l.Category is not (JKind.Default or JKind.Marker)))
        {
            _lastActivity = DateTime.Now;
            if (added.Any(l => l.Category == JKind.Error)) Flash(JKind.Error, 4);
            else if (added.Any(l => l.Category == JKind.Warning)) Flash(JKind.Warning, 3);
            else Flash(JKind.Default, 1.5);
            UpdateStatusBar();
        }
    }

    // ---------------------------------------------------------------- output

    private bool PassesFilter(JLine line)
    {
        // Blank lines and viewer markers always show; everything else obeys the chips.
        if (line.Category is not (JKind.Default or JKind.Marker) && _hidden.Contains(line.Category))
            return false;
        string f = FilterBox.Text;
        return string.IsNullOrEmpty(f) || line.Text.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private void AppendToOutput(IEnumerable<JLine> lines)
    {
        var sb = new StringBuilder();
        int before = _shown.Count;
        foreach (var line in lines)
        {
            if (!PassesFilter(line)) continue;
            sb.Append(line.Text).Append('\n');
            _shown.Add(line);
        }
        if (_shown.Count == before) return;

        bool atBottom = IsScrolledToBottom();
        var doc = Output.Document;
        doc.Insert(doc.TextLength, sb.ToString());

        // Drop the oldest lines once the view gets too long.
        int excess = _shown.Count - MaxVisibleLines;
        if (excess > 0 && excess < doc.LineCount)
        {
            doc.Remove(0, doc.GetLineByNumber(excess + 1).Offset);
            _shown.RemoveRange(0, excess);
        }

        if (AutoScrollCheck.IsChecked == true && atBottom) Output.ScrollToEnd();
    }

    /// <summary>Re-render the output from stored history using the current filters.</summary>
    private void RebuildOutput()
    {
        var visible = _allLines.Where(PassesFilter).ToList();
        if (visible.Count > MaxVisibleLines)
            visible = visible.GetRange(visible.Count - MaxVisibleLines, MaxVisibleLines);

        _shown.Clear();
        _shown.AddRange(visible);
        Output.Text = visible.Count == 0 ? "" : string.Join("\n", visible.Select(l => l.Text)) + "\n";
        _margin.InvalidateVisual();
        if (AutoScrollCheck.IsChecked == true) Output.ScrollToEnd();
    }

    private bool IsScrolledToBottom()
    {
        // Only follow the tail if the user hasn't scrolled up to read something.
        double remaining = Output.ExtentHeight - Output.VerticalOffset - Output.ViewportHeight;
        return remaining < 20 || Output.ExtentHeight <= Output.ViewportHeight;
    }

    private void ClearAll()
    {
        _allLines.Clear();
        _counts.Clear();
        UpdateCounts();
        _shown.Clear();
        _lastJournalTime = null;
        Output.Clear();
    }

    private void LineInfoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_margin == null) return; // fired during InitializeComponent
        _margin.ShowNumbers = LineNumbersCheck.IsChecked == true;
        _margin.ShowTimes = TimestampsCheck.IsChecked == true;
        if (IsLoaded) SaveSettings();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildOutput();
    }

    private void ColorsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (Output == null) return;
        var transformers = Output.TextArea.TextView.LineTransformers;
        if (ColorsCheck.IsChecked == true)
        {
            if (!transformers.Contains(_colorizer)) transformers.Add(_colorizer);
        }
        else
        {
            transformers.Remove(_colorizer);
        }
        Output.TextArea.TextView.Redraw();
    }

    private void PauseToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool paused = PauseToggle.IsChecked == true;
        PauseToggle.Content = paused ? "Resume" : "Pause";
        if (!paused)
        {
            _pausedNew = 0;
            RebuildOutput(); // catch up on everything received while paused
        }
        if (IsLoaded) UpdateStatusBar();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearAll();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save output",
            Filter = "Text files (*.txt)|*.txt",
            FileName = _tailer != null
                ? System.IO.Path.GetFileNameWithoutExtension(_tailer.Path) + "_view.txt"
                : "journal_view.txt"
        };
        if (dlg.ShowDialog(this) == true)
        {
            // Save what is shown, including the line-number and time columns that are turned on.
            var sb = new StringBuilder();
            int width = Math.Max(6, _shown.Count == 0 ? 0 : _shown.Max(l => l.Number).ToString().Length);
            foreach (var l in _shown)
            {
                if (_margin.ShowNumbers)
                    sb.Append((l.Number > 0 ? l.Number.ToString() : "").PadLeft(width)).Append("  ");
                if (_margin.ShowTimes)
                    sb.Append(l.Time is DateTime t ? JournalClassifier.FormatTime(t) : new string(' ', 12)).Append("  ");
                sb.AppendLine(l.Text);
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
            SetStatus($"Saved {dlg.FileName}");
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
