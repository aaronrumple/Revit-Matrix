<p align="center"><img src="Assets/TheRevitMatrix.png" width="128" alt="The Revit Matrix"></p>

# The Revit Matrix

Crafted by: [Aaron Rumple, AIA](https://www.linkedin.com/in/aaronrumple/)

The Revit Matrix is a Windows desktop app that finds your running Revit sessions and streams each one's journal file, live, into a colored output window. Part of the PyPiPie tools.

- **Live:** every command, dialog, add-in message and error appears as Revit writes it.
- **Readable:** journal lines are colored by kind, with the file's line numbers and each line's time.
- **Follows your theme:** dark or light, matching Windows (and Revit, when Revit follows the system theme).

## Features

| Area | What it does |
|---|---|
| **Sessions** | Lists every running `Revit.exe` and matches each to its own journal, so several sessions or versions can run at once. Each session gets its own **tab** with its own log, timeline, analysis, bookmarks and performance graphs; background tabs keep reading, turn bold on new lines and show an error count. **Open journal file…** opens any journal, including old ones, in a tab. |
| **Output** | Journal text colored by kind: commands, replay actions, quoted values, timestamps, comments, `API_SUCCESS`, warnings, errors, memory stats. |
| **Line # and Time** | A gutter with each line's number in the journal file and its time as `HH:mm:ss::hh`. Bright times are the line's own timestamp; dim times are carried from the last timestamped line. |
| **Category filters** | One chip per category with a live line count. Click to show or hide, right-click to show only that category. Presets: **All**, **Problems**, **Activity**. |
| **Text filter** | Shows only lines containing the text, combined with the chips. Click **×** or press **Esc** to clear it. |
| **Timeline** | Click the timeline button in the title bar (or press **F3**) to slide out a timeline along the bottom: every journal entry as a dot in its category's row, by time. Click a dot to jump to its line. Windows of 1, 5 or 15 minutes or the whole journal, Follow live, and a range slider to zoom. Middle-button drag slides the timeline through time; the wheel zooms around the pointer (Shift + wheel moves). |
| **Slow operations** | Every operation Revit timed (opens, saves, syncs, regenerations, views…), with how long it took; sortable, click to jump to the line. |
| **Add-in startup** | Which add-ins slowed Revit's startup, slowest first, with the memory each added. |
| **Session summary** | Files, saves, syncs, commands, errors, warnings, slowest operations and peak memory, as plain text to copy into a support ticket. |
| **Journal folders** | Size of every Revit version's journal folder; select one or more journals (or every one older than N days) and move them to the Recycle Bin; right-click a journal to open it in a tab or text editor, open its folder, copy its path or recycle it. |
| **Errors & bookmarks** | **F8** / **Shift+F8** jump between errors and warnings; **Ctrl+B** bookmarks a line, **F9** / **Shift+F9** move between bookmarks. |
| **Notifications** | While the app is in the background, a Windows notification for errors and for long opens, saves and syncs. |
| **Performance panel** | Click the chart button in the title bar (or press **F2**) to slide out live graphs of the selected tab's Revit CPU, memory, GPU and GPU memory over the last 5 minutes, with current values and peaks. |
| **Right-click menu** | Right-click anywhere to open or close the bottom panel (on any of its tabs) and the performance panel; over the log it also bookmarks the clicked line, copies and selects all. |
| **Status bar** | Changes color with the session: idle, live, new activity, warning, error, paused (with a count of new lines), ended. |
| **Tools** | Auto-scroll that stops when you scroll up, Pause/Resume, Clear, Save (with the line-number and time columns). |
| **Help** | Press **F1** or click **?** in the title bar for the full guide (`help/TheRevitMatrix_Help.html`). |

## Install

Download `TheRevitMatrix-<version>-x64.msi` from the [Releases page](https://github.com/aaronrumple/Revit-Matrix/releases/latest) and run it.
It installs to `C:\Program Files\PyPiPie\The Revit Matrix` and adds **Start menu › PyPiPie › The Revit Matrix**.
The app is self-contained, so no separate .NET runtime is needed.

> **The MSI is not code-signed.** Windows SmartScreen will show *"Windows protected your PC"*: click **More info › Run anyway**.
> Your browser may also ask you to **Keep** the download, and the install prompt shows **Publisher: Unknown**.
> Each release lists the installer's SHA-256 checksum; compare it with `Get-FileHash .\TheRevitMatrix-<version>-x64.msi` in PowerShell.

## Build and run

Needs Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/aaronrumple/Revit-Matrix.git
cd Revit-Matrix
dotnet run
```

or open `TheRevitMatrix.csproj` in Visual Studio 2022 and press F5.

## Build the MSI

Needs the .NET 8 SDK and [Node.js](https://nodejs.org/) 18 or later. The [WiX Toolset](https://wixtoolset.org/) v5 is restored from NuGet automatically.

```
npm run package
```

This publishes a self-contained `TheRevitMatrix.exe` to `build\publish` and builds `build\msi\TheRevitMatrix-<version>-x64.msi`.
The version comes from `package.json`; bump it with `npm version patch` (or `minor` / `major`) before a release.

| Script | What it does |
|---|---|
| `npm start` | Build and run the app |
| `npm run publish` | Publish the self-contained exe to `build\publish` |
| `npm run msi` | Build the MSI from the last publish |
| `npm run package` | Clean, publish and build the MSI |
| `npm run msix` | Build a signed MSIX package (see below) |
| `npm run clean` | Delete `build\publish` and `build\msi` |

### Publish a release

GitHub Actions builds the MSI on Windows and adds it to the release, with its SHA-256 checksum and the notes in `.github/RELEASE_TEMPLATE.md`. Either:

- On GitHub, **Releases › Draft a new release**, type a new tag such as `v1.0.1`, and **Publish release**; or
- push a version tag:

  ```
  git tag v1.0.1
  git push origin v1.0.1
  ```

The installer appears on the release a few minutes later.

The tag sets the version, so `package.json` doesn't have to be bumped first. To build an MSI without releasing it, run **Actions › Release MSI › Run workflow**; the MSI is attached to the run.

## Build the MSIX package

The MSIX is the package for the Microsoft Store (and for installing signed builds directly). It needs the [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/) for `makeappx`, `makepri` and `signtool`.

```
npm run msix
```

This publishes the app, stages it with `AppxManifest.xml` and the logos in `Assets\Store`, indexes the logos into `resources.pri`, then packs and signs `build\msix\TheRevitMatrix-<version>-x64.msix`. The version comes from `package.json` (as `1.2.3.0`).

- **Signing:** the script uses the certificate in your user store whose subject matches the manifest's `Publisher`, or one you name with `-CertificateThumbprint <thumbprint>` or `-PfxPath <file.pfx>` (for example `npm run msix -- -PfxPath cert.pfx`). If none matches, it prints the command to make a development certificate. A self-signed package installs only after its certificate is added to **Local Computer › Trusted People**.
- **Microsoft Store:** the Store re-signs packages, so any signature is fine for upload. `Identity Name`, `Publisher` and `PublisherDisplayName` in `AppxManifest.xml` must match **Partner Center › Product identity** exactly.
- **Store listing:** the description, features, search terms, capability justification and other Partner Center text are in [`STORE_LISTING.md`](STORE_LISTING.md).
- **Settings:** installed from the MSIX, the app keeps its settings in its own private copy of `%APPDATA%\TheRevitMatrix`, separate from an MSI install.

## How it works

- **Finding sessions:** every 3 seconds it lists `Revit.exe` processes and scans
  `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 20XX\Journals\journal.*.txt`.
  Each process is matched to the first journal created at or after the process started.
- **Streaming:** the journal is opened with full sharing (`FileShare.ReadWrite | Delete`), so Revit is never blocked,
  and read every 250 ms. Journals are read as Windows-1252 (ANSI), which is how Revit writes them.
- **Theme:** *Auto* follows the Windows app theme, which Revit 2024+ also uses when *follow the system theme* is on.
  Revit's own theme setting can only be read from inside Revit, so choose *Dark* or *Light* if you've set Revit differently.

## Repository layout

```
TheRevitMatrix.csproj         project (.NET 8, WPF)
package.json                  version, metadata and npm scripts for publishing and the MSI
scripts/build.mjs             publish + MSI build used by the npm scripts
scripts/build-msix.ps1        publish + MSIX package, PRI and signing (npm run msix)
AppxManifest.xml              MSIX package manifest (Store identity, logos, full-trust entry point)
.github/workflows/release.yml builds the MSI on Windows and publishes releases for v* tags
.github/RELEASE_TEMPLATE.md   release notes (download, unsigned-installer note, checksum)
installer/                    WiX v5 MSI: Package.wxs, .wixproj, license and installer artwork
App.xaml(.cs)                 application entry: splash screen, then the main window
SplashWindow.xaml(.cs)        splash screen (Assets/Splash.jpg)
MainWindow.xaml(.cs)          window: title bar, session tabs, toolbar, filters, output, status bar
SessionTab.cs                 one tab's journal reader, lines, analysis, bookmarks and performance history
RevitSessionFinder.cs         running Revit processes -> their journal files
ProcessMonitor.cs             samples Revit's CPU, memory and GPU once a second
PerfChart.cs                  running graph used in the performance panel
TimelineView.cs               the timeline: entries by time and category, hover and click to jump
RangeBar.cs                   two-handle time-range slider under the timeline
JournalAnalysis.cs            reads the journal for timed operations, add-in startup, commands, problems, memory
JournalFolders.cs             journal folder sizes; moves old journals to the Recycle Bin
Notifier.cs                   Windows notifications
JournalTailer.cs              reads a journal as Revit writes it, with file line numbers
JournalClassifier.cs          line categories, colored spans, timestamps
JournalColorizer.cs           colors the output view
LineInfoMargin.cs             line-number and time gutter
Theme.cs                      dark and light palettes, Auto theme detection
AppSettings.cs                settings in %APPDATA%\TheRevitMatrix\settings.json
Assets/                       app icon and logo, splash artwork, PyPiPie logo
Assets/Store/                 MSIX logos at the sizes Windows and the Store ask for
help/TheRevitMatrix_Help.html user guide (F1)
```

## Privacy

The Revit Matrix does not collect or send any personal data. See the [Privacy Policy](PRIVACY.md).

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) for the full license and [COPYRIGHT](COPYRIGHT) for the copyright notice.

© 2026 [Aaron Rumple, AIA](https://www.linkedin.com/in/aaronrumple/)
