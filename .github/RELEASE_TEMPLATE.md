<p align="center"><img src="https://raw.githubusercontent.com/aaronrumple/Revit-Matrix/main/Assets/TheRevitMatrix.png" width="96" alt="The Revit Matrix"></p>

## The Revit Matrix {{VERSION}}

Watch a running Revit session's journal file, live: colored by kind, with category filters, line numbers and timestamps.

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
