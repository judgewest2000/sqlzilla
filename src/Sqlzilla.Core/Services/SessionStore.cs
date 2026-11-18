using System.Text.Json;

namespace Sqlzilla.Services;

/// <summary>An open query window, as remembered between runs.</summary>
public sealed class SessionTab
{
    public string ServerId { get; set; } = "";
    public string Database { get; set; } = "";
    public string? FilePath { get; set; }
    public string? UntitledName { get; set; }
    public string Text { get; set; } = "";
    /// <summary>True when <see cref="Text"/> has edits not yet saved to <see cref="FilePath"/> (or the tab was never saved).</summary>
    public bool IsDirty { get; set; }
}

public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int SelectedIndex { get; set; }
}

/// <summary>Persists open query windows, including unsaved text, to %LOCALAPPDATA%\Sqlzilla\session.json (like Notepad++).</summary>
public sealed class SessionStore
{
    public SessionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sqlzilla");
    }

    public string RootDirectory { get; }
    private string FilePath => Path.Combine(RootDirectory, "session.json");

    public SessionState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath), ConnectionStore.JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save(SessionState state)
    {
        Directory.CreateDirectory(RootDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, ConnectionStore.JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
