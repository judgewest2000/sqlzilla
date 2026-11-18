using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Sqlzilla.Sql;

namespace Sqlzilla.Services;

public sealed record ResultColumn(string Name, string TypeName);

public sealed class ResultSet
{
    public required IReadOnlyList<ResultColumn> Columns { get; init; }

    /// <summary>Cell text, or null for SQL NULL.</summary>
    public List<string?[]> Rows { get; } = new();

    public bool Truncated { get; set; }
}

public enum MessageKind { Info, Error, Success }

public sealed record QueryMessage(MessageKind Kind, string Text);

public sealed class QueryResult
{
    public List<ResultSet> ResultSets { get; } = new();
    public List<QueryMessage> Messages { get; } = new();
    public bool HasErrors { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int TotalRows => ResultSets.Sum(r => r.Rows.Count);
}

/// <summary>
/// A query window's connection. Kept open between executions, as in SSMS, so temp tables, SET options, transactions and USE persist.
/// </summary>
public sealed class QuerySession : IAsyncDisposable
{
    public const int MaxRowsPerResultSet = 500_000;
    public const int MaxCellLength = 65_535;
    private const int MaxMessages = 10_000;

    private readonly string _connectionString;
    private SqlConnection? _connection;
    private QueryResult? _current;
    private int _batchLineOffset;

    public QuerySession(string connectionString)
    {
        _connectionString = connectionString;
        Database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
    }

    /// <summary>Current database (follows USE statements).</summary>
    public string Database { get; private set; }

    public string? LoginName { get; private set; }
    public int? SessionId { get; private set; }

    public bool IsOpen => _connection?.State == ConnectionState.Open;

    public async Task OpenAsync(CancellationToken ct)
    {
        if (IsOpen) return;
        if (_connection != null) await _connection.DisposeAsync();
        _connection = new SqlConnection(_connectionString);
        _connection.InfoMessage += OnInfoMessage;
        await _connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT SUSER_SNAME(), @@SPID", _connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            LoginName = r.IsDBNull(0) ? null : r.GetString(0);
            SessionId = r.GetInt16(1);
        }
        Database = _connection.Database;
    }

    public async Task ChangeDatabaseAsync(string database, CancellationToken ct)
    {
        await OpenAsync(ct);
        await _connection!.ChangeDatabaseAsync(database, ct);
        Database = _connection.Database;
    }

    public async Task<List<string>> ListDatabasesAsync(CancellationToken ct)
    {
        await OpenAsync(ct);
        const string sql = "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 AND state = 0 ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name";
        await using var cmd = new SqlCommand(sql, _connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await r.ReadAsync(ct)) result.Add(r.GetString(0));
        return result;
    }

    /// <summary>Loads table/view/column names of the current database for autocomplete.</summary>
    public async Task<SchemaInfo> LoadSchemaAsync(CancellationToken ct)
    {
        await OpenAsync(ct);
        const string sql = """
            SELECT s.name, o.name, o.type, c.name
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.columns c ON c.object_id = o.object_id
            WHERE o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id
            """;
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var objects = new Dictionary<(string, string), SchemaObject>();
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!objects.TryGetValue(key, out var obj))
            {
                obj = new SchemaObject(key.Item1, key.Item2, SchemaInfo.KindFromType(r.GetString(2).Trim()), new List<string>());
                objects[key] = obj;
            }
            if (!r.IsDBNull(3)) ((List<string>)obj.Columns).Add(r.GetString(3));
        }
        return new SchemaInfo(objects.Values.ToList());
    }

    public async Task<QueryResult> ExecuteAsync(string script, int lineOffset, CancellationToken ct)
    {
        var result = new QueryResult();
        var watch = Stopwatch.StartNew();
        try
        {
            await OpenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.HasErrors = true;
            result.Messages.Add(new(MessageKind.Error, ex.Message));
            result.Elapsed = watch.Elapsed;
            return result;
        }

        _current = result;
        try
        {
            foreach (var batch in SqlBatchSplitter.Split(script))
            {
                _batchLineOffset = lineOffset + batch.StartLine;
                for (var i = 0; i < batch.RepeatCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await ExecuteBatchAsync(batch.Text, result, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Messages.Add(new(MessageKind.Error, "Query was cancelled by user."));
        }
        catch (SqlException) when (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
            result.Messages.Add(new(MessageKind.Error, "Query was cancelled by user."));
        }
        finally
        {
            _current = null;
            result.Elapsed = watch.Elapsed;
        }

        if (_connection?.State == ConnectionState.Open) Database = _connection.Database;
        else result.Messages.Add(new(MessageKind.Info, "The connection was closed; it will be reopened on the next execution."));

        if (!result.HasErrors && !result.Cancelled)
            result.Messages.Add(new(MessageKind.Success, "Commands completed successfully."));
        result.Messages.Add(new(MessageKind.Info, $"Completion time: {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffffffzzz}"));
        return result;
    }

    private async Task ExecuteBatchAsync(string sql, QueryResult result, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = 0 };
        cmd.StatementCompleted += (_, e) => AddMessage(result, new(MessageKind.Info, e.RecordCount == 1 ? "(1 row affected)" : $"({e.RecordCount} rows affected)"));
        // Like SSMS, report errors as messages and keep going, so result sets after a failing statement still appear.
        _connection!.FireInfoMessageEventOnUserErrors = true;
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (reader.FieldCount == 0) continue;
                var set = new ResultSet { Columns = ReadColumns(reader) };
                result.ResultSets.Add(set);
                var values = new object[reader.FieldCount];
                while (await reader.ReadAsync(ct))
                {
                    if (set.Rows.Count >= MaxRowsPerResultSet)
                    {
                        set.Truncated = true;
                        continue;   // keep draining so following result sets and messages arrive
                    }
                    reader.GetValues(values);
                    var row = new string?[values.Length];
                    for (var c = 0; c < values.Length; c++) row[c] = Format(values[c]);
                    set.Rows.Add(row);
                }
                if (set.Truncated)
                    AddMessage(result, new(MessageKind.Info, $"Result set {result.ResultSets.Count} was limited to the first {MaxRowsPerResultSet:N0} rows."));
            }
            while (await reader.NextResultAsync(ct));
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            result.HasErrors = true;
            foreach (SqlError error in ex.Errors) AddMessage(result, FromError(error));
        }
        finally
        {
            if (_connection.State == ConnectionState.Open) _connection.FireInfoMessageEventOnUserErrors = false;
        }
    }

    private static List<ResultColumn> ReadColumns(SqlDataReader reader)
    {
        var columns = new List<ResultColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            columns.Add(new ResultColumn(name.Length == 0 ? "(No column name)" : name, reader.GetDataTypeName(i)));
        }
        return columns;
    }

    private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
    {
        if (_current is not { } result) return;
        foreach (SqlError error in e.Errors)
        {
            var message = error.Class > 10 ? FromError(error) : new QueryMessage(MessageKind.Info, error.Message);
            if (error.Class > 10) result.HasErrors = true;
            AddMessage(result, message);
        }
    }

    private QueryMessage FromError(SqlError e)
    {
        var line = e.LineNumber > 0 ? e.LineNumber + _batchLineOffset : 0;
        var where = line > 0 ? $", Line {line}" : "";
        var proc = string.IsNullOrEmpty(e.Procedure) ? "" : $", Procedure {e.Procedure}";
        return new QueryMessage(MessageKind.Error, $"Msg {e.Number}, Level {e.Class}, State {e.State}{proc}{where}\n{e.Message}");
    }

    private static void AddMessage(QueryResult result, QueryMessage message)
    {
        lock (result.Messages)
        {
            if (result.Messages.Count < MaxMessages) result.Messages.Add(message);
        }
    }

    /// <summary>Formats a value the way SSMS shows it in the grid.</summary>
    public static string? Format(object? value)
    {
        var text = value switch
        {
            null or DBNull => null,
            string s => s,
            bool b => b ? "1" : "0",
            DateTime d => d.ToString(d.Ticks % TimeSpan.TicksPerMillisecond == 0 ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture),
            byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, MaxCellLength / 2))),
            Guid g => g.ToString().ToUpperInvariant(),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            double f => f.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        return text is { Length: > MaxCellLength } ? text[..MaxCellLength] : text;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection == null) return;
        _connection.InfoMessage -= OnInfoMessage;
        await _connection.DisposeAsync();
        _connection = null;
    }
}

