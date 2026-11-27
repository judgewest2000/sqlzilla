# Release notes

## 1.6.6 (2026-11-27)

- **About** uses a wider, side-by-side layout so the artwork, information, keyboard shortcuts and update buttons fit without scrolling in normal-sized windows. Release notes also use the wider dialog.

## 1.6.5 (2026-11-26)

- **Server search** now finds databases on collapsed *Microsoft Entra MFA (interactive)* servers in other tenants too. An account you've signed in with on any server is reused across tenants. If you haven't signed in yet, search tries the account you're signed in to Windows with, still without ever showing a sign-in window.

## 1.6.4 (2026-11-26)

- **Search** in the explorer and in the *Open .sql file on...* picker now waits until you've paused typing for 400 ms before refiltering. Clearing the box applies straight away, and Enter acts on what you've typed even if the wait hasn't finished.

## 1.6.3 (2026-11-26)

- **Query editor:** the scroll bars now sit outside the text, like the results grid, so they no longer cover the last line or the end of long lines. They stay full-size instead of only widening on hover, and the mouse wheel works over them.

## 1.6.2 (2026-11-25)

- **Server search** now finds databases on collapsed servers that use *Microsoft Entra MFA (interactive)*, not just SQL logins. It reuses an account you've already signed in with this session (any server in the same tenant) and never pops up a sign-in window; if you haven't signed in yet, expand the server once.

## 1.6.1 (2026-11-25)

- **Save as CSV** now includes every row. The grid still shows the first 500,000 rows of a result set, but the rest are kept in a temp file instead of being discarded, so the CSV has them all.

## 1.6.0 (2026-11-25)

- Upgraded to .NET 11.

## 1.5.8 (2026-11-25)

- Result columns (including a locked first column) now widen to fit every row as it arrives, not just the first 300, so longer values further down (e.g. Ids going from 1 to 100002) are no longer cut off. Double-clicking a column's resize grip also fits every row.

## 1.5.7 (2026-11-25)

- About > Release notes has an **Export…** button that saves the full list of release notes as a Markdown (or text) file.

## 1.5.6 (2026-11-25)

- Query tabs are as wide as their title and server/database need, so they're no longer cut off. A tab only trims its server/database text once it would take more than half the width of the query area.

## 1.5.5 (2026-11-24)

- Query tabs show Azure SQL servers without the `.database.windows.net` suffix (e.g. `myserver.database.windows.net` shows as `myserver`). The full name is still shown in the tooltip and explorer.
- Read-only groups no longer ask for confirmation when a script only writes to `#temp` tables or `@table` variables (`CREATE TABLE #t`, `INSERT INTO #t`, `SELECT … INTO #t`, `DROP TABLE IF EXISTS #t`, …).

## 1.5.4 (2026-11-24)
- **Lock first column.** The new button next to Results/Messages keeps the first column visible while you scroll sideways, like Excel's Freeze First Column. It applies to every results grid and stays on until you close Sqlzilla.

## 1.5.3 (2026-11-24)
- **Release notes** in About show what changed in each version.

## 1.5.2 (2026-11-24)
- Results grid: the scroll bars now sit outside the cells, so moving sideways no longer scrolls the content by accident.
- Row numbers stay visible while you scroll sideways.

## 1.5.1 (2026-11-24)
- Fixed: typing in the search box of the *Open .sql file on...* picker could freeze or crash the app.
- Explorer search now expands every matching database.

## 1.5.0 (2026-11-24)
- **Open .sql files with Sqlzilla.** Both installers add Sqlzilla to *Open with*. Setup has an option to make it the default, and `install.ps1` has `-AssociateSqlFiles`.
- **Connection picker.** When you open a .sql file, Sqlzilla asks which server or database to use, with a searchable tree.
- **Single instance.** Files you open while Sqlzilla is running open as new tabs in the existing window.
- **Explorer search** (Ctrl+Shift+F) filters servers, groups and databases.

## 1.4.4 (2026-11-20)
- New toolbar button next to Save that refreshes IntelliSense, the same as Shift+Alt+R. It picks up tables created after the tab was opened.

## 1.4.3 (2026-11-20)
- Shift+Alt+R in the editor refreshes IntelliSense (tables, columns, routines). Progress and the result show in the tab's status bar.

## 1.4.2 (2026-11-20)
- Import: you can drag and drop a file onto the dialog, which highlights a drop zone.
- Import: binary or unsupported files (images etc.) are rejected with a clear message instead of producing garbage columns.

## 1.4.1 (2026-11-20)
- Import: column types are visible in the mapping grid without clicking.
- Import: values like 06:00 are imported as `time`.
- Import: a big *Import complete!* screen appears when the import finishes, and errors scroll into view.

## 1.4.0 (2026-11-20)
- **Import data.** Right-click a database > *Import data...* to load a CSV or other delimited file, JSON or Excel file into a new table (column types are guessed from the data). You can also append to an existing table with a column mapping. The import runs in one transaction, so a bad row leaves nothing behind.

## 1.3.7 (2026-11-20)
- Unknown and ambiguous column names get a red underline (e.g. *Ambiguous column name 'Id'.*), like SSMS.

## 1.3.6 (2026-11-20)
- Right-click a database or server in the explorer > *Open tabs* lists its open query windows. Click one to jump to it.
- Tab right-click menu: *Close*, *Close all but this - No Verify*, *Close ALL Tabs - No Verify*.

## 1.3.5 (2026-11-19)
- IntelliSense now works below a `--` comment.
- IntelliSense works after an opening `[` (e.g. `FROM [u`).
- The empty-editor placeholder scales with editor zoom.

## 1.3.4 (2026-11-19)
- Copying cells no longer adds a trailing newline.

## 1.3.3 (2026-11-19)
- Query results stream into the grid while the query runs, with a live row count.
- The *Showing the first 500,000 rows* warning no longer overlaps the grid.

## 1.3.2 (2026-11-19)
- Results and messages are cleared when a query is re-run, so it's clear it's executing (like SSMS).
- Fixed: the explorer resize handle no longer overlaps the server tree's scroll bar.
- Fixed: the query tab title lines up vertically with the server/database text.

## 1.3.1 (2026-11-19)
- Fixed: a single click on a result cell or header followed by Ctrl+C now copies. Previously a double-click was needed.

## 1.3.0 (2026-11-18)
- **Excel-style results grid.** Drag a box over cells, or click or drag row numbers and headers. Shift extends the selection, Ctrl adds ranges, you can move around with the keyboard, and copying copies the selection.
- Columns are resizable, including the row-number column, which is no longer clipped. Double-click a header edge to auto-fit.
- IntelliSense no longer inserts the `dbo.` prefix. Other schemas keep theirs.

## 1.2.0 (2026-11-18)
- The Results tab is shown by default. Messages is shown only when a query returns no rows.
- The results grid uses Consolas.
- Click the grid's top-left corner to select all rows.
- Ctrl+/ toggles `--` comments on the selected lines (VS Code style).
- Azure SQL servers now list all databases, not just master.

## 1.1.0 (2026-11-18)
- **IntelliSense:**
  - Context-aware completion (aliases, EXEC, @variables, #temp tables, CTEs)
  - Foreign-key join suggestions
  - Signature help (Ctrl+Shift+Space)
  - Hover info
  - Underlines for errors and unknown objects
  - An F1 schema docs panel

## 1.0.0 (2026-11-18)
- First release of Sqlzilla.
