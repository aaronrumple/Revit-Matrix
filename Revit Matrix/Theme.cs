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
            ["WindowBg"] = B("#1E1E1E"),
            ["PanelBg"] = B("#252526"),
            ["Fg"] = B("#DDDDDD"),
            ["MutedFg"] = B("#9A9A9A"),
            ["OutputBg"] = B("#0C0C0C"),
            ["OutputFg"] = B("#CCCCCC"),
            ["InputBg"] = B("#3C3C3C"),
            ["InputFg"] = B("#EEEEEE"),
            ["InputBorder"] = B("#555555"),
            ["ButtonBg"] = B("#3A3D41"),
            ["ButtonHoverBg"] = B("#4A4E54"),
            ["ButtonFg"] = B("#EEEEEE"),
            ["ButtonBorder"] = B("#555555"),
            ["ChipBg"] = B("#2D2D30"),
            ["ChipBorder"] = B("#4A4A4A"),
            ["Accent"] = B("#0E8CE8"),
            ["StatusBg"] = B("#007ACC"),
            ["StatusFg"] = B("#FFFFFF"),
            ["SelectionBg"] = B("#264F78"),
            ["ItemHoverBg"] = B("#2A2D2E"),
            ["ScrollTrack"] = B("#171717"),
            ["ScrollThumb"] = B("#424242"),
            ["ScrollThumbHover"] = B("#686868"),
            // Status bar states
            ["StatusIdle"] = B("#3C3C3C"),
            ["StatusLive"] = B("#007ACC"),
            ["StatusActivity"] = B("#16825D"),
            ["StatusWarning"] = B("#A86F00"),
            ["StatusError"] = B("#C42B1C"),
            ["StatusPaused"] = B("#68449E"),
            ["StatusEnded"] = B("#4D4D4D"),
        },
        Palette = new()
        {
            [JKind.Default] = new(B("#CCCCCC")),
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
            ["WindowBg"] = B("#F3F3F3"),
            ["PanelBg"] = B("#E8E8E8"),
            ["Fg"] = B("#1E1E1E"),
            ["MutedFg"] = B("#6E6E6E"),
            ["OutputBg"] = B("#FFFFFF"),
            ["OutputFg"] = B("#222222"),
            ["InputBg"] = B("#FFFFFF"),
            ["InputFg"] = B("#1E1E1E"),
            ["InputBorder"] = B("#B5B5B5"),
            ["ButtonBg"] = B("#FDFDFD"),
            ["ButtonHoverBg"] = B("#E5F1FB"),
            ["ButtonFg"] = B("#1E1E1E"),
            ["ButtonBorder"] = B("#B5B5B5"),
            ["ChipBg"] = B("#FFFFFF"),
            ["ChipBorder"] = B("#C8C8C8"),
            ["Accent"] = B("#0067C0"),
            ["StatusBg"] = B("#0067C0"),
            ["StatusFg"] = B("#FFFFFF"),
            ["SelectionBg"] = B("#ADD6FF"),
            ["ItemHoverBg"] = B("#E5F1FB"),
            ["ScrollTrack"] = B("#F0F0F0"),
            ["ScrollThumb"] = B("#C1C1C1"),
            ["ScrollThumbHover"] = B("#9E9E9E"),
            // Status bar states
            ["StatusIdle"] = B("#8A8A8A"),
            ["StatusLive"] = B("#0067C0"),
            ["StatusActivity"] = B("#107C10"),
            ["StatusWarning"] = B("#B86E00"),
            ["StatusError"] = B("#C42B1C"),
            ["StatusPaused"] = B("#6B4FBB"),
            ["StatusEnded"] = B("#6E6E6E"),
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
}
