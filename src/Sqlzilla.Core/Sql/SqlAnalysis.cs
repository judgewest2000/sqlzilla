namespace Sqlzilla.Sql;

/// <summary>A table-like object named in a statement (FROM/JOIN/UPDATE…) with its alias; <see cref="Position"/> is the name's offset.</summary>
public sealed record TableReference(SchemaObject Object, string? Alias, int Position)
{
    public string Qualifier => Alias ?? Object.Name;
}

/// <summary>Token-level heuristics shared by completion, signature help and quick info. Tolerant of half-typed SQL.</summary>
internal static class SqlAnalysis
{
    private static readonly HashSet<string> TableKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "JOIN", "UPDATE", "INTO", "APPLY", "USING", "MERGE" };

    private static readonly HashSet<string> SelectListEnd = new(StringComparer.OrdinalIgnoreCase)
        { "FROM", "INTO", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "FOR" };

    public static List<SqlToken> Code(string text) => SqlTokenizer.Tokenize(text).Where(t => t.Kind != SqlTokenKind.Comment).ToList();

    public static bool IsOp(string text, SqlToken t, char c) => t.Kind == SqlTokenKind.Operator && text[t.Start] == c;

    public static bool IsOp(string text, List<SqlToken> tokens, int i, char c) => i >= 0 && i < tokens.Count && IsOp(text, tokens[i], c);

    public static bool IsKeyword(string text, List<SqlToken> tokens, int i, string word) =>
        i >= 0 && i < tokens.Count && tokens[i].Kind == SqlTokenKind.Keyword && tokens[i].Text(text).Equals(word, StringComparison.OrdinalIgnoreCase);

    public static string KeywordAt(string text, List<SqlToken> tokens, int i) =>
        i >= 0 && i < tokens.Count && tokens[i].Kind == SqlTokenKind.Keyword ? tokens[i].Text(text).ToUpperInvariant() : "";

    /// <summary>Plain or bracketed identifier (usable as an alias).</summary>
    public static bool IsIdent(List<SqlToken> tokens, int i) =>
        i >= 0 && i < tokens.Count && tokens[i].Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier;

    /// <summary>Anything that can name a table: identifiers, #temp tables and @table variables.</summary>
    public static bool IsObjectName(List<SqlToken> tokens, int i) =>
        IsIdent(tokens, i) || (i >= 0 && i < tokens.Count && tokens[i].Kind is SqlTokenKind.TempTable or SqlTokenKind.Variable);

    public static string Name(string text, SqlToken t) => SchemaInfo.Unquote(t.Text(text)) ?? "";

    public static bool InsideCommentOrString(string text, int caret)
    {
        foreach (var t in SqlTokenizer.Tokenize(text))
        {
            if (t.Start >= caret) break;
            if (t.Kind is SqlTokenKind.Comment or SqlTokenKind.String && caret > t.Start && (caret < t.End || !IsClosed(text, t)))
            {
                // A line comment ends at the newline, so the caret is only inside it up to the end of that line.
                if (text.AsSpan(t.Start).StartsWith("--") && caret > t.End) continue;
                return true;
            }
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

    /// <summary>GO on a line of its own.</summary>
    public static bool IsGo(string text, SqlToken t)
    {
        if (t.Length != 2 || !t.Text(text).Equals("GO", StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = t.Start - 1; i >= 0 && text[i] != '\n'; i--)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    /// <summary>Text range of the statement around the caret, bounded by ';' and GO.</summary>
    public static (int Start, int End) StatementRange(string text, List<SqlToken> tokens, int caret)
    {
        int start = 0, end = text.Length;
        foreach (var t in tokens)
        {
            if (!IsOp(text, t, ';') && !IsGo(text, t)) continue;
            if (t.End <= caret) start = t.End;
            else if (t.Start >= caret)
            {
                end = t.Start;
                break;
            }
        }
        return (start, end);
    }

    /// <summary>Text range of the GO batch around the caret.</summary>
    public static (int Start, int End) BatchRange(string text, List<SqlToken> tokens, int caret)
    {
        int start = 0, end = text.Length;
        foreach (var t in tokens)
        {
            if (!IsGo(text, t)) continue;
            if (t.End <= caret) start = t.End;
            else if (t.Start >= caret)
            {
                end = t.Start;
                break;
            }
        }
        return (start, end);
    }

    public static int MatchingClose(string text, List<SqlToken> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (IsOp(text, tokens[i], '(')) depth++;
            else if (IsOp(text, tokens[i], ')') && --depth == 0) return i;
        }
        return tokens.Count;
    }

    /// <summary>Reads "a.b.c" starting at token i; returns the last two parts and the index after the name.</summary>
    public static (string? Schema, string Name, int Next) ReadDottedName(string text, List<SqlToken> tokens, int i)
    {
        string? schema = null;
        var name = Name(text, tokens[i]);
        var j = i + 1;
        while (IsOp(text, tokens, j, '.') && IsObjectName(tokens, j + 1))
        {
            schema = name;
            name = Name(text, tokens[j + 1]);
            j += 2;
        }
        return (schema, name, j);
    }

    /// <summary>Objects named after FROM/JOIN/UPDATE/INTO/APPLY (or a comma in a FROM list) inside [start, end), with aliases.</summary>
    public static List<TableReference> References(string text, List<SqlToken> tokens, int start, int end, SchemaInfo schema)
    {
        var result = new List<TableReference>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Start < start) continue;
            if (tokens[i].Start >= end) break;
            var trigger = TableKeywords.Contains(KeywordAt(text, tokens, i)) || (IsOp(text, tokens[i], ',') && InFromClause(text, tokens, i));
            if (!trigger) continue;

            var j = i + 1;
            SchemaObject? obj;
            int position;
            if (IsOp(text, tokens, j, '('))
            {
                var close = MatchingClose(text, tokens, j);
                if (!IsKeyword(text, tokens, j + 1, "SELECT") || close >= tokens.Count) continue;
                position = tokens[j].Start;
                var columns = SelectColumns(text, tokens, j + 1, close, schema);
                j = close + 1;
                if (IsKeyword(text, tokens, j, "AS")) j++;
                if (!IsIdent(tokens, j)) continue;
                var alias = Name(text, tokens[j]);
                result.Add(new(new SchemaObject("", alias, SchemaObjectKind.Local, columns) { LocalKind = "derived table" }, alias, position));
                continue;
            }
            if (!IsObjectName(tokens, j)) continue;
            position = tokens[j].Start;
            var (schemaName, name, next) = ReadDottedName(text, tokens, j);
            j = next;
            if (IsOp(text, tokens, j, '(')) j = MatchingClose(text, tokens, j) + 1;   // table-valued function arguments
            obj = schema.Find(schemaName, name);
            if (obj == null) continue;

            string? aliasName = null;
            if (IsKeyword(text, tokens, j, "AS")) j++;
            if (IsIdent(tokens, j)) aliasName = Name(text, tokens[j]);
            result.Add(new(obj, aliasName, position));
        }
        return result;
    }

    public static bool InFromClause(string text, List<SqlToken> tokens, int i)
    {
        var depth = 0;
        for (var j = i - 1; j >= 0; j--)
        {
            if (IsOp(text, tokens[j], ')')) depth++;
            else if (IsOp(text, tokens[j], '('))
            {
                if (depth == 0) return false;
                depth--;
            }
            if (depth > 0 || tokens[j].Kind != SqlTokenKind.Keyword) continue;
            var w = tokens[j].Text(text).ToUpperInvariant();
            if (w is "FROM" or "JOIN" or "APPLY") return true;
            if (w is "SELECT" or "WHERE" or "ON" or "SET" or "BY" or "VALUES" or "GROUP" or "ORDER" or "HAVING" or "DECLARE") return false;
        }
        return false;
    }

    /// <summary>Picks the reference whose alias (or name) matches, preferring the one nearest the caret.</summary>
    public static TableReference? ResolveQualifier(List<TableReference> refs, string qualifier, int caret) =>
        refs.Where(r => string.Equals(r.Alias, qualifier, StringComparison.OrdinalIgnoreCase)).MinBy(r => Math.Abs(r.Position - caret))
        ?? refs.Where(r => r.Object.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase)).MinBy(r => Math.Abs(r.Position - caret));

    /// <summary>
    /// Objects the script defines: CTEs, CREATE TABLE (incl. #temp), DECLARE @t TABLE and SELECT … INTO #t.
    /// Later definitions can refer to earlier ones.
    /// </summary>
    public static List<SchemaObject> Locals(string text, List<SqlToken> tokens, SchemaInfo schema)
    {
        var locals = new List<SchemaObject>();
        SchemaInfo Scope() => schema.WithLocals(locals);
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (KeywordAt(text, tokens, i))
            {
                case "WITH":
                    ReadCtes(text, tokens, i + 1, Scope(), locals);
                    break;
                case "TABLE" when IsKeyword(text, tokens, i - 1, "CREATE") && IsObjectName(tokens, i + 1):
                {
                    var (schemaName, name, next) = ReadDottedName(text, tokens, i + 1);
                    if (!IsOp(text, tokens, next, '(')) break;
                    var kind = name.StartsWith('#') ? "temp table" : "table (created in script)";
                    locals.Add(new SchemaObject(name.StartsWith('#') ? "" : schemaName ?? "dbo", name, SchemaObjectKind.Local,
                        ColumnDefinitions(text, tokens, next)) { LocalKind = kind });
                    break;
                }
                case "DECLARE":
                {
                    var j = i + 1;
                    if (j >= tokens.Count || tokens[j].Kind != SqlTokenKind.Variable) break;
                    var k = j + 1;
                    if (IsKeyword(text, tokens, k, "AS")) k++;
                    if (!IsKeyword(text, tokens, k, "TABLE") || !IsOp(text, tokens, k + 1, '(')) break;
                    locals.Add(new SchemaObject("", tokens[j].Text(text), SchemaObjectKind.Local,
                        ColumnDefinitions(text, tokens, k + 1)) { LocalKind = "table variable" });
                    break;
                }
                case "INTO" when i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.TempTable:
                {
                    var select = PreviousSelect(text, tokens, i);
                    if (select < 0) break;
                    var end = i;
                    while (end < tokens.Count && !IsOp(text, tokens[end], ';') && !IsGo(text, tokens[end])) end++;
                    var name = tokens[i + 1].Text(text);
                    if (locals.Any(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) break;
                    locals.Add(new SchemaObject("", name, SchemaObjectKind.Local, SelectColumns(text, tokens, select, end, Scope()))
                        { LocalKind = "temp table" });
                    break;
                }
            }
        }
        return locals;
    }

    private static int PreviousSelect(string text, List<SqlToken> tokens, int i)
    {
        var depth = 0;
        for (var j = i - 1; j >= 0; j--)
        {
            if (IsOp(text, tokens[j], ')')) depth++;
            else if (IsOp(text, tokens[j], '(')) depth--;
            else if (IsOp(text, tokens[j], ';')) return -1;
            if (depth == 0 && IsKeyword(text, tokens, j, "SELECT")) return j;
            if (depth < 0) return -1;
        }
        return -1;
    }

    /// <summary>WITH name [(cols)] AS (SELECT …) [, name2 …]</summary>
    private static void ReadCtes(string text, List<SqlToken> tokens, int i, SchemaInfo schema, List<SchemaObject> locals)
    {
        while (IsIdent(tokens, i))
        {
            var name = Name(text, tokens[i]);
            var j = i + 1;
            List<string>? explicitColumns = null;
            if (IsOp(text, tokens, j, '('))
            {
                var close = MatchingClose(text, tokens, j);
                explicitColumns = tokens.Skip(j + 1).Take(close - j - 1)
                    .Where(t => t.Kind is SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier).Select(t => Name(text, t)).ToList();
                j = close + 1;
            }
            if (!IsKeyword(text, tokens, j, "AS") || !IsOp(text, tokens, j + 1, '(')) return;
            var bodyClose = MatchingClose(text, tokens, j + 1);
            var scope = schema.WithLocals(locals);
            IReadOnlyList<SchemaColumn> columns = explicitColumns?.Select(c => (SchemaColumn)c).ToList()
                ?? (IsKeyword(text, tokens, j + 2, "SELECT") ? SelectColumns(text, tokens, j + 2, bodyClose, scope) : []);
            locals.RemoveAll(l => l.LocalKind == "CTE" && l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            locals.Add(new SchemaObject("", name, SchemaObjectKind.Local, columns) { LocalKind = "CTE" });
            if (!IsOp(text, tokens, bodyClose + 1, ',')) return;
            i = bodyClose + 2;
        }
    }

    /// <summary>Column definitions of CREATE TABLE / DECLARE … TABLE, starting at the '(' token.</summary>
    private static List<SchemaColumn> ColumnDefinitions(string text, List<SqlToken> tokens, int open)
    {
        var close = MatchingClose(text, tokens, open);
        var columns = new List<SchemaColumn>();
        foreach (var (a, b) in SplitTopLevel(text, tokens, open + 1, close))
        {
            if (b <= a || !IsIdent(tokens, a)) continue;
            var words = Enumerable.Range(a, b - a).Select(k => tokens[k].Text(text).ToUpperInvariant()).ToList();
            var type = "";
            var computed = IsKeyword(text, tokens, a + 1, "AS");
            if (!computed && a + 1 < b)
            {
                var typeEnd = a + 2;
                while (IsOp(text, tokens, typeEnd, '.') && typeEnd + 1 < b) typeEnd += 2;
                if (IsOp(text, tokens, typeEnd, '(')) typeEnd = Math.Min(MatchingClose(text, tokens, typeEnd) + 1, b);
                type = text[tokens[a + 1].Start..tokens[typeEnd - 1].End];
                if (tokens[a + 1].Kind == SqlTokenKind.Type) type = type.ToLowerInvariant();
            }
            var notNull = words.Zip(words.Skip(1)).Any(p => p is ("NOT", "NULL"));
            var pk = words.Zip(words.Skip(1)).Any(p => p is ("PRIMARY", "KEY"));
            columns.Add(new SchemaColumn(Name(text, tokens[a]), type, !notNull && !pk, pk, words.Contains("IDENTITY"), computed));
        }
        return columns;
    }

    /// <summary>Token index ranges [a, b) separated by top-level commas in [start, end).</summary>
    public static List<(int A, int B)> SplitTopLevel(string text, List<SqlToken> tokens, int start, int end)
    {
        var parts = new List<(int, int)>();
        var depth = 0;
        var a = start;
        for (var i = start; i < end && i < tokens.Count; i++)
        {
            if (IsOp(text, tokens[i], '(')) depth++;
            else if (IsOp(text, tokens[i], ')')) depth--;
            else if (depth == 0 && IsOp(text, tokens[i], ','))
            {
                parts.Add((a, i));
                a = i + 1;
            }
        }
        parts.Add((a, Math.Min(end, tokens.Count)));
        return parts;
    }

    /// <summary>Output columns of the SELECT at token <paramref name="select"/> (body ends before <paramref name="end"/>), expanding *.</summary>
    public static List<SchemaColumn> SelectColumns(string text, List<SqlToken> tokens, int select, int end, SchemaInfo schema)
    {
        end = Math.Min(end, tokens.Count);
        var i = select + 1;
        if (IsKeyword(text, tokens, i, "DISTINCT") || IsKeyword(text, tokens, i, "ALL")) i++;
        if (IsKeyword(text, tokens, i, "TOP"))
        {
            i++;
            i = IsOp(text, tokens, i, '(') ? MatchingClose(text, tokens, i) + 1 : i + 1;
            if (IsKeyword(text, tokens, i, "PERCENT")) i++;
            if (IsKeyword(text, tokens, i, "WITH")) i += 2;
        }

        var listEnd = i;
        var depth = 0;
        for (; listEnd < end; listEnd++)
        {
            if (IsOp(text, tokens[listEnd], '(')) depth++;
            else if (IsOp(text, tokens[listEnd], ')')) depth--;
            else if (depth == 0 && SelectListEnd.Contains(KeywordAt(text, tokens, listEnd))) break;
            if (depth < 0) break;
        }

        var refs = References(text, tokens, tokens[Math.Min(listEnd, tokens.Count - 1)].Start, end < tokens.Count ? tokens[end].Start : text.Length, schema);
        var columns = new List<SchemaColumn>();
        foreach (var (a, b) in SplitTopLevel(text, tokens, i, listEnd))
        {
            var n = b - a;
            if (n <= 0) continue;
            if (n == 1 && IsOp(text, tokens[a], '*'))
            {
                columns.AddRange(refs.SelectMany(r => r.Object.Columns));
                continue;
            }
            if (n == 3 && IsOp(text, tokens[a + 2], '*') && IsOp(text, tokens[a + 1], '.'))
            {
                if (ResolveQualifier(refs, Name(text, tokens[a]), tokens[a].Start) is { } r) columns.AddRange(r.Object.Columns);
                continue;
            }
            if (n >= 3 && IsIdent(tokens, a) && IsOp(text, tokens[a + 1], '=') && !IsOp(text, tokens, a + 2, '='))
            {
                columns.Add(Typed(Name(text, tokens[a]), ColumnFor(text, tokens, a + 2, b, refs)));
                continue;
            }
            var last = b - 1;
            if (n >= 2 && IsIdent(tokens, last) && !IsOp(text, tokens, last - 1, '.'))
            {
                var exprEnd = IsKeyword(text, tokens, last - 1, "AS") ? last - 1 : last;
                columns.Add(Typed(Name(text, tokens[last]), ColumnFor(text, tokens, a, exprEnd, refs)));
                continue;
            }
            if (IsIdent(tokens, last) && (n == 1 || IsOp(text, tokens[last - 1], '.')))
                columns.Add(ColumnFor(text, tokens, a, b, refs) ?? new SchemaColumn(Name(text, tokens[last])));
        }
        return columns.DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SchemaColumn Typed(string name, SchemaColumn? source) =>
        source == null ? new SchemaColumn(name) : source with { Name = name, IsPrimaryKey = false };

    /// <summary>Resolves a "col" or "q.col" expression to its source column.</summary>
    private static SchemaColumn? ColumnFor(string text, List<SqlToken> tokens, int a, int b, List<TableReference> refs)
    {
        if (b - a == 1 && IsIdent(tokens, a))
        {
            var name = Name(text, tokens[a]);
            return refs.Select(r => r.Object.FindColumn(name)).FirstOrDefault(c => c != null);
        }
        if (b - a == 3 && IsIdent(tokens, a) && IsOp(text, tokens[a + 1], '.') && IsIdent(tokens, a + 2))
            return ResolveQualifier(refs, Name(text, tokens[a]), tokens[a].Start)?.Object.FindColumn(Name(text, tokens[a + 2]));
        return null;
    }

    /// <summary>EXEC [@ret =] proc args… around the caret: the procedure and the index of the argument being typed (by position or @name).</summary>
    public static (SchemaObject Proc, int Argument, string? NamedParameter)? ExecCall(string text, List<SqlToken> tokens, int caret, SchemaInfo schema)
    {
        var (stmtStart, _) = StatementRange(text, tokens, caret);
        var exec = -1;
        for (var i = 0; i < tokens.Count && tokens[i].Start < caret; i++)
            if (tokens[i].Start >= stmtStart && KeywordAt(text, tokens, i) is "EXEC" or "EXECUTE") exec = i;
        if (exec < 0) return null;
        var j = exec + 1;
        if (j < tokens.Count && tokens[j].Kind == SqlTokenKind.Variable && IsOp(text, tokens, j + 1, '=')) j += 2;
        if (!IsIdent(tokens, j)) return null;
        var (schemaName, name, next) = ReadDottedName(text, tokens, j);
        if (tokens[next - 1].End >= caret) return null;
        if (schema.Find(schemaName, name) is not { Kind: SchemaObjectKind.Procedure } proc) return null;

        var argument = 0;
        var segment = next;
        for (var k = next; k < tokens.Count && tokens[k].Start < caret; k++)
        {
            if (KeywordAt(text, tokens, k) is "SELECT" or "EXEC" or "EXECUTE" or "INSERT" or "UPDATE" or "DELETE" or "IF" or "SET" or "DECLARE") return null;
            if (!IsOp(text, tokens[k], ',')) continue;
            argument++;
            segment = k + 1;
        }
        string? named = null;
        if (segment < tokens.Count && tokens[segment].Kind == SqlTokenKind.Variable && tokens[segment].Start < caret && IsOp(text, tokens, segment + 1, '='))
            named = tokens[segment].Text(text);
        return (proc, argument, named);
    }

    /// <summary>The declared type of a variable/parameter ("@x int", "@p nvarchar(50) OUTPUT"…), found from its first typed occurrence.</summary>
    public static string? VariableType(string text, List<SqlToken> tokens, string variable)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != SqlTokenKind.Variable || !tokens[i].Text(text).Equals(variable, StringComparison.OrdinalIgnoreCase)) continue;
            var j = i + 1;
            if (IsKeyword(text, tokens, j, "AS")) j++;
            if (j >= tokens.Count) return null;
            if (IsKeyword(text, tokens, j, "TABLE")) return "TABLE";
            if (tokens[j].Kind is not (SqlTokenKind.Type or SqlTokenKind.Identifier)) continue;
            var end = j + 1;
            while (IsOp(text, tokens, end, '.') && IsIdent(tokens, end + 1)) end += 2;
            if (IsOp(text, tokens, end, '(')) end = Math.Min(MatchingClose(text, tokens, end) + 1, tokens.Count);
            var type = text[tokens[j].Start..tokens[end - 1].End];
            return tokens[j].Kind == SqlTokenKind.Type ? type.ToLowerInvariant() : type;
        }
        return null;
    }
}
