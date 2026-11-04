using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
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

    // Performance flyout
    private const double PerfPanelWidth = 321;
    private readonly ProcessMonitor _monitor = new();
    private readonly List<PerfChart> _perfCharts = new();
    private PerfChart _cpuChart = null!, _memChart = null!, _gpuChart = null!, _gpuMemChart = null!;
    private bool _perfOpen;
    private Theme _theme = Theme.Dark;
    private string _themeMode = "Auto";      // Auto follows the Windows (and Revit) theme

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
        BuildPerfCharts();
        StatusBar.Background = _statusBrush;

        // Output: read-only log view, no undo history, no clickable links.
        Output.Document.UndoStack.SizeLimit = 0;
        Output.Options.EnableHyperlinks = false;
        Output.Options.EnableEmailHyperlinks = false;
        Output.TextArea.TextView.LineTransformers.Add(_colorizer);
        ICSharpCode.AvalonEdit.Search.SearchPanel.Install(Output);   // Ctrl+F

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
        _themeMode = _settings.ThemeMode is "Dark" or "Light" ? _settings.ThemeMode : "Auto";
        ApplyTheme(Theme.ForMode(_themeMode));
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        VersionText.Text = App.VersionLabel;

        _monitor.Sampled += s => Dispatcher.InvokeAsync(() => OnPerfSample(s));
        SetPerfOpen(_settings.ShowPerformance, animate: false);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += (_, _) => { Poll(); UpdateStatusBar(); };

        // Periodically re-scan so newly launched Revit sessions show up.
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sessionTimer.Tick += (_, _) => { CheckProcessAlive(); RefreshSessions(autoAttach: true); };
    }

    // ---------------------------------------------------------------- lifecycle

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
        _monitor.Dispose();
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        SaveSettings();
    }

    // ---------------------------------------------------------------- theme

    private void ApplyTheme(Theme theme)
    {
        _theme = theme;
        theme.ApplyTo(Resources);

        _colorizer.Theme = theme;
        _margin.Theme = theme;
        foreach (var chart in _perfCharts) chart.Theme = theme;
        Output.TextArea.SelectionBrush = theme.Ui["SelectionBg"];
        Output.TextArea.SelectionForeground = null;
        Output.TextArea.SelectionBorder = null;
        Output.TextArea.Caret.CaretBrush = theme.Ui["OutputFg"];
        Output.TextArea.TextView.Redraw();

        foreach (var (kind, chip) in _chips)
            chip.Dot.Fill = theme.StyleFor(kind).Brush;

        ThemeButton.Content = $"Theme: {_themeMode}";

        UpdateStatusBar(force: true);
    }

    // ---------------------------------------------------------------- PyPiPie window chrome

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows theme changed: follow it in Auto mode.
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_themeMode == "Auto") ApplyTheme(Theme.ForMode("Auto"));
        });
    }

    private void CapMin_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void CapMax_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CapClose_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        CapMax.Content = max ? "\uE923" : "\uE922";   // restore / maximize glyphs
        CapMax.ToolTip = max ? "Restore" : "Maximize";
        // A maximized custom-chrome window hangs past the screen edge by the resize border; pad it back in.
        var b = SystemParameters.WindowResizeBorderThickness;
        RootPanel.Margin = max ? new Thickness(b.Left + 3, b.Top + 3, b.Right + 3, b.Bottom + 3) : new Thickness(0);
        BorderThickness = max ? new Thickness(0) : new Thickness(1);
    }

    private void CapHelp_Click(object sender, RoutedEventArgs e) => OpenHelp();

    private void Help_Executed(object sender, ExecutedRoutedEventArgs e) => OpenHelp();

    /// <summary>Opens help\TheRevitMatrix_Help.html (copied next to the exe) in the default browser.</summary>
    private void OpenHelp()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "help", "TheRevitMatrix_Help.html");
        if (!File.Exists(path))
        {
            SetStatus($"Help file not found: {path}");
            return;
        }
        OpenExternal(path);
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenExternal(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"Could not open {target}: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- performance flyout

    private static string FormatMB(double mb) =>
        mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";

    private void BuildPerfCharts()
    {
        _cpuChart = new PerfChart
        {
            Title = "CPU", ColorKey = "ChartCpu", FixedMax = 100,
            Subtitle = $"% of all {Environment.ProcessorCount} logical processors",
            Format = v => $"{v:0}%"
        };
        _memChart = new PerfChart
        {
            Title = "Memory", ColorKey = "ChartMemory", MinAutoMax = 1024,
            Subtitle = "committed by Revit (private bytes)",
            Format = FormatMB
        };
        _gpuChart = new PerfChart
        {
            Title = "GPU", ColorKey = "ChartGpu", FixedMax = 100,
            Subtitle = "busiest graphics engine, as in Task Manager",
            Format = v => $"{v:0}%"
        };
        _gpuMemChart = new PerfChart
        {
            Title = "GPU memory", ColorKey = "ChartGpuMemory", MinAutoMax = 256,
            Subtitle = "dedicated video memory used by Revit",
            Format = FormatMB
        };
        foreach (var chart in new[] { _cpuChart, _memChart, _gpuChart, _gpuMemChart })
        {
            chart.Margin = new Thickness(0, 0, 0, 16);
            _perfCharts.Add(chart);
            PerfCharts.Children.Add(chart);
        }

        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        PerfFooter.Text = $"Sampled every second; the graphs show the last 5 minutes. "
                        + $"This PC has {FormatMB(ram / 1024.0 / 1024.0)} of RAM.";
    }

    /// <summary>Start graphing a Revit process (or nothing, for a journal opened from a file).</summary>
    private void WatchPerformance(int? pid)
    {
        foreach (var chart in _perfCharts) chart.Clear();
        _monitor.Watch(pid);
        PerfTarget.Text = pid is int p
            ? $"Revit PID {p}"
            : "Not watching a running Revit session.";
    }

    private void OnPerfSample(PerfSample s)
    {
        if (s.Pid != _watchedPid || _processEnded) return;   // a late sample from a previous session

        _cpuChart.Push(s.CpuPercent);
        _memChart.Push(s.PrivateMB);
        _gpuChart.Push(s.GpuPercent);
        _gpuMemChart.Push(s.GpuMemoryMB);

        _cpuChart.SetUnavailable(s.CpuPercent == null && s.Note?.Contains("administrator") == true ? s.Note : null);
        bool gpuMissing = s.GpuPercent == null && s.Note?.Contains("GPU") == true;
        _gpuChart.SetUnavailable(gpuMissing ? s.Note : null);
        _gpuMemChart.SetUnavailable(gpuMissing ? "Not available on this PC." : null);

        PerfTarget.Text = s.WorkingSetMB is double ws
            ? $"Revit PID {s.Pid}  ·  {FormatMB(ws)} in RAM"
            : $"Revit PID {s.Pid}";
    }

    private void PerfToggle_Click(object sender, RoutedEventArgs e) => SetPerfOpen(!_perfOpen, animate: true);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            SetPerfOpen(!_perfOpen, animate: true);
            e.Handled = true;
        }
    }

    private void SetPerfOpen(bool open, bool animate)
    {
        _perfOpen = open;
        double to = open ? PerfPanelWidth : 0;
        if (animate)
        {
            var slide = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            PerfPanel.BeginAnimation(WidthProperty, slide);
        }
        else
        {
            PerfPanel.BeginAnimation(WidthProperty, null);
            PerfPanel.Width = to;
        }
        CapPerf.SetResourceReference(ForegroundProperty, open ? "Accent" : "Fg");
        if (IsLoaded) SaveSettings();
    }

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
        _themeMode = _themeMode switch { "Auto" => "Dark", "Dark" => "Light", _ => "Auto" };
        ApplyTheme(Theme.ForMode(_themeMode));
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.ThemeMode = _themeMode;
        _settings.ShowLineNumbers = _margin.ShowNumbers;
        _settings.ShowTimestamps = _margin.ShowTimes;
        _settings.ShowPerformance = _perfOpen;
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
            _monitor.Watch(null);
            PerfTarget.Text = $"Revit PID {pid} has exited. The graphs show its last minutes.";
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
            WatchPerformance(pid);
            Title = $"The Revit Matrix — {System.IO.Path.GetFileName(path)}";
            TitleInfo.Text = System.IO.Path.GetFileName(path) + (pid is int tp ? $"  ·  Revit PID {tp}" : "");
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
