namespace Sqlzilla.Sql;

public enum SchemaObjectKind
{
    Table,
    View,
    Procedure,
    /// <summary>Scalar function.</summary>
    Function,
    TableFunction,
    /// <summary>Synonym: resolvable, but its columns are unknown.</summary>
    Synonym,
    /// <summary>Defined in the script itself: CTE, #temp table, @table variable or derived table.</summary>
    Local,
}

public sealed record SchemaColumn(string Name, string Type = "", bool IsNullable = true, bool IsPrimaryKey = false, bool IsIdentity = false, bool IsComputed = false)
{
    public static implicit operator SchemaColumn(string name) => new(name);

    /// <summary>e.g. "int PK identity NOT NULL".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Type.Length > 0) parts.Add(Type);
        if (IsPrimaryKey) parts.Add("PK");
        if (IsIdentity) parts.Add("identity");
        if (IsComputed) parts.Add("computed");
        if (Type.Length > 0) parts.Add(IsNullable ? "NULL" : "NOT NULL");
        return string.Join(' ', parts);
    }
}

public sealed record SchemaParameter(string Name, string Type, bool IsOutput = false, bool HasDefault = false)
{
    public override string ToString() => $"{Name} {Type}{(IsOutput ? " OUTPUT" : "")}{(HasDefault ? " = default" : "")}";
}

public sealed record SchemaObject(string Schema, string Name, SchemaObjectKind Kind, IReadOnlyList<SchemaColumn> Columns)
{
    public IReadOnlyList<SchemaParameter> Parameters { get; init; } = [];

    /// <summary>Return type of a scalar function.</summary>
    public string? ReturnType { get; init; }

    /// <summary>For <see cref="SchemaObjectKind.Local"/> objects: "CTE", "temp table", "table variable" or "derived table".</summary>
    public string? LocalKind { get; init; }

    public bool IsTableLike => Kind is SchemaObjectKind.Table or SchemaObjectKind.View or SchemaObjectKind.TableFunction
        or SchemaObjectKind.Synonym or SchemaObjectKind.Local;

    public string DisplayName => Schema.Length > 0 ? $"{Schema}.{Name}" : Name;

    public string KindLabel => Kind switch
    {
        SchemaObjectKind.Local => LocalKind ?? "local",
        SchemaObjectKind.TableFunction => "table-valued function",
        SchemaObjectKind.Function => "scalar function",
        SchemaObjectKind.Procedure => "stored procedure",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    public SchemaColumn? FindColumn(string name) =>
        Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record ForeignKey(
    string Name,
    string FromSchema, string FromTable, IReadOnlyList<string> FromColumns,
    string ToSchema, string ToTable, IReadOnlyList<string> ToColumns)
{
    public bool From(SchemaObject o) => Is(o, FromSchema, FromTable);
    public bool To(SchemaObject o) => Is(o, ToSchema, ToTable);

    private static bool Is(SchemaObject o, string schema, string table) =>
        o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && o.Name.Equals(table, StringComparison.OrdinalIgnoreCase);
}

public sealed class SchemaInfo(IReadOnlyList<SchemaObject> objects, IReadOnlyList<ForeignKey>? foreignKeys = null)
{
    public static readonly SchemaInfo Empty = new([]);

    public IReadOnlyList<SchemaObject> Objects { get; } = objects;
    public IReadOnlyList<ForeignKey> ForeignKeys { get; } = foreignKeys ?? [];

    public IEnumerable<string> Schemas => Objects.Where(o => o.Schema.Length > 0).Select(o => o.Schema).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same schema plus objects defined in the script (CTEs, temp tables…), which take precedence.</summary>
    public SchemaInfo WithLocals(IReadOnlyList<SchemaObject> locals) =>
        locals.Count == 0 ? this : new SchemaInfo([.. locals, .. Objects], ForeignKeys);

    /// <summary>Finds an object by "name" or "schema.name" (brackets allowed), preferring local objects, then dbo.</summary>
    public SchemaObject? Find(string? schema, string name)
    {
        schema = Unquote(schema);
        name = Unquote(name)!;
        var matches = Objects.Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                         && (schema == null || o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));
        return matches
            .OrderBy(o => o.Kind == SchemaObjectKind.Local ? 0 : o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .FirstOrDefault();
    }

    public static SchemaObjectKind KindFromType(string type) => type.Trim() switch
    {
        "U" or "ET" => SchemaObjectKind.Table,
        "V" => SchemaObjectKind.View,
        "P" or "PC" => SchemaObjectKind.Procedure,
        "IF" or "TF" or "FT" => SchemaObjectKind.TableFunction,
        "SN" => SchemaObjectKind.Synonym,
        _ => SchemaObjectKind.Function,
    };

    /// <summary>Formats a sys.types name with its length/precision, e.g. nvarchar(50), varchar(max), decimal(18,2).</summary>
    public static string FormatType(string type, int maxLength, int precision, int scale)
    {
        var t = type.ToLowerInvariant();
        return t switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{t}({(maxLength == -1 ? "max" : maxLength.ToString())})",
            "nvarchar" or "nchar" => $"{t}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
            "decimal" or "numeric" => $"{t}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" when scale != 7 => $"{t}({scale})",
            _ => type,
        };
    }

    public static string? Unquote(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length >= 2 && ((s[0] == '[' && s[^1] == ']') || (s[0] == '"' && s[^1] == '"'))) return s[1..^1].Replace("]]", "]");
        return s;
    }
}
