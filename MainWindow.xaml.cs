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
    private readonly Dictionary<JKind, Chip> _chips = new();
    private readonly HashSet<JKind> _hidden = new();
    private readonly JournalColorizer _colorizer = new();
    private readonly AppSettings _settings = AppSettings.Load();

    // Performance flyout
    private const double PerfPanelWidth = 321;
    private readonly List<PerfChart> _perfCharts = new();
    private PerfChart _cpuChart = null!, _memChart = null!, _gpuChart = null!, _gpuMemChart = null!, _revitMemChart = null!;
    private bool _perfOpen;

    // Timeline flyout
    private const double TimelinePanelHeight = 301;

    // Bottom flyout tabs, journal analysis, bookmarks, notifications
    private string _flyoutTab = "timeline";
    private int _analysisShown = -1;          // _analysis.Version last put on screen
    private Notifier? _notifier;
    private DateTime _lastErrorNotice = DateTime.MinValue;
    private readonly Dictionary<ListView, (string Property, bool Descending)> _sorts = new();
    private bool _timelineOpen;
    private bool _follow = true;
    private int _timelineWindow = 300;        // seconds shown when following; 0 = the whole journal
    private DateTime _fullStart, _fullEnd;    // extent of the timestamped entries
    private bool _suppressFollowEvent;
    private ICSharpCode.AvalonEdit.Search.SearchPanel? _searchPanel;
    private Theme _theme = Theme.Dark;
    private string _themeMode = "Auto";      // Auto follows the Windows (and Revit) theme

    // Lines currently in the output document; document line N == _shown[N-1].
    private readonly List<JLine> _shown = new();
    private readonly LineInfoMargin _margin;

    // ---- Session tabs. Each tab keeps its own lines, counts, analysis, bookmarks and performance
    // history; these properties are the tab in front (or empty stand-ins when there is none).
    private readonly List<SessionTab> _tabs = new();
    private SessionTab? _cur;
    private readonly HashSet<int> _dismissedPids = new();   // live sessions whose tab was closed
    private readonly List<JLine> _noLines = new();
    private readonly Dictionary<JKind, int> _noCounts = new();
    private readonly JournalAnalysis _noAnalysis = new();
    private readonly HashSet<long> _noBookmarks = new();

    private List<JLine> _allLines => _cur?.Lines ?? _noLines;
    private Dictionary<JKind, int> _counts => _cur?.Counts ?? _noCounts;
    private JournalAnalysis _analysis => _cur?.Analysis ?? _noAnalysis;
    private HashSet<long> _bookmarks => _cur?.Bookmarks ?? _noBookmarks;
    private string? _journalPath => _cur?.Path;
    private JournalTailer? _tailer => _cur?.Tailer;
    private int? _watchedPid => _cur?.Pid;
    private bool _processEnded => _cur?.Ended ?? false;

    // Status bar state
    private readonly SolidColorBrush _statusBrush = new(Colors.Gray); // animated, so not frozen
    private string? _statusKey;
    private JKind? _flashKind;               // Error / Warning / Default (= plain activity)
    private DateTime _flashUntil;
    private DateTime? _lastActivity;
    private int _pausedNew;

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
        _searchPanel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(Output);   // Ctrl+F

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

        SetPerfOpen(_settings.ShowPerformance, animate: false);
        InitTimeline();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += (_, _) =>
        {
            Poll();
            UpdateStatusBar();
            if (_timelineOpen && _follow && TimelinePlot.LiveNow) UpdateTimeline();
        };

        // Periodically re-scan so newly launched Revit sessions show up.
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sessionTimer.Tick += (_, _) => { CheckProcessAlive(); RefreshSessions(); };
    }

    // ---------------------------------------------------------------- lifecycle

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshSessions();
        _pollTimer.Start();
        _sessionTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _pollTimer.Stop();
        _sessionTimer.Stop();
        foreach (var tab in _tabs) tab.Dispose();
        _notifier?.Dispose();
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
        TimelinePlot.Theme = theme;
        TimelineRange.Theme = theme;
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

    private void CapClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

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

    // ---------------------------------------------------------------- timeline flyout

    private void InitTimeline()
    {
        TimelinePlot.SetData(_allLines, _hidden);
        TimelinePlot.EntryClicked += JumpToLine;
        TimelinePlot.ViewChangedByUser += OnTimelineViewChangedByUser;
        TimelineRange.RangeChangedByUser += OnTimelineRangeDragged;

        _timelineWindow = _settings.TimelineWindowSeconds is 60 or 300 or 900 or 0 ? _settings.TimelineWindowSeconds : 300;
        foreach (var b in WindowButtons.Children.OfType<ToggleButton>())
            b.IsChecked = int.Parse((string)b.Tag) == _timelineWindow;

        NotifyCheck.IsChecked = _settings.Notify;
        _margin.Bookmarks = _bookmarks;
        SetFoldersSplit(_settings.FoldersSplit);
        SelectFlyoutTab(_settings.FlyoutTab is "timeline" or "slow" or "addins" or "summary" or "folders" ? _settings.FlyoutTab : "timeline");
        SetTimelineOpen(_settings.ShowTimeline, animate: false);
    }

    private void TimelineToggle_Click(object sender, RoutedEventArgs e) => SetTimelineOpen(!_timelineOpen, animate: true);

    private void SetTimelineOpen(bool open, bool animate)
    {
        _timelineOpen = open;
        double to = open ? TimelinePanelHeight : 0;
        if (animate)
        {
            var slide = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            TimelinePanel.BeginAnimation(HeightProperty, slide);
        }
        else
        {
            TimelinePanel.BeginAnimation(HeightProperty, null);
            TimelinePanel.Height = to;
        }
        CapTimeline.SetResourceReference(ForegroundProperty, open ? "Accent" : "Fg");
        if (open) { UpdateTimeline(); RefreshFlyoutTab(); }
        if (IsLoaded) SaveSettings();
    }

    /// <summary>Recompute the extent, move the view when following, refresh the range and labels.</summary>
    private void UpdateTimeline()
    {
        bool live = _tailer != null && _watchedPid != null && !_processEnded;
        TimelinePlot.LiveNow = live;

        var (first, last) = TimelinePlot.Extent();
        if (first is not DateTime start || last is not DateTime end)
        {
            TimelinePlot.ClearView();
            TimelineSummary.Text = _allLines.Count == 0 ? "No journal entries yet." : $"{_allLines.Count:N0} journal entries, none timestamped yet";
            TimelineRangeText.Text = "";
            TimelineRange.Set(0, 1);
            return;
        }

        if (live && DateTime.Now > end) end = DateTime.Now;
        if (end <= start) end = start.AddSeconds(1);
        _fullStart = start;
        _fullEnd = end;

        if (_follow || !TimelinePlot.HasView)
        {
            var viewStart = _timelineWindow > 0 ? end.AddSeconds(-_timelineWindow) : start;
            TimelinePlot.SetView(viewStart, end);
        }
        else
        {
            TimelinePlot.InvalidateVisual();
        }

        TimelineSummary.Text = $"{_allLines.Count:N0} journal entries  ·  click a dot to jump to its line  ·  wheel to zoom at the pointer  ·  middle-drag to move";
        UpdateTimelineRange();
    }

    private void UpdateTimelineRange()
    {
        double span = Math.Max(0.001, (_fullEnd - _fullStart).TotalSeconds);
        TimelineRange.Set((TimelinePlot.ViewStart - _fullStart).TotalSeconds / span,
                          (TimelinePlot.ViewEnd - _fullStart).TotalSeconds / span);
        TimelineRangeText.Text = $"{TimelinePlot.ViewStart:HH:mm:ss} – {TimelinePlot.ViewEnd:HH:mm:ss}";
    }

    private void SetFollow(bool follow)
    {
        _follow = follow;
        _suppressFollowEvent = true;
        FollowCheck.IsChecked = follow;
        _suppressFollowEvent = false;
    }

    private void FollowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFollowEvent || !IsLoaded) return;
        _follow = FollowCheck.IsChecked == true;
        UpdateTimeline();
    }

    private void WindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        _timelineWindow = int.Parse((string)clicked.Tag);
        foreach (var b in WindowButtons.Children.OfType<ToggleButton>()) b.IsChecked = b == clicked;
        SetFollow(true);           // choosing a window shows the newest entries again
        UpdateTimeline();
        SaveSettings();
    }

    private void OnTimelineViewChangedByUser()
    {
        SetFollow(false);
        UpdateTimelineRange();
    }

    private void OnTimelineRangeDragged()
    {
        SetFollow(false);
        double span = (_fullEnd - _fullStart).TotalSeconds;
        TimelinePlot.SetView(_fullStart.AddSeconds(TimelineRange.Lower * span), _fullStart.AddSeconds(TimelineRange.Upper * span));
        TimelineRangeText.Text = $"{TimelinePlot.ViewStart:HH:mm:ss} – {TimelinePlot.ViewEnd:HH:mm:ss}";
    }

    /// <summary>A timeline dot was clicked: scroll the log to that line and highlight it.</summary>
    private void JumpToLine(JLine line)
    {
        string label = JournalClassifier.FilterCategories.FirstOrDefault(c => c.Kind == line.Category).Label ?? line.Category.ToString();
        string where = line.Number > 0 ? $"line {line.Number:N0}" : "that entry";

        int index = _shown.FindIndex(l => l.Number == line.Number && l.Text == line.Text);
        if (index < 0)
        {
            SetStatus(_hidden.Contains(line.Category)
                ? $"{where[..1].ToUpper()}{where[1..]} is in a hidden category ({label}). Turn it on under Show to see it in the log."
                : $"{where[..1].ToUpper()}{where[1..]} is hidden by the text filter.");
            return;
        }

        SelectShownLine(index);
        if (_cur != null) _cur.SelectedNumber = line.Number;
        SetStatus($"Jumped to {where} ({label}{(line.Time is DateTime t ? ", " + JournalClassifier.FormatTime(t) : "")}).");
    }

    /// <summary>Highlight line <paramref name="index"/> of the log (0-based) and scroll to it.</summary>
    private void SelectShownLine(int index)
    {
        var docLine = Output.Document.GetLineByNumber(index + 1);
        Output.Select(docLine.Offset, docLine.Length);
        Output.TextArea.Caret.Offset = docLine.Offset;
        Output.ScrollTo(index + 1, 0);
    }

    /// <summary>Jump to a journal line by its file line number (from the lists in the flyout).</summary>
    private void JumpToNumber(long number)
    {
        if (number <= 0) return;
        int i = _allLines.FindIndex(l => l.Number == number);
        if (i >= 0) JumpToLine(_allLines[i]);
        else SetStatus($"Line {number:N0} is no longer in memory.");
    }

    // ---------------------------------------------------------------- next/previous problem, bookmarks

    private int CaretIndex => Math.Max(0, Output.TextArea.Caret.Line - 1);

    private void GoToNext(Func<JLine, bool> match, bool backwards, string what)
    {
        if (_shown.Count == 0) return;
        int start = CaretIndex;
        for (int step = 1; step <= _shown.Count; step++)
        {
            int i = backwards ? start - step : start + step;
            if (i < 0 || i >= _shown.Count) break;
            if (match(_shown[i]))
            {
                SelectShownLine(i);
                var l = _shown[i];
                SetStatus($"{(backwards ? "Previous" : "Next")} {what}: line {l.Number:N0}.");
                return;
            }
        }
        SetStatus($"No {(backwards ? "earlier" : "later")} {what} in the log.");
    }

    private void ToggleBookmark()
    {
        if (_shown.Count == 0) return;
        var line = _shown[Math.Min(CaretIndex, _shown.Count - 1)];
        if (line.Number <= 0) return;
        bool on = _bookmarks.Add(line.Number);
        if (!on) _bookmarks.Remove(line.Number);
        _margin.InvalidateVisual();
        SetStatus(on ? $"Bookmarked line {line.Number:N0}. F9 / Shift+F9 move between bookmarks." : $"Removed the bookmark on line {line.Number:N0}.");
    }

    // ---------------------------------------------------------------- notifications

    private void NotifyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    /// <summary>While the app is in the background: notify on errors and on long opens, saves and syncs.</summary>
    private void MaybeNotify(SessionTab tab, JLine line, SlowOp? op)
    {
        // Only when nobody is looking at this session: the app is in the background, or the tab isn't in front.
        if (NotifyCheck.IsChecked != true || (IsActive && tab == _cur)) return;
        string who = _tabs.Count > 1 ? tab.Title + ": " : "";
        if (line.Category == JKind.Error && DateTime.Now - _lastErrorNotice > TimeSpan.FromSeconds(30))
        {
            _lastErrorNotice = DateTime.Now;
            (_notifier ??= new Notifier(this)).Show(who + "Revit logged an error", line.Text.Trim().TrimStart('\'', ' '));
        }
        else if (op is { Depth: 1, Seconds: >= 10 } && op.Kind is "Open" or "Save" or "Sync")
        {
            string what = op.Kind == "Sync" ? "Sync with central" : op.Kind;
            string file = op.Detail.Length > 0 ? System.IO.Path.GetFileName(op.Detail) : op.Name;
            (_notifier ??= new Notifier(this)).Show($"{who}{what} finished in {op.Seconds:0.0} s", file);
        }
    }

    // ---------------------------------------------------------------- bottom flyout tabs

    private void FlyoutTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tab }) SelectFlyoutTab(tab);
        SaveSettings();
    }

    private void SelectFlyoutTab(string tab)
    {
        _flyoutTab = tab;
        foreach (var b in FlyoutTabs.Children.OfType<ToggleButton>()) b.IsChecked = (string)b.Tag == tab;
        TabTimeline.Visibility = tab == "timeline" ? Visibility.Visible : Visibility.Collapsed;
        TabSlow.Visibility = tab == "slow" ? Visibility.Visible : Visibility.Collapsed;
        TabAddins.Visibility = tab == "addins" ? Visibility.Visible : Visibility.Collapsed;
        TabSummary.Visibility = tab == "summary" ? Visibility.Visible : Visibility.Collapsed;
        TabFolders.Visibility = tab == "folders" ? Visibility.Visible : Visibility.Collapsed;
        TimelineControls.Visibility = tab == "timeline" ? Visibility.Visible : Visibility.Collapsed;
        _analysisShown = -1;
        if (_timelineOpen) { if (tab == "timeline") UpdateTimeline(); else RefreshFlyoutTab(); }
    }

    /// <summary>Put the open tab's data on screen (lists only when the analysis changed).</summary>
    private void RefreshFlyoutTab()
    {
        switch (_flyoutTab)
        {
            case "slow":
                if (_analysisShown == _analysis.Version) return;
                _analysisShown = _analysis.Version;
                var ops = NestedCheck.IsChecked == true ? _analysis.SlowOps : _analysis.SlowOps.Where(o => o.Depth == 1).ToList();
                SetItems(SlowList, ops, "Seconds", true);
                double total = ops.Where(o => o.Depth == 1).Sum(o => o.Seconds);
                SlowInfo.Text = ops.Count == 0
                    ? "No timed operations yet. Revit logs any operation that takes more than about 0.1 second."
                    : $"{ops.Count:N0} operations, {total:0.0} s in top-level operations. Click a column to sort; click a row to jump to its line.";
                break;

            case "addins":
                if (_analysisShown == _analysis.Version) return;
                _analysisShown = _analysis.Version;
                SetItems(AddinList, _analysis.Addins, "Seconds", true);
                int count = _analysis.Addins.Sum(a => a.Count);
                AddinInfo.Text = count == 0
                    ? "No add-in startup found. It's at the start of the journal: open the session's journal with Load existing on, or from the start with Open journal file…"
                    : $"{count} add-ins loaded in {_analysis.Addins.Sum(a => a.Seconds):0.0} s. Times are approximate: Revit timestamps only now and then while add-ins load, "
                      + "so add-ins it didn't time separately share a row. Memory added is Revit's own figure.";
                break;

            case "summary":
                SummaryBox.Text = BuildSummary();
                break;

            case "folders":
                RefreshFolders();
                break;
        }
    }

    private string BuildSummary()
    {
        string state = _tailer == null ? "not attached" : _watchedPid == null ? "opened from a file" : _processEnded ? "Revit has closed" : "Revit still running";
        return _analysis.BuildSummary(_journalPath, state, _memChart.Peak, App.VersionLabel);
    }

    private void NestedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _analysisShown = -1;
        RefreshFlyoutTab();
    }

    /// <summary>Show items in a list, keeping the user's sort (default: <paramref name="property"/>).</summary>
    private void SetItems<T>(ListView list, IEnumerable<T> items, string property, bool descending)
    {
        var selected = list.SelectedItem;
        list.ItemsSource = items.ToList();
        if (!_sorts.ContainsKey(list)) _sorts[list] = (property, descending);
        ApplySort(list);
        if (selected != null) list.SelectedItem = selected;
    }

    private void ApplySort(ListView list)
    {
        if (list.ItemsSource == null || !_sorts.TryGetValue(list, out var sort)) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(list.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(sort.Property,
            sort.Descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending));
    }

    // Column header -> the property to sort by (the shown text columns sort by their underlying values).
    private static readonly Dictionary<string, string> SortKeys = new()
    {
        ["Started"] = "Start", ["Seconds"] = "Seconds", ["Kind"] = "Kind", ["Level"] = "Depth",
        ["Operation"] = "Name", ["File"] = "Detail", ["Line"] = "Line",
        ["Memory added"] = "RamMB", ["Add-ins"] = "NamesText",
        ["Journal"] = "Name", ["Revit"] = "Version", ["Modified"] = "Modified", ["Age"] = "Modified",
        ["Size"] = "Bytes", ["In use"] = "State"
    };

    private void List_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView list || e.OriginalSource is not GridViewColumnHeader { Column.Header: string header }) return;
        if (!SortKeys.TryGetValue(header, out var property)) return;
        bool descending = !(_sorts.TryGetValue(list, out var cur) && cur.Property == property && cur.Descending);
        if (!_sorts.ContainsKey(list) || _sorts[list].Property != property) descending = property is "Seconds" or "RamMB" or "Bytes" or "Modified";
        _sorts[list] = (property, descending);
        ApplySort(list);
    }

    private void SlowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlowList.SelectedItem is SlowOp op) JumpToNumber(op.Line);
    }

    private void AddinList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddinList.SelectedItem is AddinGroup g) JumpToNumber(g.Line);
    }

    private void SummaryRefresh_Click(object sender, RoutedEventArgs e) => SummaryBox.Text = BuildSummary();

    private void SummaryCopy_Click(object sender, RoutedEventArgs e)
    {
        SummaryBox.Text = BuildSummary();
        try { Clipboard.SetText(SummaryBox.Text); SetStatus("Session summary copied to the clipboard."); }
        catch (Exception ex) { SetStatus($"Could not copy: {ex.Message}"); }
    }

    private void SummarySave_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save session summary",
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"Revit session summary {DateTime.Now:yyyy-MM-dd HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, BuildSummary());
        SetStatus($"Saved {dlg.FileName}");
    }

    // ---------------------------------------------------------------- journal folders

    private int Days => int.TryParse(DaysBox.Text, out int d) && d >= 1 ? d : 30;

    private void RefreshFolders()
    {
        var keepSelected = FolderList.SelectedItems.Cast<JournalFolderInfo>().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = JournalFolders.Scan(Days);
        _refreshingFolders = true;
        FolderList.ItemsSource = folders;
        foreach (var f in folders.Where(f => keepSelected.Contains(f.Path))) FolderList.SelectedItems.Add(f);
        _refreshingFolders = false;
        RefreshFiles();
        long bytes = folders.Sum(f => f.Bytes);
        int oldFiles = folders.Sum(f => f.OldFiles);
        FoldersOldText.Text = oldFiles == 0 ? "none" : $"{oldFiles:N0} journals, {JournalFolders.Size(folders.Sum(f => f.OldBytes))}";
        RecycleButton.IsEnabled = oldFiles > 0;
        FoldersTotal.Text = $"All journals: {folders.Sum(f => f.Files):N0} files, {JournalFolders.Size(bytes)}";
    }

    private void DaysBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && _flyoutTab == "folders") RefreshFolders();
    }

    private void FoldersRefresh_Click(object sender, RoutedEventArgs e) => RefreshFolders();

    private void Recycle_Click(object sender, RoutedEventArgs e)
    {
        var folders = JournalFolders.Scan(Days);
        int n = folders.Sum(f => f.OldFiles);
        if (n == 0) return;
        var answer = MessageBox.Show(this,
            $"Move {n:N0} journals older than {Days} days ({JournalFolders.Size(folders.Sum(f => f.OldBytes))}) to the Recycle Bin?\n\n"
            + "Journals a running Revit has open, and those open in a tab here, are kept.",
            "The Revit Matrix", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var (moved, bytes, skipped) = JournalFolders.RecycleOld(Days, _tabs.Select(t => t.Path).ToList());
        SetStatus($"Moved {moved:N0} journals ({JournalFolders.Size(bytes)}) to the Recycle Bin" + (skipped > 0 ? $"; kept {skipped} in use." : "."));
        RefreshFolders();
    }

    private void FolderList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is JournalFolderInfo f) OpenExternal(f.Path);
    }

    private bool _refreshingFolders;

    /// <summary>Set the splitter between the folder and journal lists: <paramref name="share"/> of the width for folders.</summary>
    private void SetFoldersSplit(double share)
    {
        share = double.IsFinite(share) ? Math.Clamp(share, 0.1, 0.9) : 0.4;
        FoldersColumn.Width = new GridLength(share, GridUnitType.Star);
        FilesColumn.Width = new GridLength(1 - share, GridUnitType.Star);
    }

    private double FoldersSplitShare()
    {
        double left = FoldersColumn.ActualWidth, right = FilesColumn.ActualWidth;
        if (left + right > 0) return left / (left + right);
        // not laid out yet (tab never shown): keep the stored star ratio
        double l = FoldersColumn.Width.Value, r = FilesColumn.Width.Value;
        return l + r > 0 ? l / (l + r) : 0.4;
    }

    private void FoldersSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SetFoldersSplit(FoldersSplitShare());   // back to star widths so both lists keep their share as the window resizes
        SaveSettings();
    }

    private void FoldersSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        SetFoldersSplit(0.4);
        SaveSettings();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingFolders) RefreshFiles();
    }

    /// <summary>List the journals of the selected folders (all folders when none is selected).</summary>
    private void RefreshFiles()
    {
        var all = FolderList.ItemsSource as IEnumerable<JournalFolderInfo> ?? Enumerable.Empty<JournalFolderInfo>();
        var chosen = FolderList.SelectedItems.Cast<JournalFolderInfo>().ToList();
        var folders = chosen.Count > 0 ? chosen : all.ToList();

        var inRevit = RevitSessionFinder.FindSessions().Where(s => s.JournalPath != null).Select(s => s.JournalPath!).ToList();
        var inTabs = _tabs.Select(t => t.Path).ToList();
        var keepSelected = FileList.SelectedItems.Cast<JournalFileInfo>().Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = JournalFolders.ScanFiles(folders, Days, inRevit, inTabs);
        FileList.ItemsSource = files;
        if (!_sorts.ContainsKey(FileList)) _sorts[FileList] = ("Modified", true);
        ApplySort(FileList);
        foreach (var f in files.Where(f => keepSelected.Contains(f.Path))) FileList.SelectedItems.Add(f);
        UpdateFilesInfo();
    }

    private void UpdateFilesInfo()
    {
        var files = FileList.ItemsSource as IEnumerable<JournalFileInfo> ?? Enumerable.Empty<JournalFileInfo>();
        var selected = FileList.SelectedItems.Cast<JournalFileInfo>().ToList();
        int listed = files.Count();
        string scope = FolderList.SelectedItems.Count == 0 ? "all folders" : FolderList.SelectedItems.Count == 1 ? "1 folder" : $"{FolderList.SelectedItems.Count} folders";
        FilesInfo.Text = selected.Count > 0
            ? $"{selected.Count:N0} of {listed:N0} selected, {JournalFolders.Size(selected.Sum(f => f.Bytes))}"
            : $"{listed:N0} journals in {scope}. Ctrl+click or Shift+click to select several.";
        RecycleSelectedButton.IsEnabled = selected.Any(f => f.State.Length == 0);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateFilesInfo();

    private void SelectOld_Click(object sender, RoutedEventArgs e)
    {
        FileList.SelectedItems.Clear();
        foreach (var f in (FileList.ItemsSource as IEnumerable<JournalFileInfo> ?? Enumerable.Empty<JournalFileInfo>()).Where(f => f.Old && f.State.Length == 0))
            FileList.SelectedItems.Add(f);
        FileList.Focus();
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && RecycleSelectedButton.IsEnabled) { RecycleSelected_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { FileList.SelectAll(); e.Handled = true; }
    }

    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // only on a row, not the header or empty space
        if (e.OriginalSource is DependencyObject d && FindParent<ListViewItem>(d) == null) return;
        if (FileList.SelectedItem is JournalFileInfo f) OpenJournalInTab(f.Path, select: true);
    }

    private void OpenJournalInTab(string path, bool select)
    {
        var open = _tabs.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (open != null) { if (select) SelectTab(open); }
        else OpenTab(path, null, select);
    }

    // ---- right-click menu on a journal (the row under the pointer is selected by the right-click)

    private List<JournalFileInfo> SelectedJournals => FileList.SelectedItems.Cast<JournalFileInfo>().ToList();

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var sel = SelectedJournals;
        int n = sel.Count;
        foreach (var item in FileMenu.Items.OfType<MenuItem>()) item.IsEnabled = n > 0;
        FileMenuOpen.Header = n > 1 ? $"Open {n} journals in tabs" : "Open in a tab";
        FileMenuEditor.Header = n > 1 ? $"Open {n} journals in text editor" : "Open in text editor";
        FileMenuEditor.IsEnabled = n is > 0 and <= 10;
        FileMenuFolder.IsEnabled = n > 0;
        FileMenuCopyPath.Header = n > 1 ? $"Copy {n} paths" : "Copy path";
        int allowed = sel.Count(f => f.State.Length == 0);
        FileMenuRecycle.Header = n > 1 ? $"Move {allowed} journals to Recycle Bin" : "Move to Recycle Bin";
        FileMenuRecycle.IsEnabled = allowed > 0;
        FileMenuRecycle.ToolTip = allowed < n ? "Journals a running Revit or a tab here has open are kept." : null;
    }

    private void FileMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedJournals;
        for (int i = 0; i < sel.Count; i++) OpenJournalInTab(sel[i].Path, select: i == sel.Count - 1);
    }

    private void FileMenuEditor_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in SelectedJournals.Take(10)) OpenExternal(f.Path);   // the .txt's default app, usually Notepad
    }

    private void FileMenuFolder_Click(object sender, RoutedEventArgs e)
    {
        // Explorer opens the folder with the journal selected (one window per folder).
        foreach (var f in SelectedJournals.GroupBy(f => System.IO.Path.GetDirectoryName(f.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            try { Process.Start("explorer.exe", $"/select,\"{f.Path}\""); }
            catch (Exception ex) { SetStatus($"Could not open the folder: {ex.Message}"); }
        }
    }

    private void FileMenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedJournals;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(f => f.Path)));
            SetStatus(sel.Count == 1 ? $"Copied {sel[0].Path}" : $"Copied {sel.Count} paths.");
        }
        catch (Exception ex) { SetStatus($"Could not copy: {ex.Message}"); }
    }

    private void RecycleSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = FileList.SelectedItems.Cast<JournalFileInfo>().ToList();
        var allowed = selected.Where(f => f.State.Length == 0).ToList();
        if (allowed.Count == 0) return;
        string what = allowed.Count == 1 ? allowed[0].Name : $"{allowed.Count:N0} journals";
        string kept = selected.Count > allowed.Count ? $"\n\n{selected.Count - allowed.Count} selected journal(s) in use will be kept." : "";
        var answer = MessageBox.Show(this,
            $"Move {what} ({JournalFolders.Size(allowed.Sum(f => f.Bytes))}) to the Recycle Bin?{kept}",
            "The Revit Matrix", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        var (moved, bytes, skipped) = JournalFolders.RecycleFiles(allowed.Select(f => f.Path), _tabs.Select(t => t.Path).ToList());
        skipped += selected.Count - allowed.Count;
        SetStatus($"Moved {moved:N0} journal{(moved == 1 ? "" : "s")} ({JournalFolders.Size(bytes)}) to the Recycle Bin" + (skipped > 0 ? $"; kept {skipped} in use." : "."));
        RefreshFolders();
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
        _revitMemChart = new PerfChart
        {
            Title = "Revit's own memory figure", ColorKey = "ChartMemory", MinAutoMax = 1024,
            Subtitle = "RAM in use, as Revit reports it in the journal",
            Format = FormatMB
        };
        foreach (var chart in new[] { _cpuChart, _memChart, _revitMemChart, _gpuChart, _gpuMemChart })
        {
            chart.Margin = new Thickness(0, 0, 0, 16);
            _perfCharts.Add(chart);
            PerfCharts.Children.Add(chart);
        }

        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        PerfFooter.Text = $"Sampled every second; the graphs show the last 5 minutes. "
                        + $"This PC has {FormatMB(ram / 1024.0 / 1024.0)} of RAM.";
    }

    /// <summary>A sample from one tab's monitor: keep it with the tab; show it if that tab is in front.</summary>
    private void OnPerfSample(SessionTab tab, PerfSample s)
    {
        if (tab.Ended || !_tabs.Contains(tab)) return;   // a late sample from a closed or ended session
        tab.AddSample(s);
        if (tab == _cur) ShowSample(s, tab.Analysis.RevitRamMB);
    }

    private void ShowSample(PerfSample s, int? revitRam)
    {
        _cpuChart.Push(s.CpuPercent);
        _memChart.Push(s.PrivateMB);
        _revitMemChart.Push(revitRam);
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

    /// <summary>Redraw the graphs from the tab's stored history (on switching tabs).</summary>
    private void ShowPerformanceOf(SessionTab? tab)
    {
        foreach (var chart in _perfCharts) chart.Clear();
        if (tab == null) { PerfTarget.Text = "Not watching a running Revit session."; return; }
        foreach (var (sample, ram) in tab.Samples) ShowSample(sample, ram);
        if (tab.Pid == null) PerfTarget.Text = "A journal opened from a file has no running Revit to graph.";
        else if (tab.Ended) PerfTarget.Text = $"Revit PID {tab.Pid} has exited. The graphs show its last minutes.";
        else if (tab.Samples.Count == 0) PerfTarget.Text = $"Revit PID {tab.Pid}";
    }

    private void PerfToggle_Click(object sender, RoutedEventArgs e) => SetPerfOpen(!_perfOpen, animate: true);

    // ---------------------------------------------------------------- right-click menu

    private bool _menuOnLog;   // the menu was opened over the log: show the line items

    private void Window_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Text boxes keep their own Cut/Copy/Paste menu.
        if (e.OriginalSource is DependencyObject src && FindParent<TextBox>(src) != null) { e.Handled = true; return; }

        _menuOnLog = e.OriginalSource is DependencyObject d && (d == Output || Output.IsAncestorOf(d));
        if (_menuOnLog && Output.SelectionLength == 0)
        {
            // Put the caret on the line under the pointer, so "Bookmark this line" means that line.
            var pos = Output.GetPositionFromPoint(Mouse.GetPosition(Output));
            if (pos is { } p) Output.TextArea.Caret.Position = p;
        }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void MainMenu_Opened(object sender, RoutedEventArgs e)
    {
        foreach (var item in new[] { MenuTimeline, MenuSlow, MenuAddins, MenuSummary, MenuFolders })
        {
            item.IsChecked = _timelineOpen && _flyoutTab == (string)item.Tag;
        }
        MenuPerf.IsChecked = _perfOpen;
        MenuStart.IsEnabled = MenuCurrent.IsEnabled = Output.Document.TextLength > 0;

        var lineItems = _menuOnLog ? Visibility.Visible : Visibility.Collapsed;
        MenuLogSeparator.Visibility = MenuBookmark.Visibility = MenuCopy.Visibility = MenuSelectAll.Visibility = lineItems;
        if (_menuOnLog)
        {
            long number = _shown.Count == 0 ? 0 : _shown[Math.Min(CaretIndex, _shown.Count - 1)].Number;
            MenuBookmark.IsEnabled = number > 0;
            MenuBookmark.Header = number <= 0 ? "Bookmark this line"
                : _bookmarks.Contains(number) ? $"Remove bookmark on line {number:N0}" : $"Bookmark line {number:N0}";
            MenuCopy.IsEnabled = Output.SelectionLength > 0;
            MenuSelectAll.IsEnabled = Output.Document.TextLength > 0;
        }
    }

    /// <summary>A bottom-panel tab: open the panel on it, or close the panel if it's already showing.</summary>
    private void MenuFlyoutTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tab }) return;
        if (_timelineOpen && _flyoutTab == tab) SetTimelineOpen(false, animate: true);
        else
        {
            SelectFlyoutTab(tab);
            if (!_timelineOpen) SetTimelineOpen(true, animate: true);
        }
        SaveSettings();
    }

    private void MenuPerf_Click(object sender, RoutedEventArgs e) => SetPerfOpen(!_perfOpen, animate: true);
    private void MenuBookmark_Click(object sender, RoutedEventArgs e) => ToggleBookmark();
    private void MenuCopy_Click(object sender, RoutedEventArgs e) => Output.Copy();
    private void MenuSelectAll_Click(object sender, RoutedEventArgs e) => Output.SelectAll();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleLogNavigation(e)) { e.Handled = true; return; }
        if (e.Key == Key.F2)
        {
            SetPerfOpen(!_perfOpen, animate: true);
            e.Handled = true;
        }
        else if (e.Key == Key.F8 && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            GoToNext(l => l.Category is JKind.Error or JKind.Warning, Keyboard.Modifiers == ModifierKeys.Shift, "error or warning");
            e.Handled = true;
        }
        else if (e.Key == Key.F9 && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            GoToNext(l => l.Number > 0 && _bookmarks.Contains(l.Number), Keyboard.Modifiers == ModifierKeys.Shift, "bookmark");
            e.Handled = true;
        }
        else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleBookmark();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && Keyboard.Modifiers == ModifierKeys.None
                 && (_searchPanel == null || _searchPanel.IsClosed))   // F3 = find next while searching
        {
            SetTimelineOpen(!_timelineOpen, animate: true);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Home, End, Page Up, Page Down and the up and down arrows move through the log wherever the
    /// focus is, except in a text box or list (where they keep their usual meaning). In the log itself
    /// the arrows and pages work as usual, and Home and End go to the first and latest line.
    /// Shift extends the selection.
    /// </summary>
    private bool HandleLogNavigation(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Up or Key.Down)) return false;
        var mods = Keyboard.Modifiers;
        if ((mods & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        bool shift = mods.HasFlag(ModifierKeys.Shift);

        var focused = Keyboard.FocusedElement as DependencyObject;
        bool inLog = focused != null && (focused == Output || Output.IsAncestorOf(focused));
        if (!inLog && focused != null
            && (FindParent<TextBox>(focused) != null || FindParent<ListBox>(focused) != null
                || FindParent<ComboBox>(focused) != null || FindParent<System.Windows.Controls.Primitives.MenuBase>(focused) != null))
            return false;                                    // typing, or moving in a list or menu
        if (inLog && key is not (Key.Home or Key.End)) return false;   // the editor handles these itself
        if (inLog && mods.HasFlag(ModifierKeys.Control)) return false; // Ctrl+Home/End: editor default (same result)

        var area = Output.TextArea;
        RoutedUICommand command = key switch
        {
            Key.Home => shift ? System.Windows.Documents.EditingCommands.SelectToDocumentStart : System.Windows.Documents.EditingCommands.MoveToDocumentStart,
            Key.End => shift ? System.Windows.Documents.EditingCommands.SelectToDocumentEnd : System.Windows.Documents.EditingCommands.MoveToDocumentEnd,
            Key.PageUp => shift ? System.Windows.Documents.EditingCommands.SelectUpByPage : System.Windows.Documents.EditingCommands.MoveUpByPage,
            Key.PageDown => shift ? System.Windows.Documents.EditingCommands.SelectDownByPage : System.Windows.Documents.EditingCommands.MoveDownByPage,
            Key.Up => shift ? System.Windows.Documents.EditingCommands.SelectUpByLine : System.Windows.Documents.EditingCommands.MoveUpByLine,
            _ => shift ? System.Windows.Documents.EditingCommands.SelectDownByLine : System.Windows.Documents.EditingCommands.MoveDownByLine,
        };
        command.Execute(null, area);
        if (key == Key.End && !shift) Output.ScrollToEnd();   // back at the bottom: auto-scroll follows again
        area.Caret.BringCaretToView();
        return true;
    }

    private void GoToStart()
    {
        if (Output.Document.TextLength == 0) return;
        Output.TextArea.ClearSelection();
        Output.TextArea.Caret.Offset = 0;
        Output.ScrollToHome();
        Output.Focus();
    }

    private void GoToCurrent()
    {
        if (Output.Document.TextLength == 0) return;
        Output.TextArea.ClearSelection();
        Output.TextArea.Caret.Line = Output.Document.LineCount;
        Output.ScrollToEnd();
        Output.Focus();
    }

    private void MenuStart_Click(object sender, RoutedEventArgs e) => GoToStart();
    private void MenuCurrent_Click(object sender, RoutedEventArgs e) => GoToCurrent();

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
        _settings.ShowTimeline = _timelineOpen;
        _settings.TimelineWindowSeconds = _timelineWindow;
        _settings.FlyoutTab = _flyoutTab;
        _settings.Notify = NotifyCheck.IsChecked == true;
        _settings.FoldersSplit = FoldersSplitShare();
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

    // ---------------------------------------------------------------- session tabs

    /// <summary>Open a tab for every running Revit session that doesn't have one; keep tab titles current.</summary>
    private void RefreshSessions()
    {
        var sessions = RevitSessionFinder.FindSessions();
        foreach (var s in sessions)
        {
            var tab = _tabs.FirstOrDefault(t => t.Pid == s.ProcessId);
            if (tab != null)
            {
                if (s.Model != null && s.Model != tab.Model) { tab.Model = s.Model; UpdateTabButton(tab); if (tab == _cur) UpdateTitle(); }
                continue;
            }
            if (s.JournalPath == null || _dismissedPids.Contains(s.ProcessId)) continue;
            OpenTab(s.JournalPath, s, select: _cur == null);
        }
        NoSessionsText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tabs.Count == 0)
            SetStatus($"No running Revit found. Waiting… (journals: {RevitSessionFinder.JournalRoot})");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _dismissedPids.Clear();   // bring back session tabs that were closed
        RefreshSessions();
    }

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
        if (dlg.ShowDialog(this) != true) return;

        var open = _tabs.FirstOrDefault(t => string.Equals(t.Path, dlg.FileName, StringComparison.OrdinalIgnoreCase));
        if (open != null) SelectTab(open);
        else OpenTab(dlg.FileName, null, select: true);
    }

    /// <summary>Start reading a journal in a new tab (a running session when <paramref name="session"/> is given).</summary>
    private void OpenTab(string path, RevitSession? session, bool select)
    {
        var tab = new SessionTab
        {
            Path = path,
            Pid = session?.ProcessId,
            Version = session?.Version ?? "Revit",
            Model = session?.Model,
            Started = session?.Started
        };
        try
        {
            long start = LoadExistingCheck.IsChecked == true ? ExistingBytesToLoad : 0;
            tab.Tailer = new JournalTailer(path, start);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {path}: {ex.Message}");
            return;
        }

        if (tab.Pid is int pid)
        {
            tab.Monitor = new ProcessMonitor();
            tab.Monitor.Sampled += s => Dispatcher.InvokeAsync(() => OnPerfSample(tab, s));
            tab.Monitor.Watch(pid);
        }

        _tabs.Add(tab);
        AddTabButton(tab);
        NoSessionsText.Visibility = Visibility.Collapsed;

        // The backlog: classified and analysed, but no notifications or activity flashes.
        try { Ingest(tab, tab.Tailer.ReadNewLines(), loading: true); }
        catch (Exception ex) { SetStatus($"Read error: {ex.Message}"); }
        UpdateTabButton(tab);

        if (select || _cur == null) SelectTab(tab);
        else SetStatus($"New Revit session in a tab: {tab.Title}");
    }

    private void AddTabButton(SessionTab tab)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var badge = new TextBlock
        {
            Margin = new Thickness(7, 0, 0, 0), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Errors logged in this session"
        };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "StatusError");
        var close = new Button { Style = (Style)FindResource("TabCloseStyle"), ToolTip = "Close this tab" };
        close.Click += (_, e) => { CloseTab(tab); e.Handled = true; };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(dot);
        content.Children.Add(label);
        content.Children.Add(badge);
        content.Children.Add(close);

        var button = new ToggleButton { Style = (Style)FindResource("SessionTabStyle"), Content = content };
        button.Click += (_, _) => SelectTab(tab);
        button.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { CloseTab(tab); e.Handled = true; } };

        tab.Button = button; tab.Dot = dot; tab.Label = label; tab.Badge = badge;
        SessionTabs.Children.Add(button);
    }

    /// <summary>Tab text, status dot (green live, gray ended, blue file), error count and tooltip.</summary>
    private void UpdateTabButton(SessionTab tab)
    {
        if (tab.Button == null) return;
        tab.Label!.Text = tab.Title;
        tab.Label.FontWeight = tab.Unseen ? FontWeights.SemiBold : FontWeights.Normal;
        tab.Dot!.SetResourceReference(Shape.FillProperty, tab.Pid == null ? "Accent" : tab.Ended ? "MutedFg" : "StatusActivity");
        int errors = tab.Analysis.Errors.Count;
        tab.Badge!.Text = errors > 0 ? errors.ToString("N0") : "";
        tab.Badge.Visibility = errors > 0 ? Visibility.Visible : Visibility.Collapsed;
        tab.Button.ToolTip = tab.Description + (tab.Unseen ? "\nNew lines since you last looked." : "");
        tab.Button.IsChecked = tab == _cur;
    }

    /// <summary>Bring a tab to the front: the log, counts, timeline, lists and graphs all switch to it.</summary>
    private void SelectTab(SessionTab? tab)
    {
        if (_cur != null) _cur.SelectedNumber = TimelinePlot.SelectedNumber;
        _cur = tab;
        if (tab != null) tab.Unseen = false;
        foreach (var t in _tabs) UpdateTabButton(t);

        _pausedNew = 0;
        _analysisShown = -1;
        _margin.Bookmarks = _bookmarks;
        if (_allLines.Count > 0) _margin.EnsureDigits(_allLines.Max(l => l.Number));
        TimelinePlot.SetData(_allLines, _hidden);
        TimelinePlot.SelectedNumber = tab?.SelectedNumber;
        UpdateCounts();
        RebuildOutput();
        UpdateTitle();
        ShowPerformanceOf(tab);
        if (_timelineOpen) { UpdateTimeline(); RefreshFlyoutTab(); }
        if (tab != null) SetStatus(tab.Pid is int p ? $"Streaming {tab.Path}  (PID {p})" : $"Showing {tab.Path}");
        UpdateStatusBar(force: true);
    }

    private void UpdateTitle()
    {
        Title = _cur == null ? "The Revit Matrix" : $"The Revit Matrix — {_cur.Title}";
        TitleInfo.Text = _cur == null ? ""
            : System.IO.Path.GetFileName(_cur.Path) + (_cur.Pid is int pid ? $"  ·  Revit PID {pid}" : "");
    }

    private void CloseTab(SessionTab tab)
    {
        if (tab.IsLive && tab.Pid is int pid) _dismissedPids.Add(pid);   // don't reopen it on the next scan
        int index = _tabs.IndexOf(tab);
        tab.Dispose();
        _tabs.Remove(tab);
        if (tab.Button != null) SessionTabs.Children.Remove(tab.Button);
        if (tab == _cur) SelectTab(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
        NoSessionsText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Mark tabs whose Revit has closed.</summary>
    private void CheckProcessAlive()
    {
        foreach (var tab in _tabs.Where(t => t.IsLive).ToList())
        {
            bool alive;
            try { alive = !Process.GetProcessById(tab.Pid!.Value).HasExited; }
            catch (ArgumentException) { alive = false; }
            catch (InvalidOperationException) { alive = false; }
            if (alive) continue;

            PollTab(tab);   // grab any final lines
            tab.Ended = true;
            tab.Monitor?.Watch(null);
            var marker = new[]
            {
                new JournalLine(0, ""),
                new JournalLine(0, $"{JournalClassifier.MarkerPrefix} Revit session (PID {tab.Pid}) ended ========")
            };
            if (tab == _cur)
            {
                AddLines(marker);
                SetStatus($"Revit PID {tab.Pid} has exited. Its tab stays open until you close it.");
                PerfTarget.Text = $"Revit PID {tab.Pid} has exited. The graphs show its last minutes.";
                UpdateTimeline();
                UpdateStatusBar();
            }
            else Ingest(tab, marker, loading: false);
            UpdateTabButton(tab);
        }
    }

    // ---------------------------------------------------------------- tailing

    /// <summary>Read new journal lines for every tab; the tab in front also updates the screen.</summary>
    private void Poll()
    {
        foreach (var tab in _tabs.ToList()) PollTab(tab);
    }

    private void PollTab(SessionTab tab)
    {
        if (tab.Tailer == null) return;
        List<JournalLine> lines;
        try
        {
            lines = tab.Tailer.ReadNewLines();
        }
        catch (Exception ex)
        {
            if (tab == _cur) SetStatus($"Read error: {ex.Message}");
            return;
        }
        if (lines.Count == 0) return;

        if (tab == _cur) AddLines(lines);
        else
        {
            Ingest(tab, lines, loading: false);
            tab.Unseen = true;
            UpdateTabButton(tab);
        }
    }

    /// <summary>
    /// Classify, time, count and analyse new lines into a tab's history (any tab, in front or not).
    /// <paramref name="loading"/>: the backlog read when a tab opens: no notifications.
    /// </summary>
    private List<JLine> Ingest(SessionTab tab, IEnumerable<JournalLine> lines, bool loading)
    {
        var added = new List<JLine>();
        foreach (var (number, text) in lines)
        {
            // Time: the line's own journal timestamp, else the last one seen,
            // else (no timestamp yet, live only) the time we received it.
            DateTime? time;
            bool exact = JournalClassifier.TryGetTimestamp(text, out var ts);
            if (exact) time = tab.LastJournalTime = ts;
            else time = tab.LastJournalTime ?? (loading ? null : DateTime.Now);

            var line = new JLine(number, text, JournalClassifier.Category(text), time, exact);
            added.Add(line);
            var op = tab.Analysis.Add(line);
            if (!loading) MaybeNotify(tab, line, op);
            tab.Counts[line.Category] = tab.Counts.GetValueOrDefault(line.Category) + 1;
        }
        tab.Lines.AddRange(added);

        if (tab.Lines.Count > MaxStoredLines)
        {
            int drop = tab.Lines.Count - MaxStoredLines;
            for (int i = 0; i < drop; i++) tab.Counts[tab.Lines[i].Category]--;
            tab.Lines.RemoveRange(0, drop);
        }
        return added;
    }

    /// <summary>New live lines for the tab in front: store them, then update the screen.</summary>
    private void AddLines(IEnumerable<JournalLine> lines)
    {
        if (_cur == null) return;
        var added = Ingest(_cur, lines, loading: false);
        if (added.Count > 0) _margin.EnsureDigits(added.Max(l => l.Number));
        UpdateTabButton(_cur);

        if (_timelineOpen)
        {
            if (_flyoutTab == "timeline") UpdateTimeline();
            else if (_flyoutTab is "slow" or "addins") RefreshFlyoutTab();
        }

        UpdateCounts();

        bool paused = PauseToggle.IsChecked == true;
        if (paused) _pausedNew += added.Count;
        else AppendToOutput(added);

        // Status bar activity (skip the viewer's own markers).
        if (added.Any(l => l.Category is not (JKind.Default or JKind.Marker)))
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
        TimelinePlot.InvalidateVisual();
        if (AutoScrollCheck.IsChecked == true) Output.ScrollToEnd();
    }

    private bool IsScrolledToBottom()
    {
        // Only follow the tail if the user hasn't scrolled up to read something.
        double remaining = Output.ExtentHeight - Output.VerticalOffset - Output.ViewportHeight;
        return remaining < 20 || Output.ExtentHeight <= Output.ViewportHeight;
    }

    /// <summary>Clear the tab in front (its lines, counts, analysis and bookmarks); it keeps reading new lines.</summary>
    private void ClearAll()
    {
        if (_cur != null)
        {
            _cur.Lines.Clear();
            _cur.Counts.Clear();
            _cur.Analysis.Reset();
            _cur.Bookmarks.Clear();
            _cur.SelectedNumber = null;
            UpdateTabButton(_cur);
        }
        UpdateCounts();
        _shown.Clear();
        Output.Clear();
        TimelinePlot.SelectedNumber = null;
        _analysisShown = -1;
        if (_timelineOpen && _flyoutTab != "timeline") RefreshFlyoutTab();
        UpdateTimeline();
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
        if (FilterClear != null) FilterClear.Visibility = FilterBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        RebuildOutput();
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Clear();
        FilterBox.Focus();
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FilterBox.Text.Length > 0) { FilterBox.Clear(); e.Handled = true; }
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
