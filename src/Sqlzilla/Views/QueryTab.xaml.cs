using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Sqlzilla.Models;
using Sqlzilla.Services;
using Sqlzilla.Sql;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Sqlzilla.Views;

/// <summary>
/// A query window bound to one server connection (SSMS "New Query"). Holds its own SQL session, so
/// temp tables, SET options and USE persist between executions. Coloured by the server's group.
/// </summary>
public sealed partial class QueryTab : UserControl
{
    private static int _untitledCounter = 1;

    private readonly QuerySession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);   // one command at a time on the session's connection
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _untitledName;
    private CancellationTokenSource? _runCts;
    private SchemaInfo _schema = SchemaInfo.Empty;
    private DateTime _runStarted;
    private bool _suppressDatabaseChange;
    private bool _closed;
    private GridLength _editorHeight = new(1, GridUnitType.Star);
    private GridLength _resultsHeight = new(1, GridUnitType.Star);
    private string _savedText = "";

    public QueryTab(ServerGroup group, ServerConnection server, string database)
    {
        InitializeComponent();
        Group = group;
        Server = server;
        _untitledName = $"SQLQuery{_untitledCounter++}.sql";
        _session = new QuerySession(ConnectionFactory.Build(server, database, group.IsReadOnly));

        Editor.CompletionSource = (text, caret, explicitRequest) =>
            Task.Run(() => SqlCompletionProvider.Complete(text, caret, _schema, explicitRequest));
        Editor.CommandInvoked += OnEditorCommand;
        Editor.TextChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _clock.Tick += (_, _) => ElapsedText.Text = FormatElapsed(DateTime.UtcNow - _runStarted);

        ServerText.Text = server.ResolvedServer;
        ApplyGroup();
        Loaded += OnFirstLoaded;
    }

    public ServerGroup Group { get; private set; }
    public ServerConnection Server { get; private set; }
    public string? FilePath { get; private set; }
    public string Database => _session.Database;
    public bool IsExecuting => _runCts != null;
    public bool IsDirty => Editor.Text != _savedText;
    public string Title => FilePath != null ? Path.GetFileName(FilePath) : _untitledName;
    public string Description => $"{Server.DisplayName} · {(Database.Length > 0 ? Database : "(default database)")}{(IsDirty ? " · unsaved" : "")}";

    /// <summary>Title, dirty state, database or executing state changed.</summary>
    public event EventHandler? StateChanged;

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        Editor.FocusEditor();
        _ = ConnectAsync();
    }

    // ------------------------------------------------------------------ group colour

    /// <summary>Re-applies colours after the server's group (or the group's colour) changed.</summary>
    public void SetGroup(ServerGroup group, ServerConnection server)
    {
        Group = group;
        Server = server;
        ServerText.Text = server.ResolvedServer;
        ApplyGroup();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyGroup()
    {
        var color = GroupColors.Parse(Group.Color);
        var text = new SolidColorBrush(GroupColors.ContrastText(color));
        Root.Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x2E));
        Toolbar.BorderBrush = new SolidColorBrush(color);
        StatusBar.Background = new SolidColorBrush(color);
        foreach (var t in new[] { StateText, GroupText, ServerText, LoginText, ElapsedText, RowsText }) t.Foreground = text;
        StateIcon.Foreground = text;
        BusyRing.Foreground = text;
        GroupText.Text = Group.LabelWithReadOnly;
        ReadOnlyBadge.Visibility = Group.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyBadge.Background = new SolidColorBrush(color);
        ReadOnlyText.Foreground = ReadOnlyIcon.Foreground = text;
        Editor.Tint = color;
    }

    // ------------------------------------------------------------------ connection

    private async Task ConnectAsync()
    {
        SetBusy(true, "Connecting…");
        await _gate.WaitAsync();
        try
        {
            await _session.OpenAsync(CancellationToken.None);
            if (_closed) return;
            UpdateConnectionInfo();
            SetState("Connected.", "\uE73E");
            var databases = await _session.ListDatabasesAsync(CancellationToken.None);
            if (_closed) return;
            FillDatabases(databases);
        }
        catch (Exception ex)
        {
            if (_closed) return;
            SetState("Not connected.", "\uE783");
            ShowMessages([new QueryMessage(MessageKind.Error, ex.Message)]);
        }
        finally
        {
            _gate.Release();
            if (!_closed) SetBusy(false);
        }
        await LoadSchemaAsync();
    }

    private void UpdateConnectionInfo()
    {
        LoginText.Text = _session.LoginName is { } login ? $"{login} ({_session.SessionId})" : "";
        SyncDatabaseBox();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FillDatabases(IEnumerable<string> databases)
    {
        _suppressDatabaseChange = true;
        DatabaseBox.Items.Clear();
        foreach (var db in databases) DatabaseBox.Items.Add(db);
        _suppressDatabaseChange = false;
        SyncDatabaseBox();
    }

    private void SyncDatabaseBox()
    {
        _suppressDatabaseChange = true;
        var db = _session.Database;
        if (db.Length > 0 && !DatabaseBox.Items.Contains(db)) DatabaseBox.Items.Add(db);
        DatabaseBox.SelectedItem = db.Length > 0 ? db : null;
        _suppressDatabaseChange = false;
    }

    private async Task LoadSchemaAsync()
    {
        if (!_session.IsOpen || IsExecuting) return;
        await _gate.WaitAsync();
        try { _schema = await _session.LoadSchemaAsync(CancellationToken.None); }
        catch { _schema = SchemaInfo.Empty; }   // autocomplete is best-effort (e.g. no VIEW DEFINITION)
        finally { _gate.Release(); }
    }

    private async void DatabaseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDatabaseChange || DatabaseBox.SelectedItem is not string db || db == _session.Database) return;
        if (IsExecuting) { SyncDatabaseBox(); return; }
        await _gate.WaitAsync();
        try
        {
            await _session.ChangeDatabaseAsync(db, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowMessages([new QueryMessage(MessageKind.Error, ex.Message)]);
        }
        finally { _gate.Release(); }
        UpdateConnectionInfo();
        await LoadSchemaAsync();
    }

    // ------------------------------------------------------------------ execution

    private void Execute_Click(object sender, RoutedEventArgs e) => _ = ExecuteAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    public void Cancel() => _runCts?.Cancel();

    private void OnEditorCommand(object? sender, EditorCommand command)
    {
        switch (command)
        {
            case EditorCommand.Execute: _ = ExecuteAsync(); break;
            case EditorCommand.Save: _ = SaveAsync(false); break;
            case EditorCommand.ToggleResults: ToggleResults(); break;
        }
    }

    // F5 reaches both the editor and the window's accelerator; ignore the second while the first is starting.
    private bool _busyStarting;
    private bool _saving;

    public async Task ExecuteAsync()
    {
        if (IsExecuting || _closed || _busyStarting) return;
        _busyStarting = true;
        try { await ExecuteCoreAsync(); }
        finally { _busyStarting = false; }
    }

    private async Task ExecuteCoreAsync()
    {
        var selection = Editor.Selection;
        var script = selection?.Text ?? Editor.Text;
        var lineOffset = selection?.Line ?? 0;
        if (script.Trim().Length == 0) return;

        if (Group.IsReadOnly)
        {
            var writes = SqlWriteDetector.Find(script);
            if (writes.Count > 0)
            {
                var ok = await Dialogs.ConfirmAsync(XamlRoot, "Read-only group",
                    $"“{Group.Name}” is marked read-only, but this script contains {string.Join(", ", writes)}.\n\nRun it anyway against {Server.DisplayName}?",
                    "Run anyway");
                if (!ok) return;
            }
        }

        var cts = _runCts = new CancellationTokenSource();
        ExecuteButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        DatabaseBox.IsEnabled = false;
        _runStarted = DateTime.UtcNow;
        ElapsedText.Text = FormatElapsed(TimeSpan.Zero);
        _clock.Start();
        SetBusy(true, "Executing query…");
        StateChanged?.Invoke(this, EventArgs.Empty);

        QueryResult result;
        await _gate.WaitAsync();
        try
        {
            result = await Task.Run(() => _session.ExecuteAsync(script, lineOffset, cts.Token));
        }
        catch (Exception ex)
        {
            result = new QueryResult { HasErrors = true, Elapsed = DateTime.UtcNow - _runStarted };
            result.Messages.Add(new QueryMessage(MessageKind.Error, ex.Message));
        }
        finally
        {
            _gate.Release();
        }

        _runCts = null;
        cts.Dispose();
        _clock.Stop();
        if (_closed) return;

        ExecuteButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        DatabaseBox.IsEnabled = true;
        SetBusy(false);
        ElapsedText.Text = FormatElapsed(result.Elapsed);
        RowsText.Text = result.TotalRows == 1 ? "1 row" : $"{result.TotalRows:N0} rows";
        if (result.Cancelled) SetState("Query cancelled.", "\uE711");
        else if (result.HasErrors) SetState("Query completed with errors.", "\uE783");
        else SetState("Query executed successfully.", "\uE73E");

        ShowResult(result);
        var databaseBefore = DatabaseBox.SelectedItem as string;
        UpdateConnectionInfo();
        if (databaseBefore != _session.Database || !_session.IsOpen) await LoadSchemaAsync();
        else if (Regex.IsMatch(script, @"\b(CREATE|ALTER|DROP)\b", RegexOptions.IgnoreCase)) _ = LoadSchemaAsync();
    }

    private static string FormatElapsed(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private void SetBusy(bool busy, string? state = null)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        if (state != null) StateText.Text = state;
    }

    private void SetState(string text, string glyph)
    {
        StateText.Text = text;
        StateIcon.Glyph = glyph;
    }

    // ------------------------------------------------------------------ results

    private void ShowResult(QueryResult result)
    {
        foreach (var grid in ResultsHost.Children.OfType<FrameworkElement>().SelectMany(Grids)) grid.SaveCsvRequested -= OnSaveCsvRequested;
        ResultsHost.Children.Clear();
        ResultsHost.RowDefinitions.Clear();

        ShowMessages(result.Messages);

        if (result.ResultSets.Count == 0)
        {
            ResultsTabItem.Visibility = Visibility.Collapsed;
            ResultTabs.SelectedItem = MessagesTabItem;
            UpdateResultTabs();
            return;
        }

        var grids = result.ResultSets.Select(set =>
        {
            var grid = new ResultGrid(set);
            grid.SaveCsvRequested += OnSaveCsvRequested;
            return grid;
        }).ToList();

        if (grids.Count <= 3)
        {
            // Share the height, like SSMS.
            for (var i = 0; i < grids.Count; i++)
            {
                ResultsHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
                grids[i].Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0);
                Grid.SetRow(grids[i], i);
                ResultsHost.Children.Add(grids[i]);
            }
        }
        else
        {
            var stack = new StackPanel { Spacing = 6 };
            foreach (var g in grids)
            {
                g.Height = Math.Min(300, 32 + Math.Max(1, g.ResultSet.Rows.Count) * 24 + 20);
                stack.Children.Add(g);
            }
            ResultsHost.Children.Add(new ScrollViewer { Content = stack, Padding = new Thickness(0, 0, 12, 0) });
        }

        ResultsTabItem.Text = grids.Count == 1 ? "Results" : $"Results ({grids.Count})";
        ResultsTabItem.Visibility = Visibility.Visible;
        ResultTabs.SelectedItem = result.HasErrors ? MessagesTabItem : ResultsTabItem;
        UpdateResultTabs();
    }

    private static IEnumerable<ResultGrid> Grids(FrameworkElement e) => e switch
    {
        ResultGrid g => [g],
        ScrollViewer { Content: StackPanel s } => s.Children.OfType<ResultGrid>(),
        _ => [],
    };

    private void ShowMessages(IEnumerable<QueryMessage> messages)
    {
        MessagesText.Blocks.Clear();
        var error = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var success = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        var count = 0;
        foreach (var m in messages)
        {
            if (++count > 5000)
            {
                MessagesText.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "… (further messages omitted)" } } });
                break;
            }
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, m.Kind == MessageKind.Error ? 6 : 2) };
            var lineMatch = m.Kind == MessageKind.Error ? Regex.Match(m.Text, @"\bLine (\d+)") : Match.Empty;
            if (lineMatch.Success)
            {
                // Clicking the error's header jumps to the offending line, as in SSMS.
                var line = int.Parse(lineMatch.Groups[1].Value) - 1;
                var split = m.Text.IndexOf('\n');
                var head = split < 0 ? m.Text : m.Text[..split];
                var link = new Hyperlink { Foreground = error, UnderlineStyle = UnderlineStyle.None };
                link.Inlines.Add(new Run { Text = head });
                link.Click += (_, _) => Editor.GoToLine(line);
                ToolTipService.SetToolTip(link, $"Go to line {line + 1}");
                paragraph.Inlines.Add(link);
                if (split >= 0) paragraph.Inlines.Add(new Run { Text = m.Text[split..], Foreground = error });
            }
            else
            {
                var run = new Run { Text = m.Text };
                if (m.Kind == MessageKind.Error) run.Foreground = error;
                else if (m.Kind == MessageKind.Success) run.Foreground = success;
                paragraph.Inlines.Add(run);
            }
            MessagesText.Blocks.Add(paragraph);
        }
        MessagesScroll.ChangeView(0, 0, null, true);
        if (ResultsTabItem.Visibility == Visibility.Collapsed) ResultTabs.SelectedItem = MessagesTabItem;
        UpdateResultTabs();
    }

    private void ResultTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => UpdateResultTabs();

    private void UpdateResultTabs()
    {
        var showResults = ResultTabs.SelectedItem == ResultsTabItem && ResultsTabItem.Visibility == Visibility.Visible;
        ResultsHost.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        MessagesHost.Visibility = showResults ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleResults()
    {
        var rows = Body.RowDefinitions;
        if (ResultsPane.Visibility == Visibility.Visible)
        {
            _editorHeight = rows[0].Height;
            _resultsHeight = rows[2].Height;
            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[2].Height = new GridLength(0);
            ResultsPane.Visibility = Splitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            rows[0].Height = _editorHeight;
            rows[2].Height = _resultsHeight;
            ResultsPane.Visibility = Splitter.Visibility = Visibility.Visible;
        }
    }

    private async void OnSaveCsvRequested(object? sender, EventArgs e)
    {
        if (sender is not ResultGrid grid) return;
        var picker = new FileSavePicker { SuggestedFileName = "results" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, ResultExport.ToCsv(grid.ResultSet.Columns, grid.ResultSet.Rows), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "Couldn't save CSV", ex.Message);
        }
    }

    // ------------------------------------------------------------------ files

    private void Open_Click(object sender, RoutedEventArgs e) => _ = OpenFileAsync();

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAsync(false);

    public async Task OpenFileAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
        picker.FileTypeFilter.Add(".sql");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        if (IsDirty && Editor.Text.Trim().Length > 0)
        {
            var choice = await Dialogs.ConfirmUnsavedAsync(XamlRoot, Title);
            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Primary && !await SaveAsync(false)) return;
        }
        try
        {
            LoadFile(file.Path, await File.ReadAllTextAsync(file.Path));
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "Couldn't open file", ex.Message);
        }
    }

    public void LoadFile(string? path, string text)
    {
        FilePath = path;
        Editor.Text = text;
        _savedText = Editor.Text;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves to the current file (or asks for one). Returns false if cancelled or failed.</summary>
    public async Task<bool> SaveAsync(bool saveAs)
    {
        if (_saving) return false;
        _saving = true;
        try { return await SaveCoreAsync(saveAs); }
        finally { _saving = false; }
    }

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        var path = FilePath;
        if (path == null || saveAs)
        {
            var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(Title) };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Handle);
            picker.FileTypeChoices.Add("SQL script", new List<string> { ".sql" });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return false;
            path = file.Path;
        }
        try
        {
            var text = Editor.Text;
            await File.WriteAllTextAsync(path, text.Replace("\n", "\r\n"));
            FilePath = path;
            _savedText = text;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ShowErrorAsync(XamlRoot, "Couldn't save file", ex.Message);
            return false;
        }
    }

    public void SetText(string text) => Editor.Text = text;

    public SessionTab ToSessionTab() => new()
    {
        ServerId = Server.Id,
        Database = Database,
        FilePath = FilePath,
        UntitledName = FilePath == null ? _untitledName : null,
        Text = Editor.Text,
        IsDirty = IsDirty,
    };

    /// <summary>Restores a remembered window: unsaved text comes from the session, otherwise the file is re-read from disk.</summary>
    public void Restore(SessionTab tab)
    {
        if (tab.UntitledName is { } name)
        {
            _untitledName = name;
            if (name.StartsWith("SQLQuery") && int.TryParse(Path.GetFileNameWithoutExtension(name).AsSpan("SQLQuery".Length), out var n))
                _untitledCounter = Math.Max(_untitledCounter, n + 1);
        }

        string? onDisk = null;
        if (tab.FilePath != null)
        {
            try { onDisk = File.ReadAllText(tab.FilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        FilePath = tab.FilePath;
        if (tab.FilePath != null && onDisk != null)
        {
            Editor.Text = onDisk;
            _savedText = Editor.Text;
            if (tab.IsDirty) Editor.Text = tab.Text;
        }
        else
        {
            // Untitled, or the file has gone: keep the remembered text (it stays "unsaved" if it was before).
            Editor.Text = tab.Text;
            _savedText = tab.IsDirty ? "" : Editor.Text;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FocusEditor() => Editor.FocusEditor();

    /// <summary>Cancels any running query, closes the connection and releases the native editor.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _runCts?.Cancel();
        _clock.Stop();
        Editor.Close();
        foreach (var grid in ResultsHost.Children.OfType<FrameworkElement>().SelectMany(Grids)) grid.SaveCsvRequested -= OnSaveCsvRequested;
        ResultsHost.Children.Clear();
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await _session.DisposeAsync(); }
            catch { }
            finally { _gate.Release(); }
        });
    }
}
