namespace Sqlzilla.Sql;

public enum CompletionKind { Keyword, Schema, Table, View, Procedure, Function, Column, Variable, Join, Local }

public sealed record CompletionItem(string Label, CompletionKind Kind, string InsertText, string? Detail = null);

/// <summary>Suggestions for the caret position; an accepted item replaces the text from <see cref="ReplaceStart"/> to the caret.</summary>
public sealed record CompletionResult(int ReplaceStart, string Prefix, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult Empty = new(0, "", Array.Empty<CompletionItem>());
}

/// <summary>
/// Context-aware T-SQL autocomplete: tables after FROM/JOIN (with foreign-key join suggestions), procedures after EXEC,
/// columns of the tables in the current statement, "alias." / "schema." members, CTEs, #temp tables, @table variables,
/// built-in functions, keywords and variables.
/// </summary>
public static class SqlCompletionProvider
{
    private const int MaxItems = 400;

    private enum Context { General, Table, Procedure }

    private static readonly HashSet<string> TableKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "UPDATE", "INTO", "APPLY", "USING", "MERGE", "TABLE" };

    public static CompletionResult Complete(string text, int caret, SchemaInfo schema, bool explicitRequest)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (SqlAnalysis.InsideCommentOrString(text, caret)) return CompletionResult.Empty;

        var start = caret;
        while (start > 0 && (SqlTokenizer.IsIdentPart(text[start - 1]) || text[start - 1] == '[')) start--;
        var prefix = text[start..caret];
        var prefixMatch = prefix.TrimStart('[');
        if (prefix.Length > 0 && prefix.All(char.IsDigit)) return CompletionResult.Empty;

        var tokens = SqlAnalysis.Code(text);
        var before = tokens.FindLastIndex(t => t.End <= start);
        var (stmtStart, stmtEnd) = SqlAnalysis.StatementRange(text, tokens, caret);
        var locals = SqlAnalysis.Locals(text, tokens, schema);
        var scope = schema.WithLocals(locals);
        var refs = SqlAnalysis.References(text, tokens, stmtStart, stmtEnd, scope);
        var context = ContextAt(text, tokens, before);

        if (start > 0 && text[start - 1] == '.')
        {
            var qualifier = ReadQualifier(text, start - 1, out var qualifierStart);
            if (qualifier == null) return CompletionResult.Empty;
            var outer = qualifierStart > 0 && text[qualifierStart - 1] == '.' ? ReadQualifier(text, qualifierStart - 1, out _) : null;
            var members = MemberItems(qualifier, outer, refs, scope, caret, context);
            return new(start, prefix, Filter(members, prefixMatch, context));
        }

        if (prefix.Length == 0 && !explicitRequest) return CompletionResult.Empty;
        if (AtAlias(text, tokens, before) && !explicitRequest) return CompletionResult.Empty;

        if (prefix.StartsWith('@'))
        {
            var items = new List<CompletionItem>();
            if (context == Context.Table)
                items.AddRange(LocalItems(locals.Where(l => l.Name.StartsWith('@'))));
            else if (SqlAnalysis.ExecCall(text, tokens, caret, scope) is { } exec)
                items.AddRange(exec.Proc.Parameters.Select(p => new CompletionItem(p.Name, CompletionKind.Variable, p.Name + " = ", p.Type + (p.IsOutput ? " OUTPUT" : ""))));
            items.AddRange(Variables(text, tokens, prefix));
            return new(start, prefix, Filter(items, prefixMatch, context));
        }

        if (prefix.StartsWith('#'))
            return new(start, prefix, Filter(LocalItems(locals.Where(l => l.Name.StartsWith('#'))), prefixMatch, context));

        var result = context switch
        {
            Context.Table => TableItems(text, tokens, before, refs, scope, locals, caret),
            Context.Procedure => ProcedureItems(scope),
            _ => GeneralItems(refs, scope, caret),
        };
        return new(start, prefix, Filter(result, prefixMatch, context));
    }

    private static Context ContextAt(string text, List<SqlToken> tokens, int before)
    {
        if (before < 0) return Context.General;
        var word = SqlAnalysis.KeywordAt(text, tokens, before);
        if (word is "EXEC" or "EXECUTE") return Context.Procedure;
        if (TableKeywords.Contains(word)) return Context.Table;
        if (SqlAnalysis.IsOp(text, tokens[before], ',') && SqlAnalysis.InFromClause(text, tokens, before)) return Context.Table;
        // FROM schema.| — the dotted chain started in a table position
        if (SqlAnalysis.IsOp(text, tokens[before], '.'))
        {
            var i = before;
            while (SqlAnalysis.IsOp(text, tokens, i, '.') && SqlAnalysis.IsObjectName(tokens, i - 1)) i -= 2;
            return ContextAt(text, tokens, i);
        }
        return Context.General;
    }

    /// <summary>True when the caret is where a table alias is typed ("FROM dbo.Customers |", "… AS |").</summary>
    private static bool AtAlias(string text, List<SqlToken> tokens, int before)
    {
        if (before < 0) return false;
        if (SqlAnalysis.IsKeyword(text, tokens, before, "AS")) return true;
        if (!SqlAnalysis.IsObjectName(tokens, before)) return false;
        var i = before;
        while (SqlAnalysis.IsOp(text, tokens, i - 1, '.') && SqlAnalysis.IsObjectName(tokens, i - 2)) i -= 2;
        if (SqlAnalysis.IsOp(text, tokens, i - 1, '.')) return false;
        var word = SqlAnalysis.KeywordAt(text, tokens, i - 1);
        return word is "FROM" or "JOIN" or "APPLY" or "UPDATE" or "USING" or "MERGE"
               || (SqlAnalysis.IsOp(text, tokens, i - 1, ',') && SqlAnalysis.InFromClause(text, tokens, i - 1));
    }

    private static IEnumerable<CompletionItem> GeneralItems(List<TableReference> refs, SchemaInfo scope, int caret)
    {
        // Columns of the nearest reference first when names repeat (self-joins, repeated aliases).
        foreach (var r in refs.OrderBy(r => Math.Abs(r.Position - caret)))
            foreach (var col in r.Object.Columns)
                yield return Column(col, r.Qualifier);
        foreach (var r in refs.Where(r => r.Alias != null))
            yield return new(r.Alias!, CompletionKind.Local, r.Alias!, $"alias · {r.Object.DisplayName}");
        foreach (var f in SqlBuiltins.All.Values)
            yield return new(f.Name, CompletionKind.Function, f.Name, f.Returns);
        foreach (var k in SqlKeywords.CompletionKeywords)
            yield return new(k, CompletionKind.Keyword, k);
        foreach (var obj in scope.Objects)
            yield return Object(obj);
        foreach (var s in scope.Schemas)
            yield return new(s, CompletionKind.Schema, Quote(s), "schema");
    }

    private static IEnumerable<CompletionItem> TableItems(string text, List<SqlToken> tokens, int before, List<TableReference> refs,
        SchemaInfo scope, List<SchemaObject> locals, int caret)
    {
        if (SqlAnalysis.IsKeyword(text, tokens, before, "JOIN") || SqlAnalysis.IsKeyword(text, tokens, before, "APPLY"))
            foreach (var join in JoinItems(refs.Where(r => r.Position < caret).ToList(), scope))
                yield return join;
        foreach (var item in LocalItems(locals.Where(l => !l.Name.StartsWith('@'))))
            yield return item;
        foreach (var obj in scope.Objects.Where(o => o.Kind != SchemaObjectKind.Local && o.IsTableLike))
            yield return Object(obj);
        foreach (var s in scope.Schemas)
            yield return new(s, CompletionKind.Schema, Quote(s), "schema");
    }

    private static IEnumerable<CompletionItem> ProcedureItems(SchemaInfo scope)
    {
        foreach (var obj in scope.Objects.Where(o => o.Kind == SchemaObjectKind.Procedure))
            yield return Object(obj);
        foreach (var s in scope.Schemas)
            yield return new(s, CompletionKind.Schema, Quote(s), "schema");
    }

    private static IEnumerable<CompletionItem> LocalItems(IEnumerable<SchemaObject> locals) =>
        locals.Where(l => l.LocalKind != "derived table")
            .Select(l => new CompletionItem(l.Name, CompletionKind.Local,
                l.Name.StartsWith('#') || l.Name.StartsWith('@') ? l.Name : Quote(l.Name), l.KindLabel));

    /// <summary>"Orders o ON o.CustomerId = c.CustomerId" for every foreign key between an already-referenced table and another.</summary>
    private static IEnumerable<CompletionItem> JoinItems(List<TableReference> existing, SchemaInfo scope)
    {
        var used = new HashSet<string>(existing.Select(r => r.Qualifier), StringComparer.OrdinalIgnoreCase);
        foreach (var r in existing)
        {
            foreach (var fk in scope.ForeignKeys)
            {
                if (fk.From(r.Object) && FindTable(scope, fk.ToSchema, fk.ToTable) is { } to)
                    yield return Join(to, fk.ToColumns, r, fk.FromColumns, fk.Name, used);
                if (fk.To(r.Object) && FindTable(scope, fk.FromSchema, fk.FromTable) is { } from)
                    yield return Join(from, fk.FromColumns, r, fk.ToColumns, fk.Name, used);
            }
        }
    }

    private static SchemaObject? FindTable(SchemaInfo scope, string schema, string name) =>
        scope.Objects.FirstOrDefault(o => o.Kind != SchemaObjectKind.Local
                                          && o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)
                                          && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static CompletionItem Join(SchemaObject target, IReadOnlyList<string> targetColumns, TableReference other,
        IReadOnlyList<string> otherColumns, string fkName, HashSet<string> used)
    {
        var alias = MakeAlias(target.Name, used);
        var on = string.Join(" AND ", targetColumns.Zip(otherColumns, (t, o) => $"{alias}.{Quote(t)} = {Quote(other.Qualifier)}.{Quote(o)}"));
        return new($"{target.Name} {alias} ON {on}", CompletionKind.Join, $"{QualifiedName(target)} {alias} ON {on}", fkName);
    }

    /// <summary>Lower-case initials of the words in a name ("OrderLines" → "ol"), made unique among <paramref name="used"/>.</summary>
    internal static string MakeAlias(string name, ISet<string> used)
    {
        var initials = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetter(c)) continue;
            var wordStart = i == 0 || !char.IsLetterOrDigit(name[i - 1]) || (char.IsUpper(c) && char.IsLower(name[i - 1]));
            if (wordStart) initials.Append(char.ToLowerInvariant(c));
        }
        var baseAlias = initials.Length > 0 ? initials.ToString() : "t";
        var alias = baseAlias;
        for (var n = 2; used.Contains(alias) || SqlKeywords.Keywords.Contains(alias); n++) alias = baseAlias + n;
        return alias;
    }

    private static IEnumerable<CompletionItem> MemberItems(string qualifier, string? outer, List<TableReference> refs, SchemaInfo scope, int caret, Context context)
    {
        var target = outer == null ? SqlAnalysis.ResolveQualifier(refs, qualifier, caret)?.Object : null;
        var isSchema = outer == null && scope.Schemas.Contains(qualifier, StringComparer.OrdinalIgnoreCase);
        if (target == null && !isSchema) target = scope.Find(outer, qualifier);
        if (target != null)
            return target.Columns.Select(c => Column(c, target.Name));

        return scope.Objects
            .Where(o => o.Schema.Equals(qualifier, StringComparison.OrdinalIgnoreCase))
            .Where(o => context switch
            {
                Context.Table => o.IsTableLike,
                Context.Procedure => o.Kind == SchemaObjectKind.Procedure,
                _ => true,
            })
            .Select(o => new CompletionItem(o.Name, KindOf(o), Quote(o.Name), o.KindLabel));
    }

    private static IEnumerable<CompletionItem> Variables(string text, List<SqlToken> tokens, string prefix) =>
        tokens
            .Where(t => t.Kind == SqlTokenKind.Variable)
            .Select(t => t.Text(text))
            .Where(v => !v.Equals(prefix, StringComparison.Ordinal) && !v.StartsWith("@@"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(v => new CompletionItem(v, CompletionKind.Variable, v, SqlAnalysis.VariableType(text, tokens, v) ?? "variable"));

    private static CompletionItem Column(SchemaColumn col, string table) =>
        new(col.Name, CompletionKind.Column, Quote(col.Name), col.Type.Length > 0 ? $"{col.Type} · {table}" : table);

    private static CompletionItem Object(SchemaObject obj) =>
        obj.Kind == SchemaObjectKind.Local
            ? new(obj.Name, CompletionKind.Local, obj.Name.StartsWith('#') || obj.Name.StartsWith('@') ? obj.Name : Quote(obj.Name), obj.KindLabel)
            : new(obj.Name, KindOf(obj), QualifiedName(obj), obj.Schema);

    /// <summary>dbo objects are inserted bare; other schemas keep their prefix, which SQL Server needs to resolve them.</summary>
    internal static string QualifiedName(SchemaObject obj) =>
        string.Equals(obj.Schema, "dbo", StringComparison.OrdinalIgnoreCase) || obj.Schema.Length == 0
            ? Quote(obj.Name)
            : $"{Quote(obj.Schema)}.{Quote(obj.Name)}";

    private static string? ReadQualifier(string text, int dot, out int start)
    {
        var end = dot;
        start = end;
        if (start > 0 && text[start - 1] == ']')
        {
            start--;
            while (start > 0 && text[start - 1] != '[') start--;
            if (start == 0) return null;
            start--;
            return text[(start + 1)..(end - 1)];
        }
        while (start > 0 && SqlTokenizer.IsIdentPart(text[start - 1])) start--;
        return start < end ? text[start..end] : null;
    }

    private static List<CompletionItem> Filter(IEnumerable<CompletionItem> items, string prefix, Context context) =>
        items
            .Where(i => prefix.Length == 0 || i.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(i => (i.Label.ToUpperInvariant(), i.Kind))
            .OrderBy(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => Rank(i.Kind, context))
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)
            .ToList();

    private static int Rank(CompletionKind kind, Context context) => (context, kind) switch
    {
        (_, CompletionKind.Join) => 0,
        (Context.Table, CompletionKind.Local or CompletionKind.Table or CompletionKind.View) => 1,
        (Context.Table, CompletionKind.Function) => 2,
        (_, CompletionKind.Column or CompletionKind.Variable) => 1,
        (_, CompletionKind.Local) => 2,
        (_, CompletionKind.Function) => 3,
        (_, CompletionKind.Keyword) => 4,
        (_, CompletionKind.Table or CompletionKind.View) => 5,
        (_, CompletionKind.Schema) => 6,
        _ => 7,
    };

    private static CompletionKind KindOf(SchemaObject o) => o.Kind switch
    {
        SchemaObjectKind.Table or SchemaObjectKind.Synonym => CompletionKind.Table,
        SchemaObjectKind.View => CompletionKind.View,
        SchemaObjectKind.Procedure => CompletionKind.Procedure,
        SchemaObjectKind.Local => CompletionKind.Local,
        _ => CompletionKind.Function,
    };

    /// <summary>Brackets names that aren't plain identifiers or are reserved words.</summary>
    public static string Quote(string name) =>
        name.Length > 0 && SqlTokenizer.IsIdentStart(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_')
        && !SqlKeywords.Keywords.Contains(name)
            ? name
            : $"[{name.Replace("]", "]]")}]";
}
