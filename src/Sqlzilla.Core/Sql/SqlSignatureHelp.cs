namespace Sqlzilla.Sql;

public sealed record SignatureInfo(string Name, IReadOnlyList<string> Parameters, int ActiveParameter, string? Returns, string? Description);

/// <summary>Parameter hints for the call around the caret: built-in/user functions "fn(a, |" and "EXEC proc @a, |".</summary>
public static class SqlSignatureHelp
{
    public static SignatureInfo? Get(string text, int caret, SchemaInfo schema)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (SqlAnalysis.InsideCommentOrString(text, caret)) return null;
        var tokens = SqlAnalysis.Code(text);
        var (stmtStart, _) = SqlAnalysis.StatementRange(text, tokens, caret);
        var scope = schema.WithLocals(SqlAnalysis.Locals(text, tokens, schema));

        var last = tokens.FindLastIndex(t => t.End <= caret && t.Start < caret);
        var depth = 0;
        var commas = 0;
        for (var i = last; i >= 0 && tokens[i].Start >= stmtStart; i--)
        {
            if (SqlAnalysis.IsOp(text, tokens[i], ')'))
            {
                depth++;
                continue;
            }
            if (SqlAnalysis.IsOp(text, tokens[i], ','))
            {
                if (depth == 0) commas++;
                continue;
            }
            if (!SqlAnalysis.IsOp(text, tokens[i], '(')) continue;
            if (depth > 0)
            {
                depth--;
                continue;
            }
            if (Function(text, tokens, i, scope, commas) is { } signature) return signature;
            commas = 0;   // a plain parenthesis, e.g. IN (…): keep looking for an enclosing call
        }

        if (SqlAnalysis.ExecCall(text, tokens, caret, scope) is not { } exec) return null;
        var parameters = exec.Proc.Parameters;
        var active = exec.NamedParameter is { } named
            ? parameters.ToList().FindIndex(p => p.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
            : exec.Argument;
        return new SignatureInfo(exec.Proc.DisplayName, parameters.Select(p => p.ToString()).ToList(), active, null, exec.Proc.KindLabel);
    }

    private static SignatureInfo? Function(string text, List<SqlToken> tokens, int open, SchemaInfo scope, int commas)
    {
        var n = open - 1;
        if (n < 0 || tokens[n].Kind is not (SqlTokenKind.Identifier or SqlTokenKind.QuotedIdentifier or SqlTokenKind.Function or SqlTokenKind.Keyword))
            return null;
        var name = SqlAnalysis.Name(text, tokens[n]);
        var qualified = SqlAnalysis.IsOp(text, tokens, n - 1, '.') && SqlAnalysis.IsIdent(tokens, n - 2);
        if (!qualified && SqlBuiltins.Find(name) is { } builtin)
        {
            var active = builtin.Parameters.Count == 0 ? -1 : Math.Min(commas, builtin.Parameters.Count - 1);
            return new SignatureInfo(builtin.Name, builtin.Parameters, active, builtin.Returns, builtin.Description);
        }
        var schemaName = qualified ? SqlAnalysis.Name(text, tokens[n - 2]) : null;
        if (scope.Find(schemaName, name) is not { Kind: SchemaObjectKind.Function or SchemaObjectKind.TableFunction } fn) return null;
        return new SignatureInfo(fn.DisplayName, fn.Parameters.Select(p => p.ToString()).ToList(),
            fn.Parameters.Count == 0 ? -1 : Math.Min(commas, fn.Parameters.Count - 1), fn.ReturnType ?? "table", fn.KindLabel);
    }
}
