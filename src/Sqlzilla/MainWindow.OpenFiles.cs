using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sqlzilla.Models;
using Sqlzilla.Services;
using Sqlzilla.Views;
using Windows.System;

namespace Sqlzilla;

// Explorer search, and opening .sql files from Explorer / the command line.
public sealed partial class MainWindow
{
    private const int SearchDebounceMs = 400;

    private string _explorerFilter = "";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _explorerSearchTimer;
    private HashSet<string> _expandedBeforeSearch = new();
    private readonly HashSet<string> _searchLoads = new();
    private readonly HashSet<string> _searchLoadsFailed = new();
    private readonly List<string> _pendingFiles = new();   // opened from Explorer before any server existed
    private readonly SemaphoreSlim _openFilesGate = new(1, 1);

    /// <summary>Raised when background searches add database lists to the cache.</summary>
    private event Action? DatabaseListsChanged;

    // ------------------------------------------------------------------ explorer search

    /// <summary>Restarts the 400 ms wait on each keystroke, so the tree refilters once typing pauses. Clearing applies at once.</summary>
    private static void Debounce(ref Microsoft.UI.Dispatching.DispatcherQueueTimer? timer, string text, Action apply)
    {
        timer?.Stop();
        if (text.Trim().Length == 0)
        {
            apply();
            return;
        }
        if (timer == null)
        {
            timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(SearchDebounceMs);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => apply();
        }
        timer.Start();
    }

    /// <summary>Applies a pending debounced search now (e.g. Enter pressed before the wait elapsed).</summary>
    private static void Flush(Microsoft.UI.Dispatching.DispatcherQueueTimer? timer, Action apply)
    {
        if (timer is not { IsRunning: true }) return;
        timer.Stop();
        apply();
    }

    private void ExplorerSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        Debounce(ref _explorerSearchTimer, ExplorerSearch.Text, ApplyExplorerSearch);

    private void ApplyExplorerSearch()
    {
        var filter = ExplorerSearch.Text.Trim();
        if (filter == _explorerFilter) return;
        if (_explorerFilter.Length == 0)
        {
            _expandedBeforeSearch = ExpandedIds();
            // A new search retries servers that failed last time (e.g. you've since signed in to Entra).
            _searchLoads.ExceptWith(_searchLoadsFailed);
            _searchLoadsFailed.Clear();
        }
        _explorerFilter = filter;
        RebuildTree(expanded: filter.Length == 0 ? _expandedBeforeSearch : null);
        if (filter.Length > 0) LoadDatabaseListsForSearch();
    }

    private void ExplorerSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Down) Flush(_explorerSearchTimer, ApplyExplorerSearch);
        switch (e.Key)
        {
            case VirtualKey.Escape when ExplorerSearch.Text.Length > 0:
                ExplorerSearch.Text = "";
                e.Handled = true;
                break;
            case VirtualKey.Enter when Flatten(RootEntries).FirstOrDefault(x => x.IsQueryTarget) is { } first:
                OpenQuery(first);
                e.Handled = true;
                break;
            case VirtualKey.Down when RootEntries.Count > 0:
                ServerTree.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
        }
    }

    private void SearchAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ExplorerSearch.Focus(FocusState.Keyboard);
        ExplorerSearch.SelectAll();
    }

    private void OnDatabaseListsChanged()
    {
        if (_explorerFilter.Length > 0) RebuildTree();
    }

    /// <summary>
    /// Fetches database lists so searches can find databases on servers that haven't been expanded yet.
    /// Interactive Entra servers are only loaded if an account is already signed in: typing in the search box must never
    /// pop up a login window.
    /// </summary>
    private void LoadDatabaseListsForSearch()
    {
        foreach (var group in _groups)
            foreach (var server in group.Servers)
            {
                if (server.HasDatabase || _databaseCache.ContainsKey(server.Id)) continue;
                if (_searchLoads.Add(server.Id)) _ = LoadDatabaseListAsync(group, server);
            }
    }

    private async Task LoadDatabaseListAsync(ServerGroup group, ServerConnection server)
    {
        try
        {
            _databaseCache[server.Id] = await Task.Run(() => ConnectionFactory.ListDatabasesAsync(server, group.IsReadOnly, CancellationToken.None, silentSignIn: true));
        }
        catch
        {
            _searchLoadsFailed.Add(server.Id);
            return;   // unreachable servers show their error when expanded
        }
        DatabaseListsChanged?.Invoke();
    }

    // ------------------------------------------------------------------ opening files

    /// <summary>Opens files passed on the command line or from Explorer, asking which server/database to use.</summary>
    public void OpenFiles(IReadOnlyList<string> paths, bool bringToFront)
    {
        if (bringToFront) BringToFront();
        if (paths.Count == 0) return;
        if (!Root.IsLoaded)
        {
            Root.Loaded += OpenWhenLoaded;
            return;
        }
        _ = OpenFilesAsync(paths, null);

        void OpenWhenLoaded(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OpenWhenLoaded;
            _ = OpenFilesAsync(paths, null);
        }
    }

    /// <summary>Opens the files on <paramref name="connection"/>, or on one picked by the user when null.</summary>
    private async Task OpenFilesAsync(IReadOnlyList<string> paths, (ServerGroup Group, ServerConnection Server, string Database)? connection)
    {
        // One picker at a time when several files arrive separately (Explorer launches once per selected file).
        await _openFilesGate.WaitAsync();
        try { await OpenFilesCoreAsync(paths, connection); }
        catch (Exception ex) { await Dialogs.ShowErrorAsync(XamlRoot, "Couldn't open file", ex.Message); }
        finally { _openFilesGate.Release(); }
    }

    private async Task OpenFilesCoreAsync(IReadOnlyList<string> paths, (ServerGroup Group, ServerConnection Server, string Database)? connection)
    {
        var toOpen = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var open = OpenQueries.FirstOrDefault(q => string.Equals(q.Query.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (open.Tab != null) Tabs.SelectedItem = open.Tab;
            else toOpen.Add(path);
        }
        if (toOpen.Count == 0) return;

        if (!_groups.Any(g => g.Servers.Count > 0))
        {
            var firstAsk = _pendingFiles.Count == 0;
            _pendingFiles.AddRange(toOpen.Where(p => !_pendingFiles.Contains(p, StringComparer.OrdinalIgnoreCase)));
            if (firstAsk && await Dialogs.ConfirmAsync(XamlRoot, "Add a server first",
                    $"Query windows always run against a server. Add one and {Describe(toOpen)} will open on it.", "Add server"))
                NewServer_Click(this, new RoutedEventArgs());
            return;
        }

        connection ??= await PickConnectionAsync(toOpen);
        if (connection is not var (group, server, database)) return;

        foreach (var path in toOpen)
        {
            string text;
            try { text = await File.ReadAllTextAsync(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.ShowErrorAsync(XamlRoot, "Couldn't open file", ex.Message);
                continue;
            }
            _lastServerId = server.Id;
            var query = new QueryTab(group, server, database);
            query.LoadFile(path, text);
            AddTab(query);
        }
    }

    /// <summary>After the first server is added, opens files that arrived while there was nothing to connect to.</summary>
    private async Task OpenPendingFilesAsync()
    {
        if (_pendingFiles.Count == 0 || DefaultConnection() is not { } connection) return;
        var files = _pendingFiles.ToList();
        _pendingFiles.Clear();
        await OpenFilesAsync(files, connection);
    }

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files";

    /// <summary>A searchable copy of the server explorer for choosing where a script will run.</summary>
    private async Task<(ServerGroup Group, ServerConnection Server, string Database)?> PickConnectionAsync(IReadOnlyList<string> paths)
    {
        var search = new TextBox { PlaceholderText = "Search servers and databases" };
        var empty = new TextBlock
        {
            Margin = new Thickness(12),
            Visibility = Visibility.Collapsed,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var intro = new TextBlock
        {
            Text = paths.Count == 1 ? paths[0] : string.Join(Environment.NewLine, paths.Select(Path.GetFileName)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var treeHost = new Grid { Height = 360 };
        var panel = new StackPanel { Spacing = 8, Width = 440 };
        panel.Children.Add(intro);
        panel.Children.Add(search);
        panel.Children.Add(treeHost);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Open {Describe(paths)} on…",
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        ExplorerEntry? Target(object? item) => item is ExplorerEntry { Server: not null } e && e.Kind != ExplorerEntryKind.Placeholder ? e : null;
        ExplorerEntry? chosen = null;
        var entries = new List<ExplorerEntry>();
        TreeView tree = null!;
        ExplorerEntry? current = null;   // the picked entry; TreeView.SelectedItem only catches up once laid out

        void Choose(ExplorerEntry entry)
        {
            chosen = entry;
            dialog.Hide();
        }

        // Each refresh gets a new TreeView, so no item containers are recycled from the previous results.
        TreeView CreateTree()
        {
            var t = new TreeView
            {
                ItemsSource = entries,
                ItemTemplate = (DataTemplate)Root.Resources["ExplorerItemTemplate"],
                SelectionMode = TreeViewSelectionMode.Single,
                CanDragItems = false,
                AllowDrop = false,
            };
            t.SelectionChanged += (_, _) =>
            {
                if (t != tree) return;
                current = t.SelectedItem as ExplorerEntry;
                dialog.IsPrimaryButtonEnabled = Target(current) != null;
            };
            t.Expanding += (sender, e) =>
            {
                if (e.Item is not ExplorerEntry entry) return;
                entry.IsExpanded = true;
                if (entry.Kind == ExplorerEntryKind.Server) _ = LoadDatabasesAsync(entry, false);
            };
            t.Collapsed += (sender, e) => { if (e.Item is ExplorerEntry entry) entry.IsExpanded = false; };
            t.ItemInvoked += (_, e) =>
            {
                if (e.InvokedItem is ExplorerEntry entry && (entry.Kind == ExplorerEntryKind.Group || (entry.Kind == ExplorerEntryKind.Server && !entry.Server!.HasDatabase)))
                    entry.IsExpanded = !entry.IsExpanded;
            };
            t.DoubleTapped += (_, e) =>
            {
                for (var d = e.OriginalSource as DependencyObject; d != null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
                {
                    if (d is not TreeViewItem item) continue;
                    if (Target(t.ItemFromContainer(item)) is { IsQueryTarget: true } target)
                    {
                        e.Handled = true;
                        Choose(target);
                    }
                    return;
                }
            };
            return t;
        }

        void Select(ExplorerEntry? entry)
        {
            if (entry != null && !VisibleEntries(entries).Contains(entry)) entry = null;
            current = entry;
            dialog.IsPrimaryButtonEnabled = Target(entry) != null;
            if (entry == null) return;
            // Only hand the item to TreeView once it is laid out with every ancestor expanded: selecting an item
            // inside a collapsed node makes WinUI spin forever.
            var t = tree;
            var attempts = 0;
            void Highlight()
            {
                if (t != tree || !ReferenceEquals(current, entry)) return;
                if (ShownInTree(t, entries, entry)) t.SelectedItem = entry;
                else if (++attempts < 10) t.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Highlight);
            }
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                t.Loaded -= OnLoaded;
                t.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Highlight);
            }
            if (t.IsLoaded) t.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Highlight);
            else t.Loaded += OnLoaded;
        }

        void Refresh()
        {
            var selectedId = current?.Id;
            var filter = search.Text.Trim();
            entries = BuildExplorerEntries(filter, new HashSet<string>(), expandGroups: true);
            treeHost.Children.Clear();
            tree = CreateTree();
            treeHost.Children.Add(tree);
            treeHost.Children.Add(empty);
            empty.Text = $"Nothing matches \u201C{filter}\u201D.";
            empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var visible = VisibleEntries(entries).ToList();
            Select(visible.FirstOrDefault(e => e.Id == selectedId) ?? (filter.Length > 0 ? visible.FirstOrDefault(e => e.IsQueryTarget) : null));
        }

        Refresh();
        // Start on the connection a new query would use: the selected server/database, the current tab's, or the last used.
        if (DefaultConnection() is var (_, defaultServer, defaultDatabase))
        {
            var serverEntry = Flatten(entries).FirstOrDefault(e => e.Kind == ExplorerEntryKind.Server && e.Server!.Id == defaultServer.Id);
            var databaseEntry = serverEntry?.Children.FirstOrDefault(e => e.Kind == ExplorerEntryKind.Database &&
                string.Equals(e.Database, defaultDatabase, StringComparison.OrdinalIgnoreCase));
            if (databaseEntry != null) serverEntry!.IsExpanded = true;
            Select(databaseEntry ?? serverEntry);
        }

        void OnListsChanged() { if (search.Text.Trim().Length > 0) Refresh(); }
        DatabaseListsChanged += OnListsChanged;

        void ApplySearch()
        {
            Refresh();
            if (search.Text.Trim().Length > 0) LoadDatabaseListsForSearch();
        }
        Microsoft.UI.Dispatching.DispatcherQueueTimer? searchTimer = null;
        search.TextChanged += (_, _) => Debounce(ref searchTimer, search.Text, ApplySearch);
        search.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Down) Flush(searchTimer, ApplySearch);
            if (e.Key == VirtualKey.Enter && Target(current) is { } target)
            {
                e.Handled = true;
                Choose(target);
            }
            else if (e.Key == VirtualKey.Down && entries.Count > 0)
            {
                e.Handled = true;
                tree.Focus(FocusState.Keyboard);
            }
        };
        dialog.PrimaryButtonClick += (_, _) => chosen = Target(current);
        dialog.Opened += (_, _) => search.Focus(FocusState.Programmatic);

        try
        {
            await Dialogs.WhenNoDialogOpenAsync(XamlRoot);
            await dialog.ShowAsync();
        }
        finally
        {
            DatabaseListsChanged -= OnListsChanged;
            searchTimer?.Stop();
        }

        return chosen is { } c ? (c.Group, c.Server!, c.TargetDatabase) : null;
    }
    private void BringToFront()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(Handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
