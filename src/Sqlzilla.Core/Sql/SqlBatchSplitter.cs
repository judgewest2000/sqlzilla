using System.Text.RegularExpressions;

namespace Sqlzilla.Sql;

/// <param name="Text">Batch text.</param>
/// <param name="StartLine">Zero-based line of the batch's first line in the script (for mapping server error line numbers).</param>
/// <param name="RepeatCount">From "GO n".</param>
public sealed record SqlBatch(string Text, int StartLine, int RepeatCount = 1);

/// <summary>Splits a script into batches on "GO" lines, as SSMS and sqlcmd do, ignoring GO inside comments and strings.</summary>
public static partial class SqlBatchSplitter
{
    [GeneratedRegex(@"^\s*GO(?:\s+(?<count>\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    public static List<SqlBatch> Split(string script)
    {
        script = script.Replace("\r\n", "\n").Replace('\r', '\n');
        var batches = new List<SqlBatch>();
        var tokens = SqlTokenizer.Tokenize(script);
        var tokenIndex = 0;

        var batchStart = 0;
        var batchStartLine = 0;
        var line = 0;
        var lineStart = 0;
        while (lineStart <= script.Length)
        {
            var lineEnd = script.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = script.Length;

            // A GO line only counts when it isn't inside a multi-line comment or string.
            while (tokenIndex < tokens.Count && tokens[tokenIndex].End <= lineStart) tokenIndex++;
            var insideToken = tokenIndex < tokens.Count && tokens[tokenIndex].Start < lineStart;

            var match = insideToken ? null : GoLine().Match(script, lineStart, lineEnd - lineStart);
            if (match is { Success: true })
            {
                var count = match.Groups["count"].Success && int.TryParse(match.Groups["count"].Value, out var c) ? Math.Max(1, c) : 1;
                Add(batches, script[batchStart..lineStart], batchStartLine, count);
                batchStart = Math.Min(lineEnd + 1, script.Length);
                batchStartLine = line + 1;
            }

            if (lineEnd >= script.Length) break;
            lineStart = lineEnd + 1;
            line++;
        }
        Add(batches, script[batchStart..], batchStartLine, 1);
        return batches;
    }

    private static void Add(List<SqlBatch> batches, string text, int startLine, int count)
    {
        if (SqlTokenizer.Tokenize(text).All(t => t.Kind == SqlTokenKind.Comment)) return;
        batches.Add(new SqlBatch(text.TrimEnd('\n', ' ', '\t'), startLine, count));
    }
}
