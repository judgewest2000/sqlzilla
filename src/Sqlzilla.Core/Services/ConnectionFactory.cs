using Microsoft.Data.SqlClient;
using Sqlzilla.Models;

namespace Sqlzilla.Services;

public static class ConnectionFactory
{
    public const string ApplicationName = "Sqlzilla";

    /// <summary>
    /// Builds the connection string for <paramref name="server"/>.
    /// <paramref name="database"/> overrides the configured database (used when opening a database listed under a server).
    /// </summary>
    public static string Build(ServerConnection server, string? database = null, bool readOnlyIntent = false)
    {
        SqlConnectionStringBuilder b;
        if (server.AuthMode == AuthMode.ConnectionString)
        {
            b = new SqlConnectionStringBuilder(server.ConnectionString);
            if (string.IsNullOrEmpty(b.ApplicationName) || b.ApplicationName == "Core Microsoft SqlClient Data Provider")
                b.ApplicationName = ApplicationName;
        }
        else
        {
            b = new SqlConnectionStringBuilder
            {
                DataSource = server.Server.Trim(),
                InitialCatalog = server.Database.Trim(),
                ApplicationName = ApplicationName,
                ConnectTimeout = server.ConnectTimeout > 0 ? server.ConnectTimeout : 30,
                TrustServerCertificate = server.TrustServerCertificate,
                Encrypt = server.Encrypt switch
                {
                    EncryptMode.Optional => SqlConnectionEncryptOption.Optional,
                    EncryptMode.Strict => SqlConnectionEncryptOption.Strict,
                    _ => SqlConnectionEncryptOption.Mandatory,
                },
            };
            ApplyAuth(b, server);
        }

        if (!string.IsNullOrWhiteSpace(database)) b.InitialCatalog = database.Trim();
        if (readOnlyIntent) b.ApplicationIntent = ApplicationIntent.ReadOnly;
        return b.ConnectionString;
    }

    private static void ApplyAuth(SqlConnectionStringBuilder b, ServerConnection s)
    {
        switch (s.AuthMode)
        {
            case AuthMode.SqlLogin:
                b.UserID = s.UserName;
                b.Password = s.Password;
                break;
            case AuthMode.WindowsIntegrated:
                b.IntegratedSecurity = true;
                break;
            case AuthMode.EntraInteractive:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (s.UserName.Length > 0) b.UserID = s.UserName;   // login hint
                break;
            case AuthMode.EntraDefault:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                break;
            case AuthMode.EntraPassword:
#pragma warning disable CS0618 // Deprecated by Microsoft (no MFA) but still supported and requested.
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
#pragma warning restore CS0618
                b.UserID = s.UserName;
                b.Password = s.Password;
                break;
            case AuthMode.EntraServicePrincipal:
                b.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
                b.UserID = s.UserName;
                b.Password = s.Password;
                break;
        }
    }

    /// <summary>
    /// Registers the Entra ID provider. Interactive sign-in uses WAM, which needs the app's window handle to parent its dialog.
    /// </summary>
    public static void RegisterEntraProvider(Func<IntPtr> parentWindow)
    {
        var provider = new ActiveDirectoryAuthenticationProvider();
        provider.SetParentActivityOrWindowFunc(() => parentWindow());
#pragma warning disable CS0618
        SqlAuthenticationMethod[] methods =
        [
            SqlAuthenticationMethod.ActiveDirectoryInteractive,
            SqlAuthenticationMethod.ActiveDirectoryDefault,
            SqlAuthenticationMethod.ActiveDirectoryPassword,
            SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
            SqlAuthenticationMethod.ActiveDirectoryIntegrated,
            SqlAuthenticationMethod.ActiveDirectoryManagedIdentity,
            SqlAuthenticationMethod.ActiveDirectoryMSI,
            SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow,
            SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity,
        ];
#pragma warning restore CS0618
        foreach (var m in methods) SqlAuthenticationProvider.SetProvider(m, provider);
    }

    /// <summary>Opens a connection and returns a short description of who we connected as.</summary>
    public static async Task<string> TestAsync(ServerConnection server, CancellationToken ct)
    {
        await using var conn = new SqlConnection(Build(server));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT SUSER_SNAME(), DB_NAME(), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return $"Connected as {r.GetValue(0)} to {r.GetValue(1)} (SQL Server {r.GetValue(2)}).";
    }

    /// <summary>Databases the login can access, system databases first.</summary>
    public static async Task<List<string>> ListDatabasesAsync(ServerConnection server, bool readOnlyIntent, CancellationToken ct)
    {
        await using var conn = new SqlConnection(Build(server, readOnlyIntent: readOnlyIntent));
        await conn.OpenAsync(ct);
        return await ListDatabasesAsync(conn, ct);
    }

    /// <summary>
    /// Azure SQL Database (engine edition 5) can't evaluate HAS_DBACCESS for other databases from master, which would
    /// hide everything except master; there, sys.databases is already limited to what the login can see.
    /// </summary>
    private const string ListDatabasesSql = """
        SELECT name FROM sys.databases
        WHERE state = 0 AND (HAS_DBACCESS(name) = 1 OR CAST(SERVERPROPERTY('EngineEdition') AS int) = 5)
        ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name
        """;

    internal static async Task<List<string>> ListDatabasesAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(ListDatabasesSql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await r.ReadAsync(ct)) result.Add(r.GetString(0));
        return result;
    }
}
