using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDataReader;

namespace Sqlzilla.Import;

public enum ImportFormat { Delimited, Json, Excel }

/// <summary>A file's rows as text (null = empty/NULL), with unique column names.</summary>
public sealed class ImportData(IReadOnlyList<string> columns, List<string?[]> rows)
{
    public IReadOnlyList<string> Columns { get; } = columns;
    public List<string?[]> Rows { get; } = rows;
}

/// <summary>Reads CSV/delimited text, JSON and Excel files into <see cref="ImportData"/>.</summary>
public static class ImportReaders
{
    public static readonly char[] Delimiters = [',', '\t', ';', '|'];

    static ImportReaders() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // for legacy .xls

    public static ImportFormat FormatFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" or ".ndjson" or ".jsonl" => ImportFormat.Json,
        ".xlsx" or ".xlsm" or ".xlsb" or ".xls" => ImportFormat.Excel,
        _ => ImportFormat.Delimited,
    };

    /// <summary>
    /// Sniffs the start of a file and returns why it can't be imported as <paramref name="format"/>, or null if it looks fine.
    /// Excel files must carry a zip (xlsx) or OLE (xls) signature; text formats must not look binary.
    /// </summary>
    public static string? CheckFile(string path, ImportFormat format)
    {
        var head = new byte[8192];
        int n;
        using (var stream = File.OpenRead(path)) n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var bytes = head.AsSpan(0, n);
        if (n == 0) return "The file is empty.";

        bool zip = bytes.StartsWith("PK\x03\x04"u8);
        bool ole = bytes.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 });
        if (format == ImportFormat.Excel)
            return zip || ole ? null : "This doesn't look like an Excel workbook.";

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return null; // UTF-16 text
        if (zip || ole) return "This looks like an Excel or other packaged file, not text. Excel files need an .xlsx or .xls extension.";
        int control = 0;
        foreach (var b in bytes)
        {
            if (b == 0) return "This looks like a binary file (an image, document or program), not CSV, text or JSON.";
            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C) control++;
        }
        return control * 20 > n ? "This looks like a binary file (an image, document or program), not CSV, text or JSON." : null;
    }

    // ------------------------------------------------------------------ delimited

    /// <summary>Picks the candidate delimiter that appears most (outside quotes) in the first line.</summary>
    public static char DetectDelimiter(string text)
    {
        var counts = new int[Delimiters.Length];
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (inQuotes) continue;
            else if (c is '\r' or '\n') break;
            else if (Array.IndexOf(Delimiters, c) is var i and >= 0) counts[i]++;
        }
        var best = 0;
        for (var i = 1; i < counts.Length; i++)
            if (counts[i] > counts[best]) best = i;
        return Delimiters[best];
    }

    /// <summary>RFC 4180: quoted fields may contain the delimiter, newlines and "" for a quote. Empty unquoted fields are NULL.</summary>
    public static ImportData ReadDelimited(string text, char delimiter, bool hasHeader)
    {
        var records = new List<string?[]>();
        var fields = new List<string?>();
        var field = new StringBuilder();
        var quoted = false;     // the current field started with a quote
        var inQuotes = false;

        void EndField()
        {
            fields.Add(field.Length == 0 && !quoted ? null : field.ToString());
            field.Clear();
            quoted = false;
        }
        void EndRecord()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0] == null)) records.Add(fields.ToArray());   // skip blank lines
            fields.Clear();
        }

        for (var i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (c == '"' && field.Length == 0 && !quoted) inQuotes = quoted = true;
            else if (c == delimiter) EndField();
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRecord();
            }
            else if (c == '\n') EndRecord();
            else field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0 || quoted) EndRecord();
        return FromRecords(records, hasHeader);
    }

    // ------------------------------------------------------------------ JSON

    /// <summary>
    /// An array of objects (or the first array-of-objects property of a root object, or one object, or JSON Lines).
    /// Columns are the union of property names; nested objects/arrays are kept as JSON text.
    /// </summary>
    public static ImportData ReadJson(string text)
    {
        List<JsonElement> items;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            items = Items(doc.RootElement.Clone());
        }
        catch (JsonException) when (text.TrimStart().StartsWith('{'))
        {
            items = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        }

        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string name)
        {
            if (index.TryAdd(name, names.Count)) names.Add(name);
        }
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object) Add("Value");
            else foreach (var p in item.EnumerateObject()) Add(p.Name);
        }

        var rows = new List<string?[]>(items.Count);
        foreach (var item in items)
        {
            var row = new string?[names.Count];
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var p in item.EnumerateObject()) row[index[p.Name]] = JsonText(p.Value);
            else row[index["Value"]] = JsonText(item);
            rows.Add(row);
        }
        return new ImportData(UniqueNames(names), rows);
    }

    private static List<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToList();
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object))
                    return p.Value.EnumerateArray().ToList();
        return [root];
    }

    private static string? JsonText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    // ------------------------------------------------------------------ Excel

    public static List<string> ExcelSheets(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var names = new List<string>();
        do names.Add(reader.Name); while (reader.NextResult());
        return names;
    }

    public static ImportData ReadExcel(string path, int sheet, bool hasHeader)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        for (var i = 0; i < sheet; i++)
            if (!reader.NextResult()) throw new InvalidOperationException("Sheet not found.");
        var records = new List<string?[]>();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var c = 0; c < row.Length; c++) row[c] = ExcelText(reader.GetValue(c));
            records.Add(row);
        }
        // Formatting often extends the used range past the data.
        while (records.Count > 0 && records[^1].All(v => v == null)) records.RemoveAt(records.Count - 1);
        var width = records.Count == 0 ? 0 : records.Max(r => Array.FindLastIndex(r, v => v != null) + 1);
        return FromRecords(records.Select(r => r.Take(width).ToArray()).ToList(), hasHeader);
    }

    private static string? ExcelText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s.Length == 0 ? null : s,
        DateTime d when d.TimeOfDay == TimeSpan.Zero => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // ------------------------------------------------------------------ shared

    private static ImportData FromRecords(List<string?[]> records, bool hasHeader)
    {
        var width = records.Count == 0 ? 0 : records.Max(r => r.Length);
        IEnumerable<string?> header = hasHeader && records.Count > 0 ? records[0] : [];
        var rows = (hasHeader ? records.Skip(1) : records)
            .Select(r => r.Length == width ? r : [.. r, .. new string?[width - r.Length]])
            .ToList();
        return new ImportData(UniqueNames(header.Concat(Enumerable.Repeat<string?>(null, width)).Take(width)), rows);
    }

    /// <summary>Blank names become Column1…, duplicates get _2, _3…</summary>
    public static List<string> UniqueNames(IEnumerable<string?> names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var name = string.IsNullOrWhiteSpace(raw) ? $"Column{result.Count + 1}" : raw.Trim();
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{name}_{n}";
            result.Add(unique);
        }
        return result;
    }
}
