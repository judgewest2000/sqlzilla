using System.Text;

namespace Sqlzilla.Services;

public static class ResultExport
{
    /// <summary>Tab-separated text for pasting into Excel etc. NULLs are written as "NULL", like SSMS.</summary>
    public static string ToTsv(IReadOnlyList<ResultColumn> columns, IEnumerable<string?[]> rows, bool includeHeaders)
    {
        // No trailing newline, so pasting a single cell gives just its value (as SSMS does).
        var lines = rows.Select(row => string.Join('\t', row.Select(v => Flatten(v ?? "NULL"))));
        if (includeHeaders) lines = lines.Prepend(string.Join('\t', columns.Select(c => Flatten(c.Name))));
        return string.Join("\r\n", lines);
    }

    /// <summary>RFC 4180 CSV; NULLs become empty fields.</summary>
    public static string ToCsv(IReadOnlyList<ResultColumn> columns, IEnumerable<string?[]> rows)
    {
        var writer = new StringWriter();
        WriteCsv(writer, columns, rows);
        return writer.ToString();
    }

    public static void WriteCsv(TextWriter writer, IReadOnlyList<ResultColumn> columns, IEnumerable<string?[]> rows)
    {
        WriteCsvRow(writer, columns.Select(c => c.Name).ToArray());
        foreach (var row in rows) WriteCsvRow(writer, row);
    }

    public static void WriteCsvRow(TextWriter writer, string?[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(CsvField(row[i] ?? ""));
        }
        writer.Write("\r\n");
    }

    private static string Flatten(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private static string CsvField(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
