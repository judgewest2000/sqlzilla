namespace Sqlzilla.Sql;

public enum SchemaObjectKind { Table, View, Procedure, Function }

public sealed record SchemaObject(string Schema, string Name, SchemaObjectKind Kind, IReadOnlyList<string> Columns);

public sealed class SchemaInfo(IReadOnlyList<SchemaObject> objects)
{
    public static readonly SchemaInfo Empty = new([]);

    public IReadOnlyList<SchemaObject> Objects { get; } = objects;

    public IEnumerable<string> Schemas => Objects.Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds an object by "name" or "schema.name" (brackets allowed), preferring dbo for unqualified names.</summary>
    public SchemaObject? Find(string? schema, string name)
    {
        schema = Unquote(schema);
        name = Unquote(name)!;
        var matches = Objects.Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                         && (schema == null || o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));
        return matches.OrderBy(o => o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
    }

    public static SchemaObjectKind KindFromType(string type) => type switch
    {
        "U" => SchemaObjectKind.Table,
        "V" => SchemaObjectKind.View,
        "P" => SchemaObjectKind.Procedure,
        _ => SchemaObjectKind.Function,
    };

    public static string? Unquote(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length >= 2 && ((s[0] == '[' && s[^1] == ']') || (s[0] == '"' && s[^1] == '"'))) return s[1..^1];
        return s;
    }
}
