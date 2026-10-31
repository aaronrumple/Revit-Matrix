# The Revit Matrix

A small WPF (.NET 8) app that finds running Revit sessions and streams each one's journal file live into an output window.

## Build & run

```
cd "Revit Matrix"
dotnet run
```

or open `TheRevitMatrix.csproj` in Visual Studio 2022 and press F5. Needs the .NET 8 SDK on Windows.

## How it works

- **Finding sessions** – every 3 s it lists `Revit.exe` processes and scans
  `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 20XX\Journals\journal.*.txt`.
  Each process is matched to the first journal created at/after the process start time,
  so multiple sessions (same or different versions) each get their own journal.
- **Streaming** – the journal is opened with `FileShare.ReadWrite | Delete`, so Revit is never blocked,
  and polled every 250 ms for new lines.
- It auto-attaches to the newest session on launch, and to another session if the watched Revit closes.

## Features

- Session picker + **Open journal file…** for any journal (redirected journal folders, old logs).
- **Load existing** – shows the last 2 MB of the journal when attaching, or only new lines if unchecked.
- **Category chips** (the "Show:" row) – one per line category, each with its color dot and a live
  line count. Click to show/hide, right-click to show only that category.
  Presets: **All**, **Problems** (errors + warnings), **Activity** (commands, actions, events, timing, problems).
  Memory stats are hidden by default.
- **Filter** – case-insensitive substring, combined with the chips.
- **Dark / Light theme** button.
- Theme and hidden categories are saved to `%APPDATA%\TheRevitMatrix\settings.json`.
- Auto-scroll that stops following when you scroll up to read, Pause/Resume, Clear, Save.

## Colors

The output is an AvalonEdit view (NuGet `AvalonEdit`), colored line by line by `JournalClassifier`.
Dark-theme colors are listed; the light theme uses darker equivalents.

| Color | Journal content |
|---|---|
| Gold, bold | `Jrn.Command` – commands you ran |
| Light blue | other `Jrn.*` replay lines (MouseMove, PushButton, Directive, Data…) and their `_` argument lines |
| Orange | `"quoted"` values in replay lines |
| Slate | `'C` / `'E` / `'H` timestamps |
| Teal | `->enter` / `<-exit` markers and `' [Jrn.*]` data lines |
| Gray | other `'` comments |
| Green | `API_SUCCESS` |
| Amber | warnings |
| Red, bold | `API_ERROR`, error / exception / failed / crash |
| Dim italic | memory (`Delta VM`), GDI usage and perf-table noise |
| Purple | header (`Build`, `Branch`, `Release`, `Dim/Set Jrn`) |

Both palettes and the window colors are in `Theme.cs`; untick **Colors** to show plain text.
Journals are read as Windows-1252 (ANSI), which is how Revit writes them.

## Notes

- If Revit runs elevated (as admin) and this app doesn't, the version label falls back to "Revit";
  matching still works because it only needs the process start time.
- Revit buffers some journal writes, so a line can appear a moment after the action in Revit.
