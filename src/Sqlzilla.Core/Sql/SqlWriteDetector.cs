namespace Sqlzilla.Sql;

/// <summary>Finds statements that change data, schema or permissions, so read-only groups can ask before running them.</summary>
public static class SqlWriteDetector
{
    private static readonly HashSet<string> WriteKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "BULK", "RESTORE", "BACKUP", "DBCC", "KILL", "SHUTDOWN", "RECONFIGURE", "WRITETEXT", "UPDATETEXT",
    };

    /// <summary>Distinct upper-cased write keywords in <paramref name="script"/>, in order of appearance (comments and strings ignored).</summary>
    public static List<string> Find(string script)
    {
        var tokens = SqlTokenizer.Tokenize(script).Where(t => t.Kind != SqlTokenKind.Comment).ToList();
        var found = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != SqlTokenKind.Keyword) continue;
            var word = t.Text(script).ToUpperInvariant();

            if (word == "INTO" && IsSelectInto(tokens, i, script)) word = "SELECT INTO";
            else if (!WriteKeywords.Contains(word)) continue;
            // "UPDATE(col)" in a trigger and "FOR UPDATE" on a cursor don't modify anything.
            else if (word == "UPDATE" && (Next(tokens, i, script) == "(" || Previous(tokens, i, script) == "FOR")) continue;

            if (TargetsOnlyTempObjects(tokens, i, word, script)) continue;
            // MERGE … WHEN MATCHED THEN UPDATE/DELETE/INSERT: the MERGE itself was already judged.
            if (word is "INSERT" or "UPDATE" or "DELETE" && Previous(tokens, i, script) == "THEN") continue;
            if (!found.Contains(word)) found.Add(word);
        }
        return found;
    }

    /// <summary>
    /// True when the statement at <paramref name="i"/> only touches #temp tables or @table variables, which live in
    /// tempdb / the session and are fine on read-only connections (CREATE TABLE #t, INSERT INTO #t, SELECT … INTO #t, DROP TABLE IF EXISTS #t …).
    /// </summary>
    private static bool TargetsOnlyTempObjects(List<SqlToken> tokens, int i, string word, string script)
    {
        string Word(int k) => k < tokens.Count ? tokens[k].Text(script).ToUpperInvariant() : "";
        bool IsTemp(int k, bool allowVariable) => k < tokens.Count &&
            (tokens[k].Kind == SqlTokenKind.TempTable || (allowVariable && tokens[k].Kind == SqlTokenKind.Variable));
        int Skip(int k, params string[] words) { while (words.Contains(Word(k))) k++; return k; }
        int SkipTop(int k)
        {
            if (Word(k) != "TOP") return k;
            k++;
            if (Word(k) == "(")
            {
                for (var depth = 0; k < tokens.Count; k++)
                {
                    if (Word(k) == "(") depth++;
                    else if (Word(k) == ")" && --depth == 0) { k++; break; }
                }
            }
            else k++;
            return Skip(k, "PERCENT");
        }
        // "name ON target" after INDEX.
        int IndexTarget(int k) => Word(k + 1) == "ON" ? k + 2 : tokens.Count;

        switch (word)
        {
            case "INSERT": return IsTemp(Skip(SkipTop(i + 1), "INTO"), true);
            case "UPDATE": return IsTemp(SkipTop(i + 1), true);
            case "DELETE": return IsTemp(Skip(SkipTop(i + 1), "FROM"), true);
            case "MERGE": return IsTemp(Skip(SkipTop(i + 1), "INTO"), true);
            case "SELECT INTO": return IsTemp(i + 1, false);
            case "TRUNCATE": return IsTemp(Skip(i + 1, "TABLE"), false);
            case "ALTER": return Word(i + 1) == "TABLE" && IsTemp(i + 2, false);
            case "CREATE":
            {
                if (Word(i + 1) == "TABLE") return IsTemp(i + 2, false);
                var k = Skip(i + 1, "UNIQUE", "CLUSTERED", "NONCLUSTERED");
                return Word(k) == "INDEX" && IsTemp(IndexTarget(k + 1), false);
            }
            case "DROP":
            {
                if (Word(i + 1) == "INDEX")
                {
                    var target = IndexTarget(Skip(i + 2, "IF", "EXISTS"));
                    return IsTemp(target, false) && Word(target + 1) != ",";
                }
                if (Word(i + 1) != "TABLE") return false;
                // DROP TABLE [IF EXISTS] #a, #b
                for (var k = Skip(i + 2, "IF", "EXISTS"); ; k += 2)
                {
                    if (!IsTemp(k, false)) return false;
                    if (Word(k + 1) != ",") return true;
                }
            }
            default: return false;
        }
    }

    /// <summary>SELECT … INTO creates a table; INSERT INTO is already caught by INSERT.</summary>
    private static bool IsSelectInto(List<SqlToken> tokens, int intoIndex, string script)
    {
        for (var j = intoIndex - 1; j >= 0; j--)
        {
            if (tokens[j].Kind != SqlTokenKind.Keyword) continue;
            var w = tokens[j].Text(script).ToUpperInvariant();
            if (w is "INSERT" or "MERGE") return false;
            if (w == "SELECT") return true;
            if (w is "FROM" or "WHERE" or "BEGIN" or "END" or "GO" or "UPDATE" or "DELETE") return false;
        }
        return false;
    }

    private static string Next(List<SqlToken> tokens, int i, string script) =>
        i + 1 < tokens.Count ? tokens[i + 1].Text(script).ToUpperInvariant() : "";

    private static string Previous(List<SqlToken> tokens, int i, string script) =>
        i > 0 ? tokens[i - 1].Text(script).ToUpperInvariant() : "";
}
