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
        {
            var names = new NameCollector();
            fragment.Accept(names);
            // USE switches database: the loaded schema no longer applies.
            if (!names.ChangesDatabase)
            {
                result.AddRange(UnknownObjects(names, schema));
                var columns = new ColumnChecker(schema, names.CteNames);
                fragment.Accept(columns);
                result.AddRange(columns.Diagnostics);
            }
        }
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

    private static IEnumerable<SqlDiagnostic> UnknownObjects(NameCollector visitor, SchemaInfo schema)
    {
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

    /// <summary>
    /// Flags unknown and ambiguous columns in SELECTs. Each query has a scope of its FROM sources, chained to the outer queries
    /// for correlated subqueries. Sources with unknown columns (derived tables, CTEs, temp tables, UPDATE targets…) make a
    /// scope incomplete, so an unresolved name there is left alone rather than risk a false positive.
    /// </summary>
    private sealed class ColumnChecker(SchemaInfo schema, HashSet<string> cteNames) : TSqlFragmentVisitor
    {
        // Their first argument (year, dd, mi…) parses as a column reference.
        private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase)
            { "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC", "DATE_BUCKET" };

        private sealed record Source(string? ExposedName, SchemaObject? Object);

        private sealed class Scope
        {
            public List<Source> Sources { get; } = new();
            public HashSet<string> SelectAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool Incomplete { get; set; }
        }

        private readonly List<Scope> _scopes = new();
        private readonly HashSet<ColumnReferenceExpression> _skip = new();

        public List<SqlDiagnostic> Diagnostics { get; } = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            var scope = new Scope();
            foreach (var table in node.FromClause?.TableReferences ?? [])
                AddSources(scope, table);
            foreach (var element in node.SelectElements)
                if (element is SelectScalarExpression { ColumnName: { } alias }) scope.SelectAliases.Add(alias.Value);
            _scopes.Add(scope);
            try { base.ExplicitVisit(node); }
            finally { _scopes.RemoveAt(_scopes.Count - 1); }
        }

        // Subqueries in UPDATE/DELETE/MERGE can reference the target table, whose columns aren't tracked here.
        public override void ExplicitVisit(UpdateSpecification node) => WithIncompleteScope(() => base.ExplicitVisit(node));
        public override void ExplicitVisit(DeleteSpecification node) => WithIncompleteScope(() => base.ExplicitVisit(node));
        public override void ExplicitVisit(MergeSpecification node) => WithIncompleteScope(() => base.ExplicitVisit(node));

        private void WithIncompleteScope(Action visit)
        {
            _scopes.Add(new Scope { Incomplete = true });
            try { visit(); }
            finally { _scopes.RemoveAt(_scopes.Count - 1); }
        }

        public override void Visit(FunctionCall node)
        {
            if (DatePartFunctions.Contains(node.FunctionName.Value) && node.Parameters.Count > 0 && node.Parameters[0] is ColumnReferenceExpression part)
                _skip.Add(part);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (_scopes.Count == 0 || _skip.Contains(node) || node.ColumnType != ColumnType.Regular) return;
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (ids is not { Count: 1 or 2 }) return;
            var column = ids[^1];
            if (ids.Count == 2) CheckQualified(ids[0].Value, column);
            else CheckUnqualified(column);
        }

        private void CheckQualified(string qualifier, Identifier column)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                var source = _scopes[i].Sources.FirstOrDefault(s => string.Equals(s.ExposedName, qualifier, StringComparison.OrdinalIgnoreCase));
                if (source == null) continue;
                if (source.Object != null && source.Object.FindColumn(column.Value) == null)
                    Report(column, $"Invalid column name '{column.Value}'.");
                return;
            }
        }

        private void CheckUnqualified(Identifier column)
        {
            var name = column.Value;
            if (_scopes[^1].Sources.Count == 0) return;
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                var scope = _scopes[i];
                var owners = scope.Sources.Count(s => s.Object?.FindColumn(name) != null);
                if (owners > 1)
                {
                    Report(column, $"Ambiguous column name '{name}'.");
                    return;
                }
                if (owners == 1 || scope.Incomplete || scope.SelectAliases.Contains(name) || scope.Sources.Any(s => s.Object == null)) return;
            }
            Report(column, $"Invalid column name '{name}'.");
        }

        private void Report(Identifier id, string message) =>
            Diagnostics.Add(new SqlDiagnostic(id.StartOffset, id.FragmentLength, message, DiagnosticSeverity.Error));

        private void AddSources(Scope scope, Microsoft.SqlServer.TransactSql.ScriptDom.TableReference table)
        {
            switch (table)
            {
                case JoinTableReference join:
                    AddSources(scope, join.FirstTableReference);
                    AddSources(scope, join.SecondTableReference);
                    break;
                case JoinParenthesisTableReference paren:
                    AddSources(scope, paren.Join);
                    break;
                case NamedTableReference named:
                    var name = named.SchemaObject;
                    scope.Sources.Add(new Source(named.Alias?.Value ?? name.BaseIdentifier?.Value, Resolve(name)));
                    break;
                case TableReferenceWithAlias aliased:
                    scope.Sources.Add(new Source(aliased.Alias?.Value, null));
                    break;
                default:
                    scope.Incomplete = true;
                    break;
            }
        }

        private SchemaObject? Resolve(SchemaObjectName name)
        {
            if (name.ServerIdentifier != null || name.DatabaseIdentifier != null || name.BaseIdentifier == null) return null;
            var baseName = name.BaseIdentifier.Value;
            var schemaName = name.SchemaIdentifier?.Value;
            if (baseName.StartsWith('#') || (schemaName == null && (cteNames.Contains(baseName) || PseudoTables.Contains(baseName)))) return null;
            if (schemaName != null && SystemSchemas.Contains(schemaName)) return null;
            var obj = schema.Objects
                .Where(o => o.Kind is SchemaObjectKind.Table or SchemaObjectKind.View
                            && o.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                            && (schemaName == null || o.Schema.Equals(schemaName, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(o => o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            return obj is { Columns.Count: > 0 } ? obj : null;
        }
    }

    private sealed class NameCollector : TSqlFragmentVisitor
    {
        public List<(SchemaObjectName Name, bool IsProcedure)> References { get; } = new();
        public HashSet<string> LocalNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Created { get; } = new();
        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ChangesDatabase { get; private set; }

        public override void Visit(NamedTableReference node) => References.Add((node.SchemaObject, false));

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is { } name) References.Add((name, true));
        }

        public override void Visit(CommonTableExpression node)
        {
            LocalNames.Add(node.ExpressionName.Value);
            CteNames.Add(node.ExpressionName.Value);
        }

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
