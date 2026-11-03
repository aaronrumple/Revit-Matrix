using System.Windows;
using System.Windows.Media;

namespace RevitJournalViewer;

/// <summary>Text style for one kind of journal content.</summary>
public sealed record KindStyle(Brush Brush, bool Bold = false, bool Italic = false);

/// <summary>
/// A color theme: brushes for the window chrome (applied as DynamicResources by key)
/// plus the palette used to color journal text.
/// </summary>
public sealed class Theme
{
    public required string Name { get; init; }
    public required Dictionary<string, Brush> Ui { get; init; }
    public required Dictionary<JKind, KindStyle> Palette { get; init; }

    public bool IsDark => Name == "Dark";

    public Color ColorOf(string uiKey) => ((SolidColorBrush)Ui[uiKey]).Color;

    public KindStyle StyleFor(JKind kind) =>
        Palette.TryGetValue(kind, out var s) ? s : Palette[JKind.Default];

    /// <summary>Push the UI brushes into a resource dictionary; DynamicResource users update live.</summary>
    public void ApplyTo(ResourceDictionary resources)
    {
        foreach (var (key, brush) in Ui) resources[key] = brush;

        // Built-in templates (e.g. the ScrollViewer corner square between scroll bars)
        // look up these system keys dynamically; overriding them here themes those bits too.
        resources[SystemColors.ControlBrushKey] = Ui["ScrollTrack"];
        resources[SystemColors.WindowBrushKey] = Ui["OutputBg"];
        resources[SystemColors.HighlightBrushKey] = Ui["SelectionBg"];
    }

    private static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Theme Dark { get; } = new()
    {
        Name = "Dark",
        Ui = new()
        {
            // Window chrome: PyPiPie / Rover palette (matches Revit's dark UI)
            ["WindowBg"] = B("#22262B"),
            ["HeaderBg"] = B("#1B1E22"),
            ["PanelBg"] = B("#2A2E34"),
            ["Border"] = B("#434A53"),
            ["Fg"] = B("#E5E8EB"),
            ["MutedFg"] = B("#A9B2BC"),
            ["Link"] = B("#73B7FF"),
            ["OutputBg"] = B("#1A1D21"),
            ["OutputFg"] = B("#D4D8DC"),
            ["InputBg"] = B("#1E2125"),
            ["InputFg"] = B("#E5E8EB"),
            ["InputBorder"] = B("#434A53"),
            ["ButtonBg"] = B("#33383F"),
            ["ButtonHoverBg"] = B("#3B4148"),
            ["ButtonFg"] = B("#E5E8EB"),
            ["ButtonBorder"] = B("#434A53"),
            ["CaptionHoverBg"] = B("#33383F"),
            ["ChipBg"] = B("#2A2E34"),
            ["ChipBorder"] = B("#434A53"),
            ["Accent"] = B("#4EA1F3"),
            ["StatusFg"] = B("#FFFFFF"),
            ["SelectionBg"] = B("#22466B"),
            ["ItemHoverBg"] = B("#3B4148"),
            ["ScrollTrack"] = B("#1E2125"),
            ["ScrollThumb"] = B("#434A53"),
            ["ScrollThumbHover"] = B("#5E6873"),
            // Status bar states
            ["StatusIdle"] = B("#3A4048"),
            ["StatusLive"] = B("#1F6FB8"),
            ["StatusActivity"] = B("#16825D"),
            ["StatusWarning"] = B("#A86F00"),
            ["StatusError"] = B("#C42B1C"),
            ["StatusPaused"] = B("#68449E"),
            ["StatusEnded"] = B("#4A5059"),
        },
        Palette = new()
        {
            [JKind.Default] = new(B("#D4D8DC")),
            [JKind.Header] = new(B("#C586C0"), Bold: true),
            [JKind.Command] = new(B("#FFD866"), Bold: true),
            [JKind.Action] = new(B("#9CDCFE")),
            [JKind.Argument] = new(B("#7FB8D8")),
            [JKind.String] = new(B("#CE9178")),
            [JKind.Timestamp] = new(B("#6A7F99")),
            [JKind.Comment] = new(B("#A0A0A0")),
            [JKind.Arrow] = new(B("#4EC9B0")),
            [JKind.JrnData] = new(B("#4EC9B0")),
            [JKind.Timing] = new(B("#B5CEA8")),
            [JKind.Stats] = new(B("#5A5A5A"), Italic: true),
            [JKind.Success] = new(B("#6A9955")),
            [JKind.Warning] = new(B("#E5A50A")),
            [JKind.Error] = new(B("#F14C4C"), Bold: true),
            [JKind.Marker] = new(B("#89D185"), Bold: true),
        }
    };

    public static Theme Light { get; } = new()
    {
        Name = "Light",
        Ui = new()
        {
            // Window chrome: PyPiPie / Rover palette (matches Revit's light UI)
            ["WindowBg"] = B("#ECEEF1"),
            ["HeaderBg"] = B("#DDE1E6"),
            ["PanelBg"] = B("#F3F4F6"),
            ["Border"] = B("#C6CCD4"),
            ["Fg"] = B("#1C2025"),
            ["MutedFg"] = B("#4E5763"),
            ["Link"] = B("#0B5CB8"),
            ["OutputBg"] = B("#FFFFFF"),
            ["OutputFg"] = B("#1C2025"),
            ["InputBg"] = B("#FFFFFF"),
            ["InputFg"] = B("#1C2025"),
            ["InputBorder"] = B("#C6CCD4"),
            ["ButtonBg"] = B("#F3F4F6"),
            ["ButtonHoverBg"] = B("#E6EEF8"),
            ["ButtonFg"] = B("#1C2025"),
            ["ButtonBorder"] = B("#C6CCD4"),
            ["CaptionHoverBg"] = B("#C9CFD6"),
            ["ChipBg"] = B("#FFFFFF"),
            ["ChipBorder"] = B("#C6CCD4"),
            ["Accent"] = B("#0B63C4"),
            ["StatusFg"] = B("#FFFFFF"),
            ["SelectionBg"] = B("#CFE3FA"),
            ["ItemHoverBg"] = B("#E6EEF8"),
            ["ScrollTrack"] = B("#ECEEF1"),
            ["ScrollThumb"] = B("#C6CCD4"),
            ["ScrollThumbHover"] = B("#A3ACB7"),
            // Status bar states
            ["StatusIdle"] = B("#6B737D"),
            ["StatusLive"] = B("#0B63C4"),
            ["StatusActivity"] = B("#107C10"),
            ["StatusWarning"] = B("#B86E00"),
            ["StatusError"] = B("#C42B1C"),
            ["StatusPaused"] = B("#6B4FBB"),
            ["StatusEnded"] = B("#6B737D"),
        },
        Palette = new()
        {
            [JKind.Default] = new(B("#222222")),
            [JKind.Header] = new(B("#7A3E9D"), Bold: true),
            [JKind.Command] = new(B("#8A5A00"), Bold: true),
            [JKind.Action] = new(B("#0451A5")),
            [JKind.Argument] = new(B("#2C6E9E")),
            [JKind.String] = new(B("#A31515")),
            [JKind.Timestamp] = new(B("#6B7B8C")),
            [JKind.Comment] = new(B("#666666")),
            [JKind.Arrow] = new(B("#0E7C7B")),
            [JKind.JrnData] = new(B("#0E7C7B")),
            [JKind.Timing] = new(B("#098658")),
            [JKind.Stats] = new(B("#AAAAAA"), Italic: true),
            [JKind.Success] = new(B("#3B7D2B")),
            [JKind.Warning] = new(B("#B35C00")),
            [JKind.Error] = new(B("#CD1F1F"), Bold: true),
            [JKind.Marker] = new(B("#1B7F37"), Bold: true),
        }
    };

    public static Theme ByName(string? name) =>
        string.Equals(name, Light.Name, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    /// <summary>
    /// The theme for a mode: "Dark", "Light", or "Auto". Auto follows the Windows app theme,
    /// which is what Revit 2024+ uses when its "follow the system theme" option is on.
    /// (Revit's own theme can only be read from inside Revit.)
    /// </summary>
    public static Theme ForMode(string? mode) =>
        string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase)
            ? (WindowsUsesLightTheme() ? Light : Dark)
            : ByName(mode);

    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }
}
