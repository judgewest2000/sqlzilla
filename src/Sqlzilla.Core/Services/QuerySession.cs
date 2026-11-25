using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Sqlzilla.Sql;

namespace Sqlzilla.Services;

public sealed record ResultColumn(string Name, string TypeName);

/// <summary>
/// One result set, filled while the query is still running. The reader appends rows to a pending buffer;
/// <see cref="Commit"/> moves them into <see cref="Rows"/>, so the consumer's thread is the only one that touches it.
/// Rows beyond <see cref="RowLimit"/> aren't kept in memory but spooled to a temp file, so a CSV export still has them all.
/// </summary>
public sealed class ResultSet : IDisposable
{
    private readonly object _pendingLock = new();
    private List<string?[]> _pending = new();
    private long _received;
    private volatile bool _complete;

    private readonly object _overflowLock = new();
    private FileStream? _overflow;
    private StreamWriter? _overflowWriter;
    private bool _disposed;

    public required IReadOnlyList<ResultColumn> Columns { get; init; }

    /// <summary>How many rows are kept in <see cref="Rows"/> (and shown); later ones only go to the CSV spool.</summary>
    public int RowLimit { get; init; } = QuerySession.MaxRowsPerResultSet;

    /// <summary>A finished set of rows that didn't come from a query (e.g. an import preview).</summary>
    public static ResultSet FromRows(IReadOnlyList<ResultColumn> columns, IEnumerable<string?[]> rows)
    {
        var set = new ResultSet { Columns = columns };
        set.Rows.AddRange(rows);
        set._received = set.Rows.Count;
        set.MarkComplete();
        return set;
    }

    /// <summary>Committed rows: cell text, or null for SQL NULL.</summary>
    public List<string?[]> Rows { get; } = new();

    /// <summary>Rows received so far, including those beyond <see cref="RowLimit"/>.</summary>
    public long TotalRows => Interlocked.Read(ref _received);

    /// <summary>More than <see cref="RowLimit"/> rows arrived; only the first ones are in <see cref="Rows"/>.</summary>
    public bool Truncated => TotalRows > RowLimit;

    /// <summary>The reader has moved past this set; no more rows will arrive.</summary>
    public bool IsComplete => _complete;

    internal void MarkComplete()
    {
        lock (_overflowLock) _overflowWriter?.Flush();
        _complete = true;
    }

    internal void Add(string?[] row)
    {
        if (TotalRows < RowLimit)
        {
            lock (_pendingLock) _pending.Add(row);
        }
        else Spool(row);
        Interlocked.Increment(ref _received);
    }

    private void Spool(string?[] row)
    {
        lock (_overflowLock)
        {
            if (_disposed) return;
            if (_overflowWriter == null)
            {
                var path = Path.Combine(Path.GetTempPath(), $"sqlzilla-{Guid.NewGuid():N}.csv");
                _overflow = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.DeleteOnClose);
                _overflowWriter = new StreamWriter(_overflow, new UTF8Encoding(false), 1 << 16);
            }
            ResultExport.WriteCsvRow(_overflowWriter, row);
        }
    }

    /// <summary>
    /// Writes the columns and every row received so far, including those beyond <see cref="RowLimit"/>, as UTF-8 CSV.
    /// Call from the thread that calls <see cref="Commit"/>; the file itself is written in the background.
    /// </summary>
    public Task SaveCsvAsync(string path)
    {
        var rows = Rows.ToArray();
        string? spoolPath = null;
        long spoolLength = 0;
        // Spooled rows follow the in-memory ones, so only append them when there's no gap (e.g. rows still pending mid-run).
        if (rows.Length >= RowLimit)
        {
            lock (_overflowLock)
            {
                if (_overflowWriter != null && !_disposed)
                {
                    _overflowWriter.Flush();
                    spoolPath = _overflow!.Name;
                    spoolLength = _overflow.Length;
                }
            }
        }
        return Task.Run(async () =>
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            await using (var writer = new StreamWriter(file, new UTF8Encoding(true), 1 << 16, leaveOpen: true))
                ResultExport.WriteCsv(writer, Columns, rows);
            if (spoolPath == null) return;
            await using var spool = new FileStream(spoolPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var buffer = new byte[1 << 16];
            for (var left = spoolLength; left > 0;)
            {
                var read = await spool.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read));
                left -= read;
            }
        });
    }

    /// <summary>Deletes the spool file.</summary>
    public void Dispose()
    {
        lock (_overflowLock)
        {
            _disposed = true;
            _overflowWriter?.Dispose();
            _overflowWriter = null;
            _overflow = null;
        }
    }

    /// <summary>Moves rows received since the last call into <see cref="Rows"/> and returns how many. Call from one thread only.</summary>
    public int Commit()
    {
        List<string?[]> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return 0;
            batch = _pending;
            _pending = new();
        }
        Rows.AddRange(batch);
        return batch.Count;
    }
}

public enum MessageKind { Info, Error, Success }

public sealed record QueryMessage(MessageKind Kind, string Text);

public sealed class QueryResult
{
    /// <summary>Only safe to enumerate once execution has finished; use <see cref="SnapshotResultSets"/> while it runs.</summary>
    public List<ResultSet> ResultSets { get; } = new();
    public List<QueryMessage> Messages { get; } = new();

    public IReadOnlyList<ResultSet> SnapshotResultSets()
    {
        lock (ResultSets) return ResultSets.ToList();
    }

    internal int AddResultSet(ResultSet set)
    {
        lock (ResultSets)
        {
            ResultSets.Add(set);
            return ResultSets.Count;
        }
    }

    public void AddMessage(QueryMessage message)
    {
        lock (Messages) Messages.Add(message);
    }
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
        return await ConnectionFactory.ListDatabasesAsync(_connection!, ct);
    }

    /// <summary>Loads objects, typed columns, routine parameters and foreign keys of the current database for IntelliSense.</summary>
    public async Task<SchemaInfo> LoadSchemaAsync(CancellationToken ct)
    {
        await OpenAsync(ct);
        const string sql = """
            SELECT s.name, o.name, o.type, c.name, t.name, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed, CAST(CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS bit)
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.columns c ON c.object_id = o.object_id
            LEFT JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN (SELECT ic.object_id, ic.column_id
                       FROM sys.indexes i
                       JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                       WHERE i.is_primary_key = 1) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE o.type IN ('U', 'V', 'P', 'PC', 'FN', 'FS', 'IF', 'TF', 'FT', 'SN', 'ET') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id;

            SELECT s.name, o.name, p.name, t.name, p.max_length, p.precision, p.scale, p.is_output, p.has_default_value, p.parameter_id
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = p.user_type_id
            WHERE o.is_ms_shipped = 0
            ORDER BY s.name, o.name, p.parameter_id;

            SELECT fk.name, fs.name, ft.name, fc.name, rs.name, rt.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns k ON k.constraint_object_id = fk.object_id
            JOIN sys.objects ft ON ft.object_id = fk.parent_object_id
            JOIN sys.schemas fs ON fs.schema_id = ft.schema_id
            JOIN sys.columns fc ON fc.object_id = k.parent_object_id AND fc.column_id = k.parent_column_id
            JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
            ORDER BY fk.object_id, k.constraint_column_id;
            """;
        await using var cmd = new SqlCommand(sql, _connection) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);

        var order = new List<(string, string)>();
        var kinds = new Dictionary<(string, string), SchemaObjectKind>();
        var columns = new Dictionary<(string, string), List<SchemaColumn>>();
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!kinds.ContainsKey(key))
            {
                order.Add(key);
                kinds[key] = SchemaInfo.KindFromType(r.GetString(2));
                columns[key] = new();
            }
            if (r.IsDBNull(3)) continue;
            var type = r.IsDBNull(4) ? "" : SchemaInfo.FormatType(r.GetString(4), r.GetInt16(5), r.GetByte(6), r.GetByte(7));
            columns[key].Add(new SchemaColumn(r.GetString(3), type, r.GetBoolean(8), r.GetBoolean(11), r.GetBoolean(9), r.GetBoolean(10)));
        }

        var parameters = new Dictionary<(string, string), List<SchemaParameter>>();
        var returnTypes = new Dictionary<(string, string), string>();
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetString(0), r.GetString(1));
            var type = SchemaInfo.FormatType(r.GetString(3), r.GetInt16(4), r.GetByte(5), r.GetByte(6));
            if (r.GetInt32(9) == 0)
            {
                returnTypes[key] = type;
                continue;
            }
            if (!parameters.TryGetValue(key, out var list)) parameters[key] = list = new();
            list.Add(new SchemaParameter(r.GetString(2), type, r.GetBoolean(7), r.GetBoolean(8)));
        }

        var foreignKeys = new List<ForeignKey>();
        await r.NextResultAsync(ct);
        string? current = null;
        List<string> fromCols = new(), toCols = new();
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (name != current || foreignKeys.Count == 0 || foreignKeys[^1].FromTable != r.GetString(2))
            {
                current = name;
                fromCols = new();
                toCols = new();
                foreignKeys.Add(new ForeignKey(name, r.GetString(1), r.GetString(2), fromCols, r.GetString(4), r.GetString(5), toCols));
            }
            fromCols.Add(r.GetString(3));
            toCols.Add(r.GetString(6));
        }

        var objects = order.Select(key => new SchemaObject(key.Item1, key.Item2, kinds[key], columns[key])
        {
            Parameters = parameters.TryGetValue(key, out var p) ? p : [],
            ReturnType = returnTypes.GetValueOrDefault(key),
        }).ToList();
        return new SchemaInfo(objects, foreignKeys);
    }

    public async Task<QueryResult> ExecuteAsync(string script, int lineOffset, CancellationToken ct)
    {
        var result = new QueryResult();
        await ExecuteAsync(result, script, lineOffset, ct);
        foreach (var set in result.ResultSets) set.Commit();
        return result;
    }

    /// <summary>
    /// Runs the script, streaming into <paramref name="result"/>: while it runs, poll
    /// <see cref="QueryResult.SnapshotResultSets"/> and <see cref="ResultSet.Commit"/> to show rows as they arrive.
    /// </summary>
    public async Task ExecuteAsync(QueryResult result, string script, int lineOffset, CancellationToken ct)
    {
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
            return;
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
                var number = result.AddResultSet(set);
                var values = new object[reader.FieldCount];
                try
                {
                    while (await reader.ReadAsync(ct))
                    {
                        reader.GetValues(values);
                        var row = new string?[values.Length];
                        for (var c = 0; c < values.Length; c++) row[c] = Format(values[c]);
                        set.Add(row);
                    }
                }
                finally
                {
                    set.MarkComplete();
                }
                if (set.Truncated)
                    AddMessage(result, new(MessageKind.Info, $"Result set {number} returned {set.TotalRows:N0} rows; only the first {MaxRowsPerResultSet:N0} are shown, but Save as CSV includes them all."));
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

