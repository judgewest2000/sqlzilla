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

            if (!found.Contains(word)) found.Add(word);
        }
        return found;
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
