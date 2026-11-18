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
- **Editor**: native code editor (Scintilla) with T-SQL highlighting.
  - Runs `GO` batches.
  - Executes the selection only, if there is one.
  - Cancel a running query.
  - Copy results or save them as CSV.
- **Results grid**: selection works like Excel.
  - Drag a box over cells, or click or drag row numbers and column headers. Shift extends the selection, Ctrl adds ranges, and the top-left corner selects everything.
  - Arrow keys, Ctrl+arrows, PageUp/PageDown and Home/End move the selection.
  - Ctrl+C copies the selection (Ctrl+Shift+C includes headers).
  - Drag a header edge to resize a column, including the row-number column. Double-click the edge to auto-fit.
- **IntelliSense**: uses the schema of the connected database.
  - Context-aware completion: tables after `FROM`, columns via aliases (`o.`), procedures after `EXEC`, `@variables` and `#temp` tables.
  - Join suggestions built from foreign keys, e.g. `JOIN Customers c ON c.CustomerId = o.CustomerId`. Objects in `dbo` are inserted without the schema prefix; other schemas keep it.
  - Understands CTEs, temp tables, table variables, `SELECT INTO` and derived tables.
  - Signature help for built-in functions, user functions and stored procedure parameters.
  - Hover for column types, keys and foreign keys, and object or function details.
  - Red squiggles for syntax errors; amber squiggles for unknown tables and procedures.
  - F1 opens a schema docs panel for the item under the caret. It has search, columns, parameters and foreign-key links.

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
| Ctrl+/ | Comment or uncomment the selected lines |
| Ctrl+Space | Show completions |
| Ctrl+Shift+Space | Show parameter info |
| F1 | Schema docs for the item under the caret |

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
