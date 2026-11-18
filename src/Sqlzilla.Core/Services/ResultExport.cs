using System.Text;

namespace Sqlzilla.Services;

public static class ResultExport
{
    /// <summary>Tab-separated text for pasting into Excel etc. NULLs are written as "NULL", like SSMS.</summary>
    public static string ToTsv(IReadOnlyList<ResultColumn> columns, IEnumerable<string?[]> rows, bool includeHeaders)
    {
        var sb = new StringBuilder();
        if (includeHeaders) sb.AppendJoin('\t', columns.Select(c => Flatten(c.Name))).Append("\r\n");
        foreach (var row in rows) sb.AppendJoin('\t', row.Select(v => Flatten(v ?? "NULL"))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>RFC 4180 CSV; NULLs become empty fields.</summary>
    public static string ToCsv(IReadOnlyList<ResultColumn> columns, IEnumerable<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendJoin(',', columns.Select(c => CsvField(c.Name))).Append("\r\n");
        foreach (var row in rows) sb.AppendJoin(',', row.Select(v => CsvField(v ?? ""))).Append("\r\n");
        return sb.ToString();
    }

    private static string Flatten(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private static string CsvField(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
