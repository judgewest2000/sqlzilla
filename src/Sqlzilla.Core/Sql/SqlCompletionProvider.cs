namespace Sqlzilla.Sql;

public enum CompletionKind { Keyword, Schema, Table, View, Procedure, Function, Column, Variable }

public sealed record CompletionItem(string Label, CompletionKind Kind, string InsertText, string? Detail = null);

/// <summary>Suggestions for the caret position; an accepted item replaces the text from <see cref="ReplaceStart"/> to the caret.</summary>
public sealed record CompletionResult(int ReplaceStart, string Prefix, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult Empty = new(0, "", Array.Empty<CompletionItem>());
}

/// <summary>
/// T-SQL autocomplete: keywords, schema objects, columns of the tables referenced in the statement,
/// and "alias." / "schema." member completion.
/// </summary>
public static class SqlCompletionProvider
{
    private const int MaxItems = 400;

    public static CompletionResult Complete(string text, int caret, SchemaInfo schema, bool explicitRequest)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (InsideCommentOrString(text, caret)) return CompletionResult.Empty;

        var start = caret;
        while (start > 0 && (SqlTokenizer.IsIdentPart(text[start - 1]) || text[start - 1] == '[')) start--;
        var prefix = text[start..caret];
        var prefixMatch = prefix.TrimStart('[');

        if (start > 0 && text[start - 1] == '.')
        {
            var qualifier = ReadQualifier(text, start - 1);
            return qualifier == null ? CompletionResult.Empty : new(start, prefix, Filter(MemberItems(text, qualifier, schema), prefixMatch));
        }

        if (prefix.Length == 0 && !explicitRequest) return CompletionResult.Empty;
        if (prefix.StartsWith('@')) return new(start, prefix, Filter(Variables(text, prefix), prefixMatch));
        if (prefix.All(char.IsDigit) && prefix.Length > 0) return CompletionResult.Empty;

        var items = new List<CompletionItem>();
        var referenced = ReferencedObjects(text, schema);
        foreach (var (obj, alias) in referenced)
            foreach (var col in obj.Columns)
                items.Add(new(col, CompletionKind.Column, Quote(col), alias ?? obj.Name));
        foreach (var obj in schema.Objects)
            items.Add(new(obj.Name, KindOf(obj), $"{Quote(obj.Schema)}.{Quote(obj.Name)}", obj.Schema));
        foreach (var s in schema.Schemas)
            items.Add(new(s, CompletionKind.Schema, Quote(s), "schema"));
        foreach (var k in SqlKeywords.CompletionKeywords)
            items.Add(new(k, CompletionKind.Keyword, k));

        return new(start, prefix, Filter(items, prefixMatch));
    }

    private static IEnumerable<CompletionItem> MemberItems(string text, string qualifier, SchemaInfo schema)
    {
        var referenced = ReferencedObjects(text, schema);
        var target = referenced.FirstOrDefault(a => string.Equals(a.Alias, qualifier, StringComparison.OrdinalIgnoreCase)).Object
                     ?? referenced.FirstOrDefault(a => a.Object.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase)).Object;
        var isSchema = schema.Schemas.Contains(qualifier, StringComparer.OrdinalIgnoreCase);
        if (target == null && !isSchema) target = schema.Find(null, qualifier);
        if (target != null)
            return target.Columns.Select(c => new CompletionItem(c, CompletionKind.Column, Quote(c), target.Name));

        return schema.Objects
            .Where(o => o.Schema.Equals(qualifier, StringComparison.OrdinalIgnoreCase))
            .Select(o => new CompletionItem(o.Name, KindOf(o), Quote(o.Name), o.Kind.ToString().ToLowerInvariant()));
    }

    private static IEnumerable<CompletionItem> Variables(string text, string prefix) =>
        SqlTokenizer.Tokenize(text)
            .Where(t => t.Kind == SqlTokenKind.Variable)
            .Select(t => t.Text(text))
            .Where(v => !v.Equals(prefix, StringComparison.Ordinal) && !v.StartsWith("@@"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(v => new CompletionItem(v, CompletionKind.Variable, v, "variable"));

    /// <summary>Objects named after FROM/JOIN/UPDATE/INTO/APPLY, with their aliases.</summary>
    internal static List<(SchemaObject Object, string? Alias)> ReferencedObjects(string text, SchemaInfo schema)
    {
        var tokens = SqlTokenizer.Tokenize(text).Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        var result = new List<(SchemaObject, string?)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var word = tokens[i].Kind == SqlTokenKind.Keyword ? tokens[i].Text(text).ToUpperInvariant() : "";
            var isList = tokens[i].Kind == SqlTokenKind.Operator && text[tokens[i].Start] == ',' && InFromClause(tokens, i, text);
            if (word is not ("FROM" or "JOIN" or "UPDATE" or "INTO" or "APPLY") && !isList) continue;

            var j = i + 1;
            if (!IsName(tokens, j)) continue;
            string? schemaName = null;
            var name = tokens[j].Text(text);
            j++;
            while (j + 1 < tokens.Count && text[tokens[j].Start] == '.' && tokens[j].Length == 1 && IsName(tokens, j + 1))
            {
                schemaName = name;
                name = tokens[j + 1].Text(text);
                j += 2;
            }
            if (schema.Find(schemaName, name) is not { } obj) continue;

            string? alias = null;
            if (j < tokens.Count && tokens[j].Kind == SqlTokenKind.Keyword && tokens[j].Text(text).Equals("AS", StringComparison.OrdinalIgnoreCase)) j++;
            if (IsName(tokens, j)) alias = SchemaInfo.Unquote(tokens[j].Text(text));
            result.Add((obj, alias));
        }
        return result;
    }

    private static bool InFromClause(List<SqlToken> tokens, int i, string text)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (tokens[j].Kind != SqlTokenKind.Keyword) continue;
            var w = tokens[j].Text(text).ToUpperInvariant();
            if (w is "FROM" or "JOIN") return true;
            if (w is "SELECT" or "WHERE" or "ON" or "SET" or "BY" or "VALUES" or "GROUP" or "ORDER" or "HAVING") return false;
        }
        return false;
    }

    private static bool IsName(List<SqlToken> tokens, int i) =>
        i < tokens.Count && tokens[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier;

    private static string? ReadQualifier(string text, int dot)
    {
        var end = dot;
        var start = end;
        if (start > 0 && text[start - 1] == ']')
        {
            start--;
            while (start > 0 && text[start - 1] != '[') start--;
            if (start == 0) return null;
            return text[start..(end - 1)];
        }
        while (start > 0 && SqlTokenizer.IsIdentPart(text[start - 1])) start--;
        return start < end ? text[start..end] : null;
    }

    private static bool InsideCommentOrString(string text, int caret)
    {
        foreach (var t in SqlTokenizer.Tokenize(text))
        {
            if (t.Start >= caret) break;
            if (t.Kind is SqlTokenKind.Comment or SqlTokenKind.String && caret > t.Start && (caret < t.End || !IsClosed(text, t)))
                return true;
        }
        return false;
    }

    private static bool IsClosed(string text, SqlToken t)
    {
        var s = t.Text(text);
        return t.Kind == SqlTokenKind.String
            ? s.Length >= 2 && s.EndsWith('\'') && !(s.Length == 2 && s[0] is 'N' or 'n')
            : !s.StartsWith("--") && s.EndsWith("*/");
    }

    private static List<CompletionItem> Filter(IEnumerable<CompletionItem> items, string prefix) =>
        items
            .Where(i => prefix.Length == 0 || i.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(i => (i.Label.ToUpperInvariant(), i.Kind, i.Detail))
            .OrderBy(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => Rank(i.Kind))
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems)
            .ToList();

    private static int Rank(CompletionKind kind) => kind switch
    {
        CompletionKind.Column or CompletionKind.Variable => 0,
        CompletionKind.Table or CompletionKind.View => 1,
        CompletionKind.Schema => 2,
        CompletionKind.Procedure or CompletionKind.Function => 3,
        _ => 4,
    };

    private static CompletionKind KindOf(SchemaObject o) => o.Kind switch
    {
        SchemaObjectKind.Table => CompletionKind.Table,
        SchemaObjectKind.View => CompletionKind.View,
        SchemaObjectKind.Procedure => CompletionKind.Procedure,
        _ => CompletionKind.Function,
    };

    /// <summary>Brackets names that aren't plain identifiers or are reserved words.</summary>
    public static string Quote(string name) =>
        name.Length > 0 && SqlTokenizer.IsIdentStart(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_')
        && !SqlKeywords.Keywords.Contains(name)
            ? name
            : $"[{name.Replace("]", "]]")}]";
}
