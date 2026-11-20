using System.Data;
using Microsoft.Data.SqlClient;

namespace Sqlzilla.Import;

/// <summary>One destination column fed from source column <see cref="SourceIndex"/>.</summary>
public sealed record ImportColumn(int SourceIndex, string Name, string SqlType, bool Nullable = true, bool IsIdentity = false);

public sealed record ImportPlan(string Schema, string Table, bool CreateTable, IReadOnlyList<ImportColumn> Columns)
{
    public string QualifiedName => $"{Quote(Schema)}.{Quote(Table)}";

    public static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

    public string CreateTableSql() =>
        $"CREATE TABLE {QualifiedName}\n(\n"
        + string.Join(",\n", Columns.Select(c => $"    {Quote(c.Name)} {c.SqlType} {(c.Nullable ? "NULL" : "NOT NULL")}"))
        + "\n);";
}

public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Creates the table (optionally) and bulk-copies the rows, all in one transaction so a failure leaves nothing behind.</summary>
public static class DataImporter
{
    private const int BatchSize = 5_000;

    public static async Task<int> ImportAsync(string connectionString, ImportPlan plan, IReadOnlyList<string?[]> rows,
        IProgress<int>? progress, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        if (plan.CreateTable)
        {
            await using var create = new SqlCommand(plan.CreateTableSql(), connection, transaction);
            await create.ExecuteNonQueryAsync(ct);
        }

        var options = SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.CheckConstraints;
        if (plan.Columns.Any(c => c.IsIdentity)) options |= SqlBulkCopyOptions.KeepIdentity;
        using var bulk = new SqlBulkCopy(connection, options, transaction)
        {
            DestinationTableName = plan.QualifiedName,
            BulkCopyTimeout = 0,
            BatchSize = BatchSize,
        };
        var table = new DataTable();
        foreach (var c in plan.Columns)
        {
            table.Columns.Add(c.Name, ImportTypes.ClrType(c.SqlType));
            bulk.ColumnMappings.Add(c.Name, c.Name);
        }

        var done = 0;
        for (var start = 0; start < rows.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            table.Clear();
            var end = Math.Min(rows.Count, start + BatchSize);
            for (var r = start; r < end; r++)
            {
                var values = new object[plan.Columns.Count];
                for (var c = 0; c < values.Length; c++)
                {
                    var column = plan.Columns[c];
                    var source = rows[r];
                    var text = column.SourceIndex < source.Length ? source[column.SourceIndex] : null;
                    try { values[c] = ImportTypes.Convert(text, column.SqlType); }
                    catch (FormatException ex) { throw new ImportException($"Row {r + 1}, column {column.Name}: {ex.Message}", ex); }
                }
                table.Rows.Add(values);
            }
            try { await bulk.WriteToServerAsync(table, ct); }
            catch (SqlException ex) { throw new ImportException($"Rows {start + 1}–{end}: {ex.Message}", ex); }
            catch (InvalidOperationException ex) { throw new ImportException($"Rows {start + 1}–{end}: {ex.Message}", ex); }
            done = end;
            progress?.Report(done);
        }

        await transaction.CommitAsync(ct);
        return done;
    }
}
