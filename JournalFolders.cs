using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace RevitJournalViewer;

/// <summary>One Revit version's journal folder.</summary>
public sealed record JournalFolderInfo(string Version, string Path, int Files, long Bytes, DateTime? Oldest, int OldFiles, long OldBytes)
{
    public string SizeText => JournalFolders.Size(Bytes);
    public string FilesText => Files.ToString("N0", CultureInfo.InvariantCulture);
    public string OldestText => Oldest is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
    public string OldText => OldFiles == 0 ? "none" : $"{OldFiles:N0} ({JournalFolders.Size(OldBytes)})";
}

/// <summary>
/// Revit never deletes its journals. Lists the journal folders of every installed Revit
/// version and moves old journals to the Recycle Bin.
/// </summary>
public static class JournalFolders
{
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" :
        $"{Math.Max(1, bytes / 1024):N0} KB";

    private static IEnumerable<FileInfo> JournalFiles(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles("journal.*")
            .Where(f => f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                     || f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase));

    public static List<JournalFolderInfo> Scan(int olderThanDays)
    {
        var list = new List<JournalFolderInfo>();
        var root = RevitSessionFinder.JournalRoot;
        if (!Directory.Exists(root)) return list;
        var cutoff = DateTime.Now.AddDays(-olderThanDays);

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            string folder = System.IO.Path.Combine(dir, "Journals");
            if (!Directory.Exists(folder)) continue;
            try
            {
                var files = JournalFiles(folder).ToList();
                var old = files.Where(f => f.LastWriteTime < cutoff).ToList();
                list.Add(new JournalFolderInfo(
                    System.IO.Path.GetFileName(dir).Replace("Autodesk ", ""),
                    folder,
                    files.Count,
                    files.Sum(f => f.Length),
                    files.Count > 0 ? files.Min(f => f.LastWriteTime) : null,
                    old.Count,
                    old.Sum(f => f.Length)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>
    /// Moves journals last written more than <paramref name="olderThanDays"/> days ago to the
    /// Recycle Bin. Skips any journal a running Revit still has open, and <paramref name="keep"/>.
    /// </summary>
    public static (int Moved, long Bytes, int Skipped) RecycleOld(int olderThanDays, string? keep)
    {
        var cutoff = DateTime.Now.AddDays(-olderThanDays);
        var toDelete = new List<FileInfo>();
        int skipped = 0;

        foreach (var info in Scan(olderThanDays))
        {
            foreach (var f in JournalFiles(info.Path).Where(f => f.LastWriteTime < cutoff))
            {
                if (keep != null && string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (InUse(f)) { skipped++; continue; }
                toDelete.Add(f);
            }
        }
        if (toDelete.Count == 0) return (0, 0, skipped);

        long bytes = toDelete.Sum(f => f.Length);
        bool ok = SendToRecycleBin(toDelete.Select(f => f.FullName));
        return ok ? (toDelete.Count, bytes, skipped) : (0, 0, skipped + toDelete.Count);
    }

    private static bool InUse(FileInfo f)
    {
        try
        {
            using var s = f.Open(FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    // ---------------------------------------------------------------- Recycle Bin (SHFileOperation)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    private static bool SendToRecycleBin(IEnumerable<string> paths)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join("\0", paths) + "\0\0",   // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        return SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }
}
