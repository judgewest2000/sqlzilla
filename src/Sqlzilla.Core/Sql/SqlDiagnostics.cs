using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Sqlzilla.Sql;

public enum DiagnosticSeverity { Error, Warning }

public sealed record SqlDiagnostic(int Start, int Length, string Message, DiagnosticSeverity Severity);

/// <summary>Syntax errors (via the SQL Server ScriptDom parser) and warnings for tables/procedures missing from the loaded schema.</summary>
public static class SqlDiagnostics
{
    private const int MaxErrors = 20;

    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase) { "sys", "INFORMATION_SCHEMA" };

    // Trigger pseudo-tables.
    private static readonly HashSet<string> PseudoTables = new(StringComparer.OrdinalIgnoreCase) { "inserted", "deleted" };

    public static IReadOnlyList<SqlDiagnostic> Analyze(string text, SchemaInfo schema)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment? fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(text)) fragment = parser.Parse(reader, out errors);

        var result = new List<SqlDiagnostic>();
        foreach (var e in errors.Take(MaxErrors))
        {
            var start = Math.Clamp(e.Offset, 0, text.Length);
            result.Add(new(start, WordLength(text, start), e.Message, DiagnosticSeverity.Error));
        }
        if (fragment != null && schema.Objects.Count > 0)
            result.AddRange(UnknownObjects(fragment, schema));
        return result;
    }

    private static int WordLength(string text, int start)
    {
        if (start >= text.Length) return 0;
        var end = start;
        if (text[end] == '[')
        {
            while (end < text.Length && text[end] != ']' && text[end] != '\n') end++;
            return Math.Min(end + 1, text.Length) - start;
        }
        while (end < text.Length && SqlTokenizer.IsIdentPart(text[end])) end++;
        return Math.Max(1, end - start);
    }

    private static IEnumerable<SqlDiagnostic> UnknownObjects(TSqlFragment fragment, SchemaInfo schema)
    {
        var visitor = new NameCollector();
        fragment.Accept(visitor);
        // USE switches database: the loaded schema no longer applies.
        if (visitor.ChangesDatabase) yield break;

        foreach (var (name, isProcedure) in visitor.References)
        {
            if (name.ServerIdentifier != null || name.DatabaseIdentifier != null || name.BaseIdentifier == null) continue;
            var baseName = name.BaseIdentifier.Value;
            var schemaName = name.SchemaIdentifier?.Value;
            if (baseName.StartsWith('#') || baseName.StartsWith('@')) continue;
            if (schemaName != null && SystemSchemas.Contains(schemaName)) continue;
            if (schemaName == null && (visitor.LocalNames.Contains(baseName) || PseudoTables.Contains(baseName))) continue;
            if (visitor.Created.Contains(Key(schemaName ?? "dbo", baseName))) continue;
            if (isProcedure && (baseName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))) continue;

            var found = schema.Objects.Any(o => o.Kind != SchemaObjectKind.Local
                                                && o.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                                                && (schemaName == null || o.Schema.Equals(schemaName, StringComparison.OrdinalIgnoreCase)));
            if (found) continue;
            var label = schemaName == null ? baseName : $"{schemaName}.{baseName}";
            yield return new SqlDiagnostic(name.StartOffset, name.FragmentLength,
                isProcedure ? $"Could not find stored procedure '{label}'." : $"Invalid object name '{label}'.", DiagnosticSeverity.Warning);
        }
    }

    private static string Key(string schema, string name) => $"{schema}.{name}".ToUpperInvariant();

    private sealed class NameCollector : TSqlFragmentVisitor
    {
        public List<(SchemaObjectName Name, bool IsProcedure)> References { get; } = new();
        public HashSet<string> LocalNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Created { get; } = new();
        public bool ChangesDatabase { get; private set; }

        public override void Visit(NamedTableReference node) => References.Add((node.SchemaObject, false));

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is { } name) References.Add((name, true));
        }

        public override void Visit(CommonTableExpression node) => LocalNames.Add(node.ExpressionName.Value);

        // "UPDATE c SET … FROM dbo.Customers c" names the alias as a table.
        public override void Visit(TableReferenceWithAlias node)
        {
            if (node.Alias != null) LocalNames.Add(node.Alias.Value);
        }

        public override void Visit(UseStatement node) => ChangesDatabase = true;

        public override void Visit(CreateTableStatement node) => AddCreated(node.SchemaObjectName);
        public override void Visit(CreateViewStatement node) => AddCreated(node.SchemaObjectName);
        public override void Visit(CreateProcedureStatement node) => AddCreated(node.ProcedureReference.Name);
        public override void Visit(CreateOrAlterProcedureStatement node) => AddCreated(node.ProcedureReference.Name);
        public override void Visit(CreateSynonymStatement node) => AddCreated(node.Name);

        public override void Visit(SelectStatement node)
        {
            if (node.Into != null) AddCreated(node.Into);
        }

        private void AddCreated(SchemaObjectName? name)
        {
            if (name?.BaseIdentifier != null) Created.Add(Key(name.SchemaIdentifier?.Value ?? "dbo", name.BaseIdentifier.Value));
        }
    }
}
