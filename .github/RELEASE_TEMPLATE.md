<p align="center"><img src="https://raw.githubusercontent.com/aaronrumple/Revit-Matrix/main/Assets/TheRevitMatrix.png" width="96" alt="The Revit Matrix"></p>

## The Revit Matrix {{VERSION}}

Watch every running Revit session's journal file, live: colored by kind, with filters, a timeline, slow-operation and add-in startup reports, a session summary and live performance graphs.

### What's new in 1.1.0

- **A tab for each Revit session**, each with its own log, timeline, reports, bookmarks and performance graphs. Background tabs keep reading, turn bold on new lines and show their error count.
- **Timeline** of every journal entry along the bottom: click a dot to jump to its line; the mouse wheel zooms at the pointer and the middle mouse button drags through time.
- **Reports:** Slow operations, Add-in startup and a Session summary ready to paste into a support ticket.
- **Live performance graphs** for CPU, memory, Revit's own memory figure, GPU and GPU memory.
- **Navigation:** F8 / Shift+F8 for the next / previous error, Ctrl+B and F9 for bookmarks, and Home, End, Page Up, Page Down and the arrow keys from anywhere in the window.
- **Windows notifications** for errors and for opens, saves and syncs that take 10 seconds or more.
- **Journal folders:** select journals, or every journal older than a number of days, and move them to the Recycle Bin; right-click a journal to open it, open its folder or copy its path.
- **Right-click menus**, a clear button in the filter box, and a fix for the window freezing when closed right after starting.

Crafted by: [Aaron Rumple, AIA](https://www.linkedin.com/in/aaronrumple/)

### Download

| File | What it is |
|---|---|
| `{{MSI}}` | Windows installer (64-bit). Installs to `C:\Program Files\PyPiPie\The Revit Matrix` and adds **Start menu › PyPiPie › The Revit Matrix**. |
| `{{MSI}}.sha256` | SHA-256 checksum of the installer. |

Needs Windows 10 or 11 (64-bit). The app is self-contained, so no separate .NET install is needed. Installing a newer version replaces the old one.

### ⚠️ The installer is not code-signed

The MSI isn't signed with a code-signing certificate, so Windows warns before running it:

- **Microsoft Edge / Chrome** may say the download "isn't commonly downloaded". Choose **Keep** (in Edge: **…** › **Keep** › **Show more** › **Keep anyway**).
- **Windows SmartScreen** shows *"Windows protected your PC"*. Click **More info**, check that the app is `{{MSI}}`, then **Run anyway**.
- The install prompt shows **Publisher: Unknown**. That is expected for an unsigned installer.

To check the file is the one built here, compare its checksum in PowerShell:

```powershell
Get-FileHash .\{{MSI}} -Algorithm SHA256
```

It should match:

```
{{SHA256}}
```

This installer was built from this repository's source by [GitHub Actions]({{RUN_URL}}).

### License

GPL-3.0. © 2026 [Aaron Rumple, AIA](https://www.linkedin.com/in/aaronrumple/) · [Source](https://github.com/aaronrumple/Revit-Matrix) · [Help](https://github.com/aaronrumple/Revit-Matrix/blob/main/README.md)
