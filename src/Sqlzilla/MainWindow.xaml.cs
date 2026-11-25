using System.Collections.ObjectModel;
using System.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Sqlzilla.Models;
using Sqlzilla.Services;
using Sqlzilla.Views;
using Windows.Graphics;

namespace Sqlzilla;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionStore _store = new();
    private readonly SessionStore _sessionStore = new();
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _restoring;
    private readonly List<ServerGroup> _groups;
    private readonly Dictionary<string, List<string>> _databaseCache = new();
    private string? _lastServerId;

    public ObservableCollection<ExplorerEntry> RootEntries { get; } = new();
    private TreeView ServerTree = null!;

    /// <summary>Window handle for file pickers.</summary>
    public static IntPtr Handle { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Sqlzilla";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        SizeAndCenter();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));
        WelcomeArt.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AboutArt.png")));

        _groups = _store.Load();
        RebuildTree(expandGroups: true);
        RestoreSession();
        _sessionTimer.Tick += (_, _) => SaveSession();
        DatabaseListsChanged += OnDatabaseListsChanged;
        Tabs.SizeChanged += (_, _) => { foreach (var t in Tabs.TabItems.OfType<TabViewItem>()) t.MaxWidth = TabMaxWidth; };
        Root.Loaded += async (_, _) => await CheckForUpdateAsync();
        AppWindow.Closing += OnClosing;
    }

    private XamlRoot XamlRoot => Root.XamlRoot;

    private void SizeAndCenter()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(area.Width * 0.8);
        var height = (int)(area.Height * 0.85);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private QueryTab? CurrentQuery => (Tabs.SelectedItem as TabViewItem)?.Content as QueryTab;

    private IEnumerable<(TabViewItem Tab, QueryTab Query)> OpenQueries =>
        Tabs.TabItems.OfType<TabViewItem>().Where(t => t.Content is QueryTab).Select(t => (t, (QueryTab)t.Content));

    private void Save() => _store.Save(_groups);

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var update = await Dialogs.ShowAboutAsync(XamlRoot);
        if (update is not null) await Dialogs.OfferUpdateAsync(XamlRoot, update);
    }

    private async Task CheckForUpdateAsync()
    {
        UpdateInfo? update;
        try { update = await UpdateService.CheckAsync(CancellationToken.None); }
        catch { return; }
        if (update is not null) await Dialogs.OfferUpdateAsync(XamlRoot, update);
    }

    // ------------------------------------------------------------------ tree

    private void RebuildTree(bool expandGroups = false, ISet<string>? expanded = null)
    {
        expanded ??= _explorerFilter.Length > 0 ? new HashSet<string>() : ExpandedIds();
        var selectedId = (ServerTree?.SelectedItem as ExplorerEntry)?.Id;
        RootEntries.Clear();
        foreach (var entry in BuildExplorerEntries(_explorerFilter, expanded, expandGroups)) RootEntries.Add(entry);
        ReplaceServerTree();
        EmptyHint.Text = _groups.Count == 0
            ? "No groups yet. Create a group (e.g. Production, Develop) and add servers to it."
            : $"Nothing matches \u201C{_explorerFilter}\u201D.";
        EmptyHint.Visibility = RootEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedId != null && VisibleEntries(RootEntries).FirstOrDefault(e => e.Id == selectedId) is { } selected) SelectWhenShown(selected);
    }

    /// <summary>
    /// Swaps in a new TreeView. Reusing one lets recycled item containers keep their old collapsed state,
    /// so after a search some matches stayed hidden inside collapsed servers.
    /// </summary>
    private void ReplaceServerTree()
    {
        if (ServerTree != null) ServerTree.ItemsSource = null;
        ServerTree = new TreeView
        {
            ItemsSource = RootEntries,
            ItemTemplate = (DataTemplate)Root.Resources["ExplorerItemTemplate"],
            SelectionMode = TreeViewSelectionMode.Single,
            CanDragItems = false,
            AllowDrop = false,
        };
        ServerTree.ItemInvoked += ServerTree_ItemInvoked;
        ServerTree.Expanding += ServerTree_Expanding;
        ServerTree.Collapsed += ServerTree_Collapsed;
        ServerTree.DoubleTapped += ServerTree_DoubleTapped;
        ServerTree.RightTapped += ServerTree_RightTapped;
        ServerTreeHost.Child = ServerTree;
    }

    /// <summary>
    /// Selects the entry once the tree has laid it out. Selecting an item inside a collapsed node makes WinUI
    /// spin forever, so this waits until every ancestor is really expanded, and gives up if it never is.
    /// </summary>
    private void SelectWhenShown(ExplorerEntry entry)
    {
        var tree = ServerTree;
        var passes = 0;
        void Select()
        {
            if (tree != ServerTree || !Flatten(RootEntries).Contains(entry)) return;   // rebuilt since
            if (ShownInTree(tree, RootEntries, entry)) tree.SelectedItem = entry;
            else if (++passes < 10) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Select);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Select);
    }

    /// <summary>True when every ancestor's tree item is actually expanded, not just marked as expanded.</summary>
    private static bool ShownInTree(TreeView tree, IEnumerable<ExplorerEntry> roots, ExplorerEntry entry)
    {
        List<ExplorerEntry>? PathTo(IEnumerable<ExplorerEntry> level)
        {
            foreach (var e in level)
            {
                if (ReferenceEquals(e, entry)) return new();
                if (PathTo(e.Children) is { } rest) { rest.Insert(0, e); return rest; }
            }
            return null;
        }
        return PathTo(roots) is { } path && path.All(p => tree.ContainerFromItem(p) is TreeViewItem { IsExpanded: true });
    }

    /// <summary>
    /// Entries not hidden inside a collapsed parent. Only these may be given to TreeView.SelectedItem:
    /// selecting a hidden item makes WinUI spin forever.
    /// </summary>
    private static IEnumerable<ExplorerEntry> VisibleEntries(IEnumerable<ExplorerEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(e.IsExpanded ? VisibleEntries(e.Children) : []));

    private HashSet<string> ExpandedIds() => new(Flatten(RootEntries).Where(e => e.IsExpanded).Select(e => e.Id));

    /// <summary>
    /// Builds explorer entries. With a filter, keeps groups, servers and (already loaded) databases whose names contain it,
    /// and expands everything so the matches are visible.
    /// </summary>
    private List<ExplorerEntry> BuildExplorerEntries(string filter, ISet<string> expanded, bool expandGroups)
    {
        var searching = filter.Length > 0;
        bool Matches(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var result = new List<ExplorerEntry>();
        foreach (var g in _groups)
        {
            var groupMatches = searching && Matches(g.Name);
            var groupEntry = ExplorerEntry.ForGroup(g);
            foreach (var s in g.Servers)
            {
                _databaseCache.TryGetValue(s.Id, out var cached);
                var serverMatches = !searching || groupMatches || Matches(s.DisplayName) || Matches(s.ResolvedServer) || (s.HasDatabase && Matches(s.ResolvedDatabase));
                var databases = serverMatches ? cached : cached?.Where(Matches).ToList();
                if (!serverMatches && (databases == null || databases.Count == 0)) continue;

                var serverEntry = ExplorerEntry.ForServer(g, s);
                if (!s.HasDatabase && databases != null) FillDatabases(serverEntry, databases);
                // While searching, open servers that are only listed because of a matching database.
                serverEntry.IsExpanded = !s.HasDatabase && serverEntry.DatabasesLoaded && (searching ? !serverMatches : expanded.Contains(s.Id));
                groupEntry.Children.Add(serverEntry);
            }
            if (searching && !groupMatches && groupEntry.Children.Count == 0) continue;
            groupEntry.IsExpanded = searching || expandGroups || expanded.Contains(g.Id);
            result.Add(groupEntry);
        }
        return result;
    }

    private static IEnumerable<ExplorerEntry> Flatten(IEnumerable<ExplorerEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(Flatten(e.Children)));

    private ExplorerEntry? FindEntry(string id) => Flatten(RootEntries).FirstOrDefault(e => e.Id == id);

    private static void FillDatabases(ExplorerEntry serverEntry, IEnumerable<string> databases)
    {
        serverEntry.Children.Clear();
        foreach (var db in databases) serverEntry.Children.Add(ExplorerEntry.ForDatabase(serverEntry.Group, serverEntry.Server!, db));
        if (serverEntry.Children.Count == 0)
            serverEntry.Children.Add(ExplorerEntry.Placeholder(serverEntry.Group, serverEntry.Server!, "(no accessible databases)"));
        serverEntry.DatabasesLoaded = true;
    }

    private async Task LoadDatabasesAsync(ExplorerEntry serverEntry, bool force)
    {
        if (serverEntry.Kind != ExplorerEntryKind.Server || serverEntry.Server!.HasDatabase) return;
        if (serverEntry.DatabasesLoaded && !force) return;
        serverEntry.DatabasesLoaded = true;
        var server = serverEntry.Server;
        serverEntry.Children.Clear();
        serverEntry.Children.Add(ExplorerEntry.Placeholder(serverEntry.Group, server, "Loading…"));
        try
        {
            var databases = await Task.Run(() => ConnectionFactory.ListDatabasesAsync(server, serverEntry.Group.IsReadOnly, CancellationToken.None));
            _databaseCache[server.Id] = databases;
            FillDatabases(serverEntry, databases);
        }
        catch (Exception ex)
        {
            serverEntry.DatabasesLoaded = false;
            serverEntry.Children.Clear();
            serverEntry.Children.Add(ExplorerEntry.Placeholder(serverEntry.Group, server, "⚠ " + FirstLine(ex.Message)));
        }
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOfAny(['\r', '\n']);
        return i < 0 ? s : s[..i];
    }

    private void ServerTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not ExplorerEntry entry) return;
        entry.IsExpanded = true;   // the template binds IsExpanded one way, so user expands are copied back here
        if (entry.Kind == ExplorerEntryKind.Server) _ = LoadDatabasesAsync(entry, false);
    }

    private void ServerTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (args.Item is ExplorerEntry entry) entry.IsExpanded = false;
    }

    private void ServerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not ExplorerEntry entry) return;
        if (entry.Kind == ExplorerEntryKind.Group || (entry.Kind == ExplorerEntryKind.Server && !entry.Server!.HasDatabase))
            entry.IsExpanded = !entry.IsExpanded;
        if (entry.Server != null) _lastServerId = entry.Server.Id;
    }

    private void ServerTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var entry = EntryFromElement(e.OriginalSource as DependencyObject);
        if (entry is not { IsQueryTarget: true }) return;
        e.Handled = true;
        OpenQuery(entry);
    }

    private ExplorerEntry? EntryFromElement(DependencyObject? element)
    {
        for (var d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return ServerTree.ItemFromContainer(item) as ExplorerEntry ?? item.DataContext as ExplorerEntry;
        return null;
    }

    private void ServerTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var entry = EntryFromElement(e.OriginalSource as DependencyObject);
        if (entry == null || entry.Kind == ExplorerEntryKind.Placeholder && entry.Server == null) return;
        e.Handled = true;
        ServerTree.SelectedItem = entry;

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        switch (entry.Kind)
        {
            case ExplorerEntryKind.Group:
                Add("Add server…", "\uE710", () => _ = AddServerAsync(entry.Group));
                Add("Edit group…", "\uE70F", () => _ = EditGroupAsync(entry.Group));
                menu.Items.Add(new MenuFlyoutSeparator());
                Add("Move up", "\uE70E", () => MoveGroup(entry.Group, -1));
                Add("Move down", "\uE70D", () => MoveGroup(entry.Group, 1));
                menu.Items.Add(new MenuFlyoutSeparator());
                Add("Delete group", "\uE74D", () => _ = DeleteGroupAsync(entry.Group));
                break;
            case ExplorerEntryKind.Server:
                Add("New query", "\uE8A5", () => OpenQuery(entry));
                menu.Items.Add(OpenTabsSubMenu(entry));
                if (entry.Server!.HasDatabase) menu.Items.Add(ImportMenuItem(entry));
                if (!entry.Server!.HasDatabase) Add("Refresh databases", "\uE72C", () => _ = RefreshServerAsync(entry));
                menu.Items.Add(new MenuFlyoutSeparator());
                Add("Edit server…", "\uE70F", () => _ = EditServerAsync(entry.Group, entry.Server));
                Add("Duplicate", "\uE8C8", () => DuplicateServer(entry.Group, entry.Server));
                menu.Items.Add(new MenuFlyoutSeparator());
                Add("Delete server", "\uE74D", () => _ = DeleteServerAsync(entry.Group, entry.Server));
                break;
            case ExplorerEntryKind.Database:
                Add("New query", "\uE8A5", () => OpenQuery(entry));
                menu.Items.Add(OpenTabsSubMenu(entry));
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(ImportMenuItem(entry));
                break;
            case ExplorerEntryKind.Placeholder:
                Add("Retry", "\uE72C", () =>
                {
                    if (Flatten(RootEntries).FirstOrDefault(x => x.Kind == ExplorerEntryKind.Server && x.Server == entry.Server) is { } s)
                        _ = RefreshServerAsync(s);
                });
                break;
        }
        menu.ShowAt(ServerTree, new FlyoutShowOptions { Position = e.GetPosition(ServerTree) });
    }

    /// <summary>Imports a file into the entry's database; not offered for read-only groups.</summary>
    private MenuFlyoutItem ImportMenuItem(ExplorerEntry entry)
    {
        var readOnly = entry.Group.IsReadOnly;
        var item = new MenuFlyoutItem
        {
            Text = readOnly ? "Import data… (read-only group)" : "Import data…",
            Icon = new FontIcon { Glyph = "\uE8B5" },
            IsEnabled = !readOnly,
        };
        item.Click += async (_, _) => await ImportDialog.ShowAsync(XamlRoot, entry.Server!, entry.TargetDatabase);
        return item;
    }

    /// <summary>
    /// Lists the open query windows for a database (or, for a server with a database list, every database on it); picking one selects that tab.
    /// </summary>
    private MenuFlyoutItemBase OpenTabsSubMenu(ExplorerEntry entry)
    {
        var allDatabases = entry.Kind == ExplorerEntryKind.Server && !entry.Server!.HasDatabase;
        var matches = OpenQueries
            .Where(q => q.Query.Server.Id == entry.Server!.Id)
            .Where(q => allDatabases || string.Equals(q.Query.Database, entry.TargetDatabase, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sub = new MenuFlyoutSubItem { Text = $"Open tabs ({matches.Count})", Icon = new FontIcon { Glyph = "\uE737" } };
        if (matches.Count == 0)
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "No open tabs", IsEnabled = false });
            return sub;
        }
        foreach (var (tab, query) in matches)
        {
            var text = allDatabases && query.Database.Length > 0 ? $"{query.Title}  ({query.Database})" : query.Title;
            if (query.IsDirty) text += " ●";
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => Tabs.SelectedItem = tab;
            sub.Items.Add(item);
        }
        return sub;
    }

    private async Task RefreshServerAsync(ExplorerEntry serverEntry)
    {
        serverEntry.IsExpanded = true;
        await LoadDatabasesAsync(serverEntry, true);
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        _databaseCache.Clear();
        _searchLoads.Clear();
        _searchLoadsFailed.Clear();
        foreach (var entry in Flatten(RootEntries).Where(x => x.Kind == ExplorerEntryKind.Server).ToList())
        {
            if (entry.IsExpanded) _ = LoadDatabasesAsync(entry, true);
            else if (!entry.Server!.HasDatabase)
            {
                entry.DatabasesLoaded = false;
                entry.Children.Clear();
                entry.Children.Add(ExplorerEntry.Placeholder(entry.Group, entry.Server, "Loading…"));
            }
        }
    }

    // ------------------------------------------------------------------ groups & servers

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var color = GroupColors.Presets[_groups.Count % GroupColors.Presets.Length];
        var group = new ServerGroup { Name = "", Color = color };
        if (!await Dialogs.EditGroupAsync(XamlRoot, group, isNew: true)) return;
        _groups.Add(group);
        Save();
        RebuildTree();
        if (FindEntry(group.Id) is { } entry) entry.IsExpanded = true;
    }

    private async void NewServer_Click(object sender, RoutedEventArgs e)
    {
        var group = (ServerTree.SelectedItem as ExplorerEntry)?.Group ?? _groups.FirstOrDefault();
        if (group == null)
        {
            group = new ServerGroup { Name = "", Color = GroupColors.Presets[4] };
            if (!await Dialogs.EditGroupAsync(XamlRoot, group, isNew: true)) return;
            _groups.Add(group);
            Save();
            RebuildTree();
        }
        await AddServerAsync(group);
    }

    private async Task EditGroupAsync(ServerGroup group)
    {
        if (!await Dialogs.EditGroupAsync(XamlRoot, group, isNew: false)) return;
        Save();
        RebuildTree();
        RefreshTabsFor(s => group.Servers.Contains(s));
    }

    private void MoveGroup(ServerGroup group, int delta)
    {
        var index = _groups.IndexOf(group);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _groups.Count) return;
        _groups.RemoveAt(index);
        _groups.Insert(target, group);
        Save();
        RebuildTree();
    }

    private async Task DeleteGroupAsync(ServerGroup group)
    {
        var detail = group.Servers.Count == 0 ? "" : $" and its {group.Servers.Count} server{(group.Servers.Count == 1 ? "" : "s")}";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete group", $"Delete “{group.Name}”{detail}? Open query windows stay open.")) return;
        _groups.Remove(group);
        Save();
        RebuildTree();
    }

    private async Task AddServerAsync(ServerGroup group)
    {
        var result = await Dialogs.EditServerAsync(XamlRoot, new ServerConnection(), _groups, group, isNew: true);
        if (result is not { } r) return;
        r.Group.Servers.Add(r.Server);
        Save();
        RebuildTree();
        if (FindEntry(r.Group.Id) is { } g) g.IsExpanded = true;
        if (FindEntry(r.Server.Id) is { } s)
        {
            SelectWhenShown(s);
            _lastServerId = r.Server.Id;
            if (_pendingFiles.Count > 0) await OpenPendingFilesAsync();
            else if (r.Server.HasDatabase) OpenQuery(s);
            else s.IsExpanded = true;
        }
    }

    private async Task EditServerAsync(ServerGroup group, ServerConnection server)
    {
        var result = await Dialogs.EditServerAsync(XamlRoot, server, _groups, group, isNew: false);
        if (result is not { } r) return;
        var index = group.Servers.IndexOf(server);
        if (index < 0) return;
        if (r.Group == group) group.Servers[index] = r.Server;
        else
        {
            group.Servers.RemoveAt(index);
            r.Group.Servers.Add(r.Server);
        }
        _databaseCache.Remove(server.Id);
        Save();
        RebuildTree();
        foreach (var (tab, query) in OpenQueries.Where(q => q.Query.Server.Id == server.Id).ToList())
            query.SetGroup(r.Group, r.Server);
        UpdateChrome();
    }

    private void DuplicateServer(ServerGroup group, ServerConnection server)
    {
        var copy = server.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = server.Name.Length > 0 ? server.Name + " (copy)" : "";
        group.Servers.Insert(group.Servers.IndexOf(server) + 1, copy);
        Save();
        RebuildTree();
    }

    private async Task DeleteServerAsync(ServerGroup group, ServerConnection server)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete server", $"Delete “{server.DisplayName}” from “{group.Name}”?")) return;
        group.Servers.Remove(server);
        _databaseCache.Remove(server.Id);
        Save();
        RebuildTree();
    }

    private void RefreshTabsFor(Func<ServerConnection, bool> predicate)
    {
        foreach (var (_, query) in OpenQueries)
        {
            var group = _groups.FirstOrDefault(g => g.Servers.Any(s => s.Id == query.Server.Id));
            if (group != null && predicate(query.Server)) query.SetGroup(group, group.Servers.First(s => s.Id == query.Server.Id));
        }
        UpdateChrome();
    }

    // ------------------------------------------------------------------ query windows

    private void OpenQuery(ExplorerEntry entry)
    {
        if (entry.Server == null) return;
        _lastServerId = entry.Server.Id;
        AddTab(new QueryTab(entry.Group, entry.Server, entry.TargetDatabase));
    }

    /// <summary>New query on the selected tree entry, else the current tab's connection, else the last used server.</summary>
    private void NewQuery()
    {
        if (DefaultConnection() is not var (group, server, database))
        {
            NewServer_Click(this, new RoutedEventArgs());
            return;
        }
        _lastServerId = server.Id;
        AddTab(new QueryTab(group, server, database));
    }

    private (ServerGroup Group, ServerConnection Server, string Database)? DefaultConnection()
    {
        if (ServerTree.SelectedItem is ExplorerEntry { Server: { } selectedServer } selected && selected.Kind != ExplorerEntryKind.Placeholder)
            return (selected.Group, selectedServer, selected.TargetDatabase);
        if (CurrentQuery is { } current) return (current.Group, current.Server, current.Database);
        var group = _groups.FirstOrDefault(g => g.Servers.Any(s => s.Id == _lastServerId)) ?? _groups.FirstOrDefault(g => g.Servers.Count > 0);
        var server = group?.Servers.FirstOrDefault(s => s.Id == _lastServerId) ?? group?.Servers.FirstOrDefault();
        return group != null && server != null ? (group, server, server.ResolvedDatabase) : null;
    }

    private void NewQuery_Click(object sender, RoutedEventArgs e) => NewQuery();

    private void Tabs_AddTabButtonClick(TabView sender, object args) => NewQuery();

    private void AddTab(QueryTab query)
    {
        var tab = new TabViewItem
        {
            Content = query,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            MaxWidth = TabMaxWidth,
        };
        query.StateChanged += (_, _) =>
        {
            UpdateTabHeader(tab, query);
            if (ReferenceEquals(Tabs.SelectedItem, tab)) UpdateChrome();
            QueueSessionSave();
        };
        UpdateTabHeader(tab, query);
        tab.RightTapped += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != tab; d = VisualTreeHelper.GetParent(d))
                if (d == query) return;   // right-clicks inside the editor/results have their own menus
            e.Handled = true;
            TabContextMenu(tab).ShowAt(tab, new FlyoutShowOptions { Position = e.GetPosition(tab) });
        };
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateWelcome();
        QueueSessionSave();
    }

    // ------------------------------------------------------------------ session (open tabs survive restarts, like Notepad++)

    private void RestoreSession()
    {
        var state = _sessionStore.Load();
        _restoring = true;
        try
        {
            foreach (var saved in state.Tabs)
            {
                var group = _groups.FirstOrDefault(g => g.Servers.Any(s => s.Id == saved.ServerId));
                var server = group?.Servers.First(s => s.Id == saved.ServerId);
                if (group == null || server == null) continue;   // server was deleted since
                var query = new QueryTab(group, server, saved.Database);
                query.Restore(saved);
                AddTab(query);
            }
        }
        finally { _restoring = false; }
        if (Tabs.TabItems.Count > 0)
            Tabs.SelectedIndex = Math.Clamp(state.SelectedIndex, 0, Tabs.TabItems.Count - 1);
    }

    /// <summary>Debounced so typing doesn't hammer the disk, but a crash loses at most a couple of seconds.</summary>
    private void QueueSessionSave()
    {
        if (_restoring) return;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        _sessionTimer.Stop();
        var queries = OpenQueries.ToList();
        try
        {
            _sessionStore.Save(new SessionState
            {
                Tabs = queries.Select(q => q.Query.ToSessionTab()).ToList(),
                SelectedIndex = Math.Max(0, queries.FindIndex(q => ReferenceEquals(q.Tab, Tabs.SelectedItem))),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A tab may take at most half the query area before its text is trimmed.</summary>
    private double TabMaxWidth => Tabs.ActualWidth > 0 ? Tabs.ActualWidth / 2 : double.PositiveInfinity;

    private static void UpdateTabHeader(TabViewItem tab, QueryTab query)
    {
        var color = GroupColors.Parse(query.Group.Color);
        // Tabs size to their content; when capped at TabMaxWidth the server/database text (star column) trims first.
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Add(FrameworkElement e, int column) { Grid.SetColumn(e, column); header.Children.Add(e); }
        Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
        }, 0);
        Add(new TextBlock { Text = query.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 1);
        Add(new TextBlock
        {
            Text = query.Database.Length > 0 ? $"{query.Server.TabName}.{query.Database}" : query.Server.TabName,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        }, 2);
        if (query.IsDirty)
            Add(new TextBlock { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(color) }, 3);
        tab.Header = header;
        tab.Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x30));
        ToolTipService.SetToolTip(tab, $"{query.Group.Name} · {query.Description}");
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateChrome();
        QueueSessionSave();
    }

    /// <summary>Tints the title bar with the selected query window's group colour.</summary>
    private void UpdateChrome()
    {
        if (CurrentQuery is not { } query)
        {
            AppTitleBar.Background = null;
            TitleGroupBadge.Visibility = Visibility.Collapsed;
            TitleContext.Text = "";
            Title = "Sqlzilla";
            return;
        }
        var color = GroupColors.Parse(query.Group.Color);
        AppTitleBar.Background = new SolidColorBrush(GroupColors.WithAlpha(color, 0x50));
        TitleGroupBadge.Background = new SolidColorBrush(color);
        TitleGroupText.Foreground = new SolidColorBrush(GroupColors.ContrastText(color));
        TitleGroupText.Text = query.Group.LabelWithReadOnly.ToUpperInvariant();
        TitleGroupBadge.Visibility = Visibility.Visible;
        TitleContext.Text = $"{query.Title} — {query.Description}";
        Title = $"{query.Title} - {query.Server.DisplayName} - Sqlzilla";
    }

    private void UpdateWelcome()
    {
        var any = Tabs.TabItems.Count > 0;
        Tabs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        UpdateChrome();
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => await CloseTabAsync(args.Tab);

    private async Task<bool> CloseTabAsync(TabViewItem tab)
    {
        if (tab.Content is QueryTab query)
        {
            if (query.IsDirty)
            {
                Tabs.SelectedItem = tab;
                var choice = await Dialogs.ConfirmUnsavedAsync(XamlRoot, query.Title);
                if (choice == ContentDialogResult.None) return false;
                if (choice == ContentDialogResult.Primary && !await query.SaveAsync(false)) return false;
            }
            query.Close();
        }
        Tabs.TabItems.Remove(tab);
        UpdateWelcome();
        ReclaimMemory();
        QueueSessionSave();
        return true;
    }

    private MenuFlyout TabContextMenu(TabViewItem tab)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Close", "\uE711", () => _ = CloseTabAsync(tab));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Close all but this - No Verify", "\uE8BB", () => CloseTabsWithoutAsking(Tabs.TabItems.OfType<TabViewItem>().Where(t => t != tab)));
        Add("Close ALL Tabs - No Verify", "\uE8BB", () => CloseTabsWithoutAsking(Tabs.TabItems.OfType<TabViewItem>()));
        return menu;
    }

    /// <summary>Closes the tabs straight away, discarding any unsaved changes.</summary>
    private void CloseTabsWithoutAsking(IEnumerable<TabViewItem> tabs)
    {
        foreach (var tab in tabs.ToList())
        {
            if (tab.Content is QueryTab query) query.Close();
            Tabs.TabItems.Remove(tab);
        }
        UpdateWelcome();
        ReclaimMemory();
        QueueSessionSave();
    }

    /// <summary>Open tabs, including unsaved text, are remembered in the session, so closing doesn't need to ask.</summary>
    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) => SaveSession();

    private CancellationTokenSource? _reclaimCts;

    /// <summary>
    /// A closed tab's native editor (Scintilla document, D3D device) is only freed once the GC finalises its WinRT wrappers
    /// and XAML renders a frame to process deferred releases; an idle window renders nothing, so keep rendering until memory settles.
    /// </summary>
    private async void ReclaimMemory()
    {
        _reclaimCts?.Cancel();
        var cts = _reclaimCts = new CancellationTokenSource();
        CompositionTarget.Rendering += KeepRendering;
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var last = long.MaxValue;
            for (int i = 0, stable = 0; i < 40 && stable < 4; i++)
            {
                await Task.Delay(500, cts.Token);
                await Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                process.Refresh();
                var now = process.PrivateMemorySize64;
                stable = now > last - (1 << 20) ? stable + 1 : 0;
                last = Math.Min(last, now);
            }
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            await Task.Run(() => GC.Collect());
        }
        catch (OperationCanceledException) { }
        finally
        {
            CompositionTarget.Rendering -= KeepRendering;
        }
    }

    private static void KeepRendering(object? sender, object e) { }

    // ------------------------------------------------------------------ accelerators

    private void NewQueryAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        NewQuery();
    }

    private async void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Tabs.SelectedItem is TabViewItem tab) await CloseTabAsync(tab);
    }

    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (CurrentQuery is { } query) await query.OpenFileAsync();
    }

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (CurrentQuery is { } query) await query.SaveAsync(false);
    }

    private async void ExecuteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (CurrentQuery is { } query) await query.ExecuteAsync();
    }
}
