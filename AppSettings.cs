using System.IO;
using System.Text.Json;

namespace RevitJournalViewer;

/// <summary>Theme and category-filter choices, saved to %APPDATA%\TheRevitMatrix\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>"Auto" (follow the Windows/Revit theme), "Dark" or "Light".
    /// Older settings files only have the obsolete "Theme" value, so they start on Auto.</summary>
    public string ThemeMode { get; set; } = "Auto";
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowPerformance { get; set; }

    /// <summary>Categories switched off (by enum name). Memory stats are hidden by default.</summary>
    public List<string> HiddenCategories { get; set; } = new() { nameof(JKind.Stats) };

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string FilePath => Path.Combine(AppData, "TheRevitMatrix", "settings.json");

    // Settings from before the app was renamed; read once if the new file doesn't exist yet.
    private static string OldFilePath => Path.Combine(AppData, "RevitJournalViewer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = File.Exists(FilePath) ? FilePath : OldFilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch { /* corrupt or unreadable – fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}
