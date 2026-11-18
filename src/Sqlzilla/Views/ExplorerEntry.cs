using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Sqlzilla.Models;

namespace Sqlzilla.Views;

public enum ExplorerEntryKind { Group, Server, Database, Placeholder }

/// <summary>
/// View-model for one row in the server explorer: group → server → (databases, when the server isn't pinned to one).
/// </summary>
public sealed class ExplorerEntry : ObservableObject
{
    private bool _isExpanded;
    private string _label;

    private ExplorerEntry(ExplorerEntryKind kind, ServerGroup group, ServerConnection? server, string label)
    {
        Kind = kind;
        Group = group;
        Server = server;
        _label = label;
    }

    public static ExplorerEntry ForGroup(ServerGroup g) => new(ExplorerEntryKind.Group, g, null, g.Name);

    public static ExplorerEntry ForServer(ServerGroup g, ServerConnection s)
    {
        var entry = new ExplorerEntry(ExplorerEntryKind.Server, g, s, s.DisplayName);
        if (!s.HasDatabase) entry.Children.Add(Placeholder(g, s, "Loading…"));
        return entry;
    }

    public static ExplorerEntry ForDatabase(ServerGroup g, ServerConnection s, string database) =>
        new(ExplorerEntryKind.Database, g, s, database) { Database = database };

    public static ExplorerEntry Placeholder(ServerGroup g, ServerConnection s, string text) =>
        new(ExplorerEntryKind.Placeholder, g, s, text);

    public ExplorerEntryKind Kind { get; }
    public ServerGroup Group { get; }
    public ServerConnection? Server { get; }
    public string? Database { get; private init; }
    public ObservableCollection<ExplorerEntry> Children { get; } = new();

    /// <summary>True once a server's database list has been fetched (or is being fetched).</summary>
    public bool DatabasesLoaded { get; set; }

    public string Id => Kind switch
    {
        ExplorerEntryKind.Group => Group.Id,
        ExplorerEntryKind.Server => Server!.Id,
        _ => $"{Server!.Id}/{Database ?? Label}",
    };

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>A server with a database, or a database under a server: opening it starts a query window.</summary>
    public bool IsQueryTarget => Kind == ExplorerEntryKind.Database || (Kind == ExplorerEntryKind.Server && Server!.HasDatabase);

    /// <summary>The database a query window opened from this entry should start in ("" = the login's default).</summary>
    public string TargetDatabase => Database ?? Server?.ResolvedDatabase ?? "";

    public string Glyph => Kind switch
    {
        ExplorerEntryKind.Server when Server!.HasDatabase => "\uE968",   // server + database: end of the line
        ExplorerEntryKind.Server => "\uE839",
        ExplorerEntryKind.Database => "\uEE94",
        _ => "",
    };

    public string Detail => Kind == ExplorerEntryKind.Server && Server!.HasDatabase ? Server.ResolvedDatabase : "";

    public Visibility SwatchVisibility => Kind == ExplorerEntryKind.Group ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Kind is ExplorerEntryKind.Server or ExplorerEntryKind.Database ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadOnlyVisibility => Kind == ExplorerEntryKind.Group && Group.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
    public double LabelOpacity => Kind == ExplorerEntryKind.Placeholder ? 0.6 : 1;
    public Windows.UI.Text.FontWeight LabelWeight => Kind == ExplorerEntryKind.Group ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public Brush GroupBrush => GroupColors.Brush(Group.Color);

    public override string ToString() => Label;

    /// <summary>Icons take the group colour so every row shows which environment it belongs to.</summary>
    public Brush GlyphBrush => GroupColors.Brush(Group.Color);
}
