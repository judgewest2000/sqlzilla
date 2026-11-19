using Microsoft.Data.SqlClient;
using Sqlzilla.Models;
using Sqlzilla.Services;

namespace Sqlzilla.Tests;

public class ConnectionTests
{
    [Fact]
    public void BuildsEntraInteractiveWithDatabaseOverrideAndReadOnlyIntent()
    {
        var server = new ServerConnection { Server = "my.database.windows.net", AuthMode = AuthMode.EntraInteractive, UserName = "me@corp.com" };

        var b = new SqlConnectionStringBuilder(ConnectionFactory.Build(server, "Sales", readOnlyIntent: true));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, b.Authentication);
        Assert.Equal("me@corp.com", b.UserID);
        Assert.Equal("Sales", b.InitialCatalog);
        Assert.Equal(ApplicationIntent.ReadOnly, b.ApplicationIntent);
        Assert.Equal("Sqlzilla", b.ApplicationName);
    }

    [Fact]
    public void BuildsServicePrincipalFromUserNameAndPassword()
    {
        var server = new ServerConnection { Server = "s", AuthMode = AuthMode.EntraServicePrincipal, UserName = "app-id", Password = "secret" };
        var b = new SqlConnectionStringBuilder(ConnectionFactory.Build(server));
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, b.Authentication);
        Assert.Equal(("app-id", "secret"), (b.UserID, b.Password));
    }

    [Fact]
    public void RawConnectionStringKeepsItsSettingsButHonoursDatabaseOverride()
    {
        var server = new ServerConnection
        {
            AuthMode = AuthMode.ConnectionString,
            ConnectionString = "Server=tcp:x,1433;Database=Main;User ID=u;Password=p;Application Name=Mine",
        };
        var b = new SqlConnectionStringBuilder(ConnectionFactory.Build(server, "Other"));
        Assert.Equal(("tcp:x,1433", "Other", "Mine"), (b.DataSource, b.InitialCatalog, b.ApplicationName));
        Assert.Equal("Main", server.ResolvedDatabase);
        Assert.True(server.HasDatabase);
    }

    [Fact]
    public void StoreRoundTripsAndEncryptsSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlzilla-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConnectionStore(dir);
            var group = new ServerGroup { Name = "Prod", Color = "#FF0000", IsReadOnly = true };
            group.Servers.Add(new ServerConnection { Server = "s", AuthMode = AuthMode.SqlLogin, UserName = "sa", Password = "hunter2" });
            store.Save([group]);

            Assert.DoesNotContain("hunter2", File.ReadAllText(Path.Combine(dir, "connections.json")));
            var loaded = Assert.Single(store.Load());
            Assert.Equal(("Prod", "#FF0000", true), (loaded.Name, loaded.Color, loaded.IsReadOnly));
            Assert.Equal("hunter2", Assert.Single(loaded.Servers).Password);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FirstRunHasDefaultGroups() =>
        Assert.Equal(["Production", "Develop", "Read-only"], new ConnectionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Load().Select(g => g.Name));

    [Fact]
    public void SessionRoundTripsTabsAndToleratesCorruptFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlzilla-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(dir);
            Assert.Empty(store.Load().Tabs);

            store.Save(new SessionState
            {
                SelectedIndex = 1,
                Tabs =
                [
                    new() { ServerId = "a", Database = "Orders", UntitledName = "SQLQuery3.sql", Text = "SELECT 1;\n", IsDirty = true },
                    new() { ServerId = "b", Database = "", FilePath = @"C:\x.sql", Text = "GO" },
                ],
            });
            var loaded = store.Load();
            Assert.Equal(1, loaded.SelectedIndex);
            Assert.Equal(("a", "Orders", "SQLQuery3.sql", "SELECT 1;\n", true),
                (loaded.Tabs[0].ServerId, loaded.Tabs[0].Database, loaded.Tabs[0].UntitledName, loaded.Tabs[0].Text, loaded.Tabs[0].IsDirty));
            Assert.Equal(@"C:\x.sql", loaded.Tabs[1].FilePath);

            File.WriteAllText(Path.Combine(dir, "session.json"), "{ not json");
            Assert.Empty(store.Load().Tabs);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class ResultFormattingTests
{
    [Fact]
    public void FormatsValuesLikeSsms()
    {
        Assert.Null(QuerySession.Format(DBNull.Value));
        Assert.Equal("1", QuerySession.Format(true));
        Assert.Equal("0x0AFF", QuerySession.Format(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("2024-03-01 13:45:00.000", QuerySession.Format(new DateTime(2024, 3, 1, 13, 45, 0)));
        Assert.Equal("1.5", QuerySession.Format(1.5m));
    }

    [Fact]
    public void CsvQuotesFieldsThatNeedIt()
    {
        var csv = ResultExport.ToCsv([new("a", "int"), new("b", "nvarchar")], [["1", "x,y"], [null, "say \"hi\""]]);
        Assert.Equal("a,b\r\n1,\"x,y\"\r\n,\"say \"\"hi\"\"\"\r\n", csv);
    }

    [Fact]
    public void TsvWritesNullAndFlattensNewlines()
    {
        var tsv = ResultExport.ToTsv([new("a", "int")], [[null], ["x\ny"]], includeHeaders: false);
        Assert.Equal("NULL\r\nx y", tsv);
        Assert.Equal("a\r\n42", ResultExport.ToTsv([new("a", "int")], [["42"]], includeHeaders: true));
    }
}
