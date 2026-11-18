using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sqlzilla.Models;

namespace Sqlzilla.Services;

/// <summary>Persists server groups to %LOCALAPPDATA%\Sqlzilla\connections.json.</summary>
public sealed class ConnectionStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConnectionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sqlzilla");
    }

    public string RootDirectory { get; }
    private string FilePath => Path.Combine(RootDirectory, "connections.json");

    /// <summary>Loads the groups; on first run creates Production / Develop / Read-only groups.</summary>
    public List<ServerGroup> Load()
    {
        if (!File.Exists(FilePath)) return DefaultGroups();
        try
        {
            return JsonSerializer.Deserialize<List<ServerGroup>>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection rather than silently overwriting it on the next save.
            File.Copy(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return DefaultGroups();
        }
    }

    public void Save(IEnumerable<ServerGroup> groups)
    {
        Directory.CreateDirectory(RootDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(groups, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static List<ServerGroup> DefaultGroups() =>
    [
        new() { Name = "Production", Color = "#C42B1C" },
        new() { Name = "Develop", Color = "#2EA043" },
        new() { Name = "Read-only", Color = "#2575D6", IsReadOnly = true },
    ];
}
