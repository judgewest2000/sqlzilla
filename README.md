# Sqlzilla

Sqlzilla is a lightweight SQL Server query tool for Windows in the spirit of SQL Server Management Studio, built with WinUI 3. Organise servers into colour-coded groups such as Production, Develop and Read-only, so you always know which environment a query window is pointed at.

## Install

Requires Windows 10 1809 or later. Installs per user, no admin rights needed.

### Option 1: PowerShell one-liner

```powershell
& ([scriptblock]::Create((irm "https://raw.githubusercontent.com/judgewest2000/sqlzilla/main/install.ps1")))
```

This downloads the latest release for your architecture to `%LOCALAPPDATA%\Programs\Sqlzilla`, then adds a Start Menu shortcut and an Apps & Features entry.

Options (append after the closing parentheses):

- `-Version v1.0.0` installs a specific release
- `-InstallDir <path>` installs somewhere else
- `-Uninstall` removes Sqlzilla

### Option 2: Setup.exe

Download `Sqlzilla-Setup-<arch>.exe` from the [latest release](https://github.com/judgewest2000/sqlzilla/releases/latest) and run it.

### Uninstall

Use Windows Settings > Apps, or run the one-liner above with `-Uninstall`.

## Features

- **Server groups**: a tree of groups, each with a name and colour. Under each group are servers:
  - A server without a database expands into its list of databases.
  - A server with a database is a leaf that opens straight into that database.
- **Colour-coded windows**: query windows, tabs, the title bar and the status bar are tinted with the group's colour.
- **Read-only groups**: connections use `ApplicationIntent=ReadOnly`, and scripts containing writes (INSERT, UPDATE, DELETE, DDL and so on) ask for confirmation before running.
- **Authentication**: SQL Server login, Windows, Microsoft Entra ID (interactive/MFA, default credentials, password, service principal) or a raw connection string.
- **Results**: one or more grids of results. Scripts that return no rows show messages such as `(2 rows affected)` and `Commands completed successfully.`. Errors show their line number, and clicking an error jumps to that line.
- **Editor**: native code editor (Scintilla) with T-SQL highlighting and autocomplete for tables, columns and keywords.
  - Runs `GO` batches.
  - Executes the selection only, if there is one.
  - Cancel a running query.
  - Copy results or save them as CSV.

Connections are stored in `%LOCALAPPDATA%\Sqlzilla\connections.json`. Passwords and secrets are encrypted with Windows DPAPI.

Like Notepad++, open tabs are remembered between runs in `%LOCALAPPDATA%\Sqlzilla\session.json`. Each tab keeps its server, database and text, including unsaved edits, so closing Sqlzilla never prompts you to save.

### Shortcuts

| Key | Action |
| --- | --- |
| F5 / Ctrl+Enter / Ctrl+E | Execute (selection or whole script) |
| Ctrl+N | New query for the selected server |
| Ctrl+O / Ctrl+S | Open / save a .sql file |
| Ctrl+W | Close tab |
| Ctrl+R | Show or hide results |
| Ctrl+F | Find |

## Updates

On startup, Sqlzilla checks GitHub for a newer release and asks whether to update. You can also use **Check for updates** in the About dialog.

## Build from source

Requires the .NET 9 SDK.

```powershell
# Runs tests, then publishes a self-contained build to .\publish\win-x64
.\publish.ps1 -Zip
```

## Publish a release

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and the GitHub CLI.

```powershell
.\installer\build-installer.ps1 -Platform x64 -Version 1.0.1
gh release create v1.0.1 publish\Sqlzilla-win-x64.zip publish\Sqlzilla-Setup-x64.exe --title v1.0.1
```

`-Version` is stamped into the app, so the release tag must match it for the update check to work. The zip must be named `Sqlzilla-win-<arch>.zip` for `install.ps1` to find it.

## License

[MIT](LICENSE). Copyright (c) 2026 James West.
