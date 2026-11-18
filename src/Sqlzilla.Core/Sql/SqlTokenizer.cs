namespace Sqlzilla.Sql;

public enum SqlTokenKind
{
    Default = 0,
    Comment = 1,
    String = 2,
    Number = 3,
    Keyword = 4,
    Type = 5,
    Function = 6,
    Variable = 7,
    Identifier = 8,
    QuotedIdentifier = 9,
    Operator = 10,
    TempTable = 11,
}

public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
    public string Text(string source) => source.Substring(Start, Length);
}

/// <summary>Tolerant T-SQL tokenizer (unterminated strings/comments run to the end) used for highlighting, GO splitting and analysis.</summary>
public static class SqlTokenizer
{
    public static List<SqlToken> Tokenize(string text)
    {
        var tokens = new List<SqlToken>();
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            var start = i;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '-' && Peek(text, i + 1) == '-')
            {
                while (i < n && text[i] != '\n') i++;
                tokens.Add(new(SqlTokenKind.Comment, start, i - start));
                continue;
            }
            if (c == '/' && Peek(text, i + 1) == '*')
            {
                i = SkipBlockComment(text, i);
                tokens.Add(new(SqlTokenKind.Comment, start, i - start));
                continue;
            }
            if (c == '\'' || ((c == 'N' || c == 'n') && Peek(text, i + 1) == '\''))
            {
                if (c != '\'') i++;
                i = SkipQuoted(text, i, '\'');
                tokens.Add(new(SqlTokenKind.String, start, i - start));
                continue;
            }
            if (c == '[')
            {
                i = SkipQuoted(text, i, ']');
                tokens.Add(new(SqlTokenKind.QuotedIdentifier, start, i - start));
                continue;
            }
            if (c == '"')
            {
                i = SkipQuoted(text, i, '"');
                tokens.Add(new(SqlTokenKind.QuotedIdentifier, start, i - start));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(text, i + 1))))
            {
                if (c == '0' && (Peek(text, i + 1) is 'x' or 'X'))
                {
                    i += 2;
                    while (i < n && Uri.IsHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < n && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    if (i < n && (text[i] is 'e' or 'E') && (char.IsDigit(Peek(text, i + 1)) || (Peek(text, i + 1) is '+' or '-' && char.IsDigit(Peek(text, i + 2)))))
                    {
                        i += 2;
                        while (i < n && char.IsDigit(text[i])) i++;
                    }
                }
                tokens.Add(new(SqlTokenKind.Number, start, i - start));
                continue;
            }
            if (c == '@' || c == '#' || IsIdentStart(c))
            {
                i++;
                while (i < n && IsIdentPart(text[i])) i++;
                var word = text.AsSpan(start, i - start);
                var kind = c switch
                {
                    '@' => SqlTokenKind.Variable,
                    '#' => SqlTokenKind.TempTable,
                    _ => Classify(word, text, i),
                };
                tokens.Add(new(kind, start, i - start));
                continue;
            }
            i++;
            tokens.Add(new(SqlTokenKind.Operator, start, 1));
        }
        return tokens;
    }

    private static SqlTokenKind Classify(ReadOnlySpan<char> word, string text, int end)
    {
        var w = word.ToString();
        if (SqlKeywords.Keywords.Contains(w)) return SqlTokenKind.Keyword;
        if (SqlKeywords.Types.Contains(w)) return SqlTokenKind.Type;
        if (SqlKeywords.Functions.Contains(w) && NextNonSpace(text, end) == '(') return SqlTokenKind.Function;
        return SqlTokenKind.Identifier;
    }

    private static char NextNonSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return Peek(text, i);
    }

    private static char Peek(string text, int i) => i < text.Length ? text[i] : '\0';

    internal static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    internal static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>T-SQL block comments nest.</summary>
    private static int SkipBlockComment(string text, int i)
    {
        var depth = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && Peek(text, i + 1) == '*') { depth++; i += 2; }
            else if (text[i] == '*' && Peek(text, i + 1) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0) return i;
            }
            else i++;
        }
        return i;
    }

    /// <summary>Skips a quoted run starting at the opening char; a doubled closing char is an escape.</summary>
    private static int SkipQuoted(string text, int i, char close)
    {
        i++;
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (Peek(text, i + 1) == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return i;
    }
}
