using System.Text;

namespace Sqlzilla.Sql;

/// <summary>Hover text for a word: <see cref="Start"/>/<see cref="Length"/> is the word's range.</summary>
public sealed record QuickInfo(string Title, string? Body, int Start, int Length);

/// <summary>What the docs panel should show: an object (optionally highlighting a column) or a built-in function.</summary>
public sealed record DocTarget(SchemaObject? Object, string? Column, BuiltinFunction? Builtin);

/// <summary>Describes the column, alias, table, function or variable under a position.</summary>
public static class SqlQuickInfo
{
    private const int MaxColumns = 30;

    public static QuickInfo? Get(string text, int offset, SchemaInfo schema)
    {
        var tokens = SqlAnalysis.Code(text);
        var i = tokens.FindIndex(t => t.Start <= offset && offset < t.End);
        if (i < 0) return null;
        var token = tokens[i];
        QuickInfo Info(string title, string? body) => new(title, body, token.Start, token.Length);

        var locals = SqlAnalysis.Locals(text, tokens, schema);
        var scope = schema.WithLocals(locals);
        var (stmtStart, stmtEnd) = SqlAnalysis.StatementRange(text, tokens, offset);
        var refs = SqlAnalysis.References(text, tokens, stmtStart, stmtEnd, scope);
        var word = token.Text(text);

        if (token.Kind == SqlTokenKind.Variable)
        {
            if (word.StartsWith("@@")) return null;
            if (scope.Find(null, word) is { Kind: SchemaObjectKind.Local } table) return Info($"{word} (table variable)", Describe(table, scope));
            var type = SqlAnalysis.VariableType(text, tokens, word);
            return Info(type == null ? word : $"{word} {type}", type == null ? "Variable is not declared in this script." : null);
        }
        if (token.Kind == SqlTokenKind.TempTable)
            return scope.Find(null, word) is { } temp ? Info($"{temp.KindLabel} {temp.Name}", Describe(temp, scope)) : null;
        if (token.Kind is not (SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Function or SqlTokenKind.Keyword))
            return null;

        var name = SqlAnalysis.Name(text, token);
        var isCall = SqlAnalysis.IsOp(text, tokens, i + 1, '(');
        var qualifier = SqlAnalysis.IsOp(text, tokens, i - 1, '.') && SqlAnalysis.IsObjectName(tokens, i - 2) ? SqlAnalysis.Name(text, tokens[i - 2]) : null;
        var isQualifier = SqlAnalysis.IsOp(text, tokens, i + 1, '.');

        if (token.Kind is SqlTokenKind.Function or SqlTokenKind.Keyword || (isCall && qualifier == null))
        {
            if (isCall && SqlBuiltins.Find(name) is { } builtin) return Info(builtin.Signature, builtin.Description);
            if (token.Kind == SqlTokenKind.Keyword) return null;
        }

        if (qualifier != null)
        {
            var outer = SqlAnalysis.IsOp(text, tokens, i - 3, '.') && SqlAnalysis.IsObjectName(tokens, i - 4) ? SqlAnalysis.Name(text, tokens[i - 4]) : null;
            var owner = outer == null ? SqlAnalysis.ResolveQualifier(refs, qualifier, token.Start)?.Object : scope.Find(outer, qualifier);
            if (owner?.FindColumn(name) is { } column) return Info($"{owner.Name}.{column.Name}", DescribeColumn(column, owner, scope));
            if (scope.Find(qualifier, name) is { } qualifiedObject) return Info($"{qualifiedObject.KindLabel} {qualifiedObject.DisplayName}", Describe(qualifiedObject, scope));
            return null;
        }

        if (refs.Where(r => string.Equals(r.Alias, name, StringComparison.OrdinalIgnoreCase)).MinBy(r => Math.Abs(r.Position - token.Start)) is { } aliased
            && (isQualifier || !aliased.Object.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return Info($"{name} → {aliased.Object.KindLabel} {aliased.Object.DisplayName}", Describe(aliased.Object, scope));

        if (isQualifier && scope.Schemas.Contains(name, StringComparer.OrdinalIgnoreCase) && scope.Find(null, name) == null)
            return Info($"schema {name}", $"{scope.Objects.Count(o => o.Schema.Equals(name, StringComparison.OrdinalIgnoreCase))} objects");

        if (scope.Find(null, name) is { } obj && (isQualifier || isCall || !refs.Any(r => r.Object.FindColumn(name) != null)))
            return Info($"{obj.KindLabel} {obj.DisplayName}", Describe(obj, scope));

        var owners = refs.Where(r => r.Object.FindColumn(name) != null).DistinctBy(r => r.Object).ToList();
        if (owners.Count == 1)
        {
            var column = owners[0].Object.FindColumn(name)!;
            return Info($"{owners[0].Object.Name}.{column.Name}", DescribeColumn(column, owners[0].Object, scope));
        }
        if (owners.Count > 1)
            return Info(name, "Ambiguous column, found in: " + string.Join(", ", owners.Select(o => o.Qualifier)));
        return null;
    }

    /// <summary>The object (and column) or built-in function at/just before a caret, for the docs panel.</summary>
    public static DocTarget? Target(string text, int caret, SchemaInfo schema)
    {
        var tokens = SqlAnalysis.Code(text);
        var i = tokens.FindIndex(t => t.Start <= caret && caret < t.End);
        var before = tokens.FindIndex(t => t.End == caret);
        if (i < 0 || (before >= 0 && tokens[i].Kind is SqlTokenKind.Operator)) i = before;
        if (i < 0) return null;
        var token = tokens[i];
        var scope = schema.WithLocals(SqlAnalysis.Locals(text, tokens, schema));
        var name = SqlAnalysis.Name(text, token);

        if (token.Kind is SqlTokenKind.Variable or SqlTokenKind.TempTable)
            return scope.Find(null, name) is { } local ? new DocTarget(local, null, null) : null;
        if (SqlAnalysis.IsOp(text, tokens, i + 1, '(') && SqlBuiltins.Find(name) is { } builtin && !SqlAnalysis.IsOp(text, tokens, i - 1, '.'))
            return new DocTarget(null, null, builtin);
        if (token.Kind is not (SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier)) return null;

        var (stmtStart, stmtEnd) = SqlAnalysis.StatementRange(text, tokens, caret);
        var refs = SqlAnalysis.References(text, tokens, stmtStart, stmtEnd, scope);
        if (SqlAnalysis.IsOp(text, tokens, i - 1, '.') && SqlAnalysis.IsObjectName(tokens, i - 2))
        {
            var qualifier = SqlAnalysis.Name(text, tokens[i - 2]);
            if (SqlAnalysis.ResolveQualifier(refs, qualifier, token.Start)?.Object is { } owner && owner.FindColumn(name) is { } column)
                return new DocTarget(owner, column.Name, null);
            return scope.Find(qualifier, name) is { } qualified ? new DocTarget(qualified, null, null) : null;
        }
        if (refs.Where(r => string.Equals(r.Alias, name, StringComparison.OrdinalIgnoreCase)).MinBy(r => Math.Abs(r.Position - token.Start)) is { } aliased)
            return new DocTarget(aliased.Object, null, null);
        var columnOwner = refs.FirstOrDefault(r => r.Object.FindColumn(name) != null);
        if (scope.Find(null, name) is { } obj && (columnOwner == null || SqlAnalysis.IsOp(text, tokens, i + 1, '.')))
            return new DocTarget(obj, null, null);
        return columnOwner != null ? new DocTarget(columnOwner.Object, columnOwner.Object.FindColumn(name)!.Name, null) : null;
    }

    public static string DescribeColumn(SchemaColumn column, SchemaObject owner, SchemaInfo schema)
    {
        var sb = new StringBuilder(column.Describe());
        foreach (var fk in schema.ForeignKeys.Where(f => f.From(owner)))
        {
            var at = fk.FromColumns.ToList().FindIndex(c => c.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) sb.Append($"\n→ {fk.ToSchema}.{fk.ToTable}.{fk.ToColumns[at]} ({fk.Name})");
        }
        return sb.ToString();
    }

    /// <summary>Columns, parameters and foreign keys of an object, one per line.</summary>
    public static string Describe(SchemaObject obj, SchemaInfo schema)
    {
        var sb = new StringBuilder();
        if (obj.ReturnType != null) sb.AppendLine($"Returns {obj.ReturnType}");
        foreach (var p in obj.Parameters) sb.AppendLine(p.ToString());
        if (obj.Parameters.Count > 0 && obj.Columns.Count > 0) sb.AppendLine();
        foreach (var c in obj.Columns.Take(MaxColumns))
            sb.AppendLine(c.Type.Length > 0 ? $"{c.Name}  {c.Describe()}" : c.Name);
        if (obj.Columns.Count > MaxColumns) sb.AppendLine($"… {obj.Columns.Count - MaxColumns} more columns");
        var outgoing = schema.ForeignKeys.Where(f => f.From(obj)).ToList();
        var incoming = schema.ForeignKeys.Where(f => f.To(obj)).ToList();
        if (outgoing.Count + incoming.Count > 0) sb.AppendLine();
        foreach (var fk in outgoing) sb.AppendLine($"→ {fk.ToSchema}.{fk.ToTable} ({string.Join(", ", fk.FromColumns)})");
        foreach (var fk in incoming) sb.AppendLine($"← {fk.FromSchema}.{fk.FromTable} ({string.Join(", ", fk.FromColumns)})");
        if (obj.Kind == SchemaObjectKind.Synonym && sb.Length == 0) sb.Append("Synonym");
        return sb.ToString().TrimEnd();
    }
}
