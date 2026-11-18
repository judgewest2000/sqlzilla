using System.Text.Json.Serialization;
using Sqlzilla.Services;

namespace Sqlzilla.Models;

public enum AuthMode
{
    SqlLogin,
    WindowsIntegrated,
    EntraInteractive,
    EntraDefault,
    EntraPassword,
    EntraServicePrincipal,
    ConnectionString,
}

public enum EncryptMode { Mandatory, Optional, Strict }

/// <summary>A named, coloured set of servers (e.g. Production, Develop). Query windows take on the group's colour.</summary>
public sealed class ServerGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Group";

    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#2EA043";

    /// <summary>The name with a "(read-only)" suffix, unless the name already says so.</summary>
    [JsonIgnore]
    public string LabelWithReadOnly => IsReadOnly && !Name.Contains("read", StringComparison.OrdinalIgnoreCase) ? $"{Name} (read-only)" : Name;

    /// <summary>Connects with ApplicationIntent=ReadOnly and asks for confirmation before running statements that modify anything.</summary>
    public bool IsReadOnly { get; set; }

    public List<ServerConnection> Servers { get; set; } = new();

    public ServerGroup CloneShallow() => new() { Id = Id, Name = Name, Color = Color, IsReadOnly = IsReadOnly, Servers = Servers };
}

/// <summary>
/// One server. With <see cref="Database"/> set it is a leaf in the tree; without it the tree lists the server's databases.
/// Secrets are held in memory as plain text and written to disk encrypted with DPAPI (current user).
/// </summary>
public sealed class ServerConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional display name; the tree falls back to the host name.</summary>
    public string Name { get; set; } = "";

    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public AuthMode AuthMode { get; set; } = AuthMode.EntraInteractive;

    /// <summary>SQL/Entra user name, or the application (client) id for a service principal.</summary>
    public string UserName { get; set; } = "";

    public EncryptMode Encrypt { get; set; } = EncryptMode.Mandatory;
    public bool TrustServerCertificate { get; set; }
    public int ConnectTimeout { get; set; } = 30;

    [JsonIgnore] public string Password { get; set; } = "";
    [JsonIgnore] public string ConnectionString { get; set; } = "";

    [JsonPropertyName("password")]
    public string ProtectedPassword
    {
        get => SecretProtector.Protect(Password);
        set => Password = SecretProtector.Unprotect(value);
    }

    [JsonPropertyName("connectionString")]
    public string ProtectedConnectionString
    {
        get => SecretProtector.Protect(ConnectionString);
        set => ConnectionString = SecretProtector.Unprotect(value);
    }

    [JsonIgnore] public bool HasDatabase => ResolvedDatabase.Length > 0;

    /// <summary>The database this entry pins to, including one set inside a raw connection string.</summary>
    [JsonIgnore]
    public string ResolvedDatabase
    {
        get
        {
            if (AuthMode != AuthMode.ConnectionString) return Database.Trim();
            try { return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString).InitialCatalog.Trim(); }
            catch (ArgumentException) { return ""; }
        }
    }

    [JsonIgnore]
    public string ResolvedServer
    {
        get
        {
            if (AuthMode != AuthMode.ConnectionString) return Server.Trim();
            try { return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString).DataSource.Trim(); }
            catch (ArgumentException) { return ""; }
        }
    }

    [JsonIgnore]
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : ResolvedServer.Length > 0 ? ResolvedServer : "(no server)";

    public ServerConnection Clone() => new()
    {
        Id = Id,
        Name = Name,
        Server = Server,
        Database = Database,
        AuthMode = AuthMode,
        UserName = UserName,
        Password = Password,
        ConnectionString = ConnectionString,
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
        ConnectTimeout = ConnectTimeout,
    };

    public static string AuthLabel(AuthMode mode) => mode switch
    {
        AuthMode.SqlLogin => "SQL Server authentication",
        AuthMode.WindowsIntegrated => "Windows authentication",
        AuthMode.EntraInteractive => "Microsoft Entra MFA (interactive)",
        AuthMode.EntraDefault => "Microsoft Entra default (Azure CLI / Visual Studio)",
        AuthMode.EntraPassword => "Microsoft Entra password",
        AuthMode.EntraServicePrincipal => "Microsoft Entra service principal",
        AuthMode.ConnectionString => "Connection string",
        _ => mode.ToString(),
    };
}
