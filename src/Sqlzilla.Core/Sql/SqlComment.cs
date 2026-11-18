namespace Sqlzilla.Sql;

/// <summary>VS Code style Ctrl+/ line comments using <c>--</c>.</summary>
public static class SqlComment
{
    /// <summary>
    /// Uncomments when every non-blank line is already commented, otherwise comments every non-blank line,
    /// placing <c>-- </c> at the smallest indentation so the block stays aligned.
    /// </summary>
    public static string[] ToggleLines(IReadOnlyList<string> lines)
    {
        var content = lines.Where(l => l.Trim().Length > 0).ToList();
        if (content.Count == 0) return lines.ToArray();

        if (content.All(l => l.TrimStart().StartsWith("--", StringComparison.Ordinal)))
        {
            return lines.Select(l =>
            {
                var indent = l.Length - l.TrimStart().Length;
                if (l.Trim().Length == 0) return l;
                var marker = l.AsSpan(indent).StartsWith("-- ") ? 3 : 2;
                return l[..indent] + l[(indent + marker)..];
            }).ToArray();
        }

        var column = content.Min(l => l.Length - l.TrimStart().Length);
        return lines.Select(l => l.Trim().Length == 0 ? l : l[..column] + "-- " + l[column..]).ToArray();
    }
}
