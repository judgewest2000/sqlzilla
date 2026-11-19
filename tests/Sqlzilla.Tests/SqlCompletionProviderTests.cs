using Sqlzilla.Sql;

namespace Sqlzilla.Tests;

public class SqlCompletionProviderTests
{
    private static readonly SchemaInfo Schema = new(
    [
        new("dbo", "Customers", SchemaObjectKind.Table, ["CustomerId", "Name", "Email"]),
        new("sales", "Orders", SchemaObjectKind.Table, ["OrderId", "CustomerId", "Total"]),
        new("dbo", "Order Lines", SchemaObjectKind.View, ["LineId"]),
    ]);

    private static List<string> Labels(string textWithCaret, bool explicitRequest = false)
    {
        var caret = textWithCaret.IndexOf('|');
        var text = textWithCaret.Remove(caret, 1);
        return SqlCompletionProvider.Complete(text, caret, Schema, explicitRequest).Items.Select(i => i.Label).ToList();
    }

    [Fact]
    public void AliasDotListsColumnsOfAliasedTable() =>
        Assert.Equal(["CustomerId", "OrderId", "Total"], Labels("SELECT o.| FROM sales.Orders AS o JOIN dbo.Customers c ON 1=1").Order());

    [Fact]
    public void AliasCompletionWorksBeforeFromClauseIsTyped() =>
        Assert.Equal(["Email"], Labels("SELECT c.Em| FROM Customers c"));

    [Fact]
    public void SchemaDotListsObjectsInSchema() =>
        Assert.Equal(["Orders"], Labels("SELECT * FROM sales.|"));

    [Fact]
    public void PrefixOffersColumnsOfReferencedTablesFirst()
    {
        var labels = Labels("SELECT Cust| FROM dbo.Customers");
        Assert.Equal("CustomerId", labels[0]);
        Assert.Contains("Customers", labels);
    }

    [Fact]
    public void NothingInsideStringsOrComments()
    {
        Assert.Empty(Labels("SELECT 'Cust|'"));
        Assert.Empty(Labels("-- Cust|"));
        Assert.Empty(Labels("SELECT 1\n-- Cust|\nSELECT 2"));
    }

    [Fact]
    public void CompletesAfterLineComment() =>
        Assert.Contains("Customers", Labels("SELECT 1;\n\n--note\n\nSELECT * FROM Cu|\n"));

    [Theory]
    [InlineData("SELECT * FROM [Cu|")]
    [InlineData("SELECT * FROM [Cu|\n")]
    [InlineData("SELECT * FROM [Order Lines];\nSELECT * FROM [Cu|\n")]
    [InlineData("SELECT * FROM [Cu|\nSELECT * FROM [Order Lines]")]
    public void CompletesOpenBracketedName(string text) =>
        Assert.Contains("Customers", Labels(text));

    [Fact]
    public void NoSuggestionsWithoutPrefixUnlessRequested()
    {
        Assert.Empty(Labels("SELECT |"));
        Assert.NotEmpty(Labels("SELECT |", explicitRequest: true));
    }

    [Fact]
    public void InsertTextQuotesAwkwardNames()
    {
        var item = SqlCompletionProvider.Complete("SELECT * FROM Order", 19, Schema, false).Items.First(i => i.Label == "Order Lines");
        Assert.Equal("[Order Lines]", item.InsertText);
        var other = SqlCompletionProvider.Complete("SELECT * FROM Ord", 17, Schema, false).Items.First(i => i.Label == "Orders" && i.Detail == "sales");
        Assert.Equal("sales.Orders", other.InsertText);
    }

    [Fact]
    public void SuggestsDeclaredVariables() =>
        Assert.Equal(["@customerId"], Labels("DECLARE @customerId int; SELECT @cu|"));

    private static readonly SchemaInfo Shop = new(
    [
        new("dbo", "Customers", SchemaObjectKind.Table, [new SchemaColumn("CustomerId", "int", false, IsPrimaryKey: true), new SchemaColumn("Name", "nvarchar(100)")]),
        new("dbo", "Orders", SchemaObjectKind.Table, [new SchemaColumn("OrderId", "int", false, IsPrimaryKey: true), new SchemaColumn("CustomerId", "int", false), new SchemaColumn("Total", "decimal(18,2)")]),
        new("dbo", "OrderLines", SchemaObjectKind.Table, ["LineId", "OrderId"]),
        new("dbo", "GetOrders", SchemaObjectKind.Procedure, []) { Parameters = [new("@CustomerId", "int"), new("@Since", "date", HasDefault: true)] },
        new("dbo", "Discount", SchemaObjectKind.Function, []) { Parameters = [new("@Total", "money"), new("@Rate", "decimal(5,2)")], ReturnType = "money" },
    ],
    [
        new("FK_Orders_Customers", "dbo", "Orders", ["CustomerId"], "dbo", "Customers", ["CustomerId"]),
        new("FK_OrderLines_Orders", "dbo", "OrderLines", ["OrderId"], "dbo", "Orders", ["OrderId"]),
    ]);

    private static (string Text, int Caret) At(string textWithCaret) => (textWithCaret.Remove(textWithCaret.IndexOf('|'), 1), textWithCaret.IndexOf('|'));

    private static List<CompletionItem> ShopItems(string textWithCaret, bool explicitRequest = true)
    {
        var (text, caret) = At(textWithCaret);
        return SqlCompletionProvider.Complete(text, caret, Shop, explicitRequest).Items.ToList();
    }

    [Fact]
    public void AfterJoinSuggestsForeignKeyJoinsFirst()
    {
        var items = ShopItems("SELECT * FROM dbo.Orders o JOIN |");
        Assert.Equal(CompletionKind.Join, items[0].Kind);
        Assert.Contains(items, i => i.InsertText == "Customers c ON c.CustomerId = o.CustomerId");
        Assert.Contains(items, i => i.InsertText == "OrderLines ol ON ol.OrderId = o.OrderId");
        Assert.DoesNotContain(items, i => i.Kind is CompletionKind.Keyword or CompletionKind.Column);
    }

    [Fact]
    public void JoinAliasAvoidsAliasesInUse() =>
        Assert.Contains(ShopItems("SELECT * FROM dbo.Orders c JOIN |"), i => i.InsertText == "Customers c2 ON c2.CustomerId = c.CustomerId");

    [Fact]
    public void AfterExecOnlyProcedures() =>
        Assert.Equal(["GetOrders"], ShopItems("EXEC |").Where(i => i.Kind != CompletionKind.Schema).Select(i => i.Label));

    [Fact]
    public void NoPopupWhileTypingAnAlias() =>
        Assert.Empty(ShopItems("SELECT * FROM dbo.Customers c|", explicitRequest: false));

    [Fact]
    public void AliasResolvesToNearestStatement()
    {
        var items = ShopItems("SELECT * FROM dbo.Customers x;\nSELECT x.| FROM dbo.Orders x");
        Assert.Equal(["CustomerId", "OrderId", "Total"], items.Select(i => i.Label).Order());
        Assert.Equal("int · Orders", items.First(i => i.Label == "OrderId").Detail);
    }

    [Fact]
    public void CteColumnsComeFromItsSelectList() =>
        Assert.Equal(["CustomerId", "Spend"], ShopItems(
            "WITH totals AS (SELECT o.CustomerId, SUM(o.Total) AS Spend FROM dbo.Orders o GROUP BY o.CustomerId)\nSELECT t.| FROM totals t")
            .Select(i => i.Label).Order());

    [Fact]
    public void TempTablesAndTableVariablesAreKnown()
    {
        const string script = "CREATE TABLE #work (Id int NOT NULL PRIMARY KEY, Label nvarchar(20));\nDECLARE @ids TABLE (Value int);\n";
        Assert.Contains(ShopItems(script + "SELECT * FROM #w|"), i => i.Label == "#work");
        var columns = ShopItems(script + "SELECT w.| FROM #work w");
        Assert.Equal(["Id", "Label"], columns.Select(i => i.Label));
        Assert.Equal("int · #work", columns[0].Detail);
        Assert.Equal(["Value"], ShopItems(script + "SELECT v.| FROM @ids v").Select(i => i.Label));
    }

    [Fact]
    public void SelectIntoTempTableAndDerivedTableColumns()
    {
        Assert.Equal(["CustomerId", "Name"], ShopItems("SELECT * INTO #c FROM dbo.Customers;\nSELECT x.| FROM #c x").Select(i => i.Label));
        Assert.Equal(["Id"], ShopItems("SELECT d.| FROM (SELECT CustomerId AS Id FROM dbo.Customers) d").Select(i => i.Label));
    }

    [Fact]
    public void ExecSuggestsProcedureParameters() =>
        Assert.Equal(["@CustomerId", "@Since"], ShopItems("EXEC dbo.GetOrders @|").Where(i => i.Kind == CompletionKind.Variable).Select(i => i.Label));

    [Fact]
    public void MakeAliasUsesWordInitials()
    {
        Assert.Equal("ol", SqlCompletionProvider.MakeAlias("OrderLines", new HashSet<string>()));
        Assert.Equal("co", SqlCompletionProvider.MakeAlias("customer_orders", new HashSet<string>()));
        Assert.Equal("o2", SqlCompletionProvider.MakeAlias("Orders", new HashSet<string> { "o" }));
    }

    [Fact]
    public void SignatureHelpTracksActiveArgument()
    {
        var (text, caret) = At("SELECT DATEADD(day, ISNULL(x, (1)), |");
        var sig = SqlSignatureHelp.Get(text, caret, Shop)!;
        Assert.Equal("DATEADD", sig.Name);
        Assert.Equal(2, sig.ActiveParameter);

        (text, caret) = At("SELECT * FROM t WHERE a IN (1, dbo.Discount(Total, |");
        sig = SqlSignatureHelp.Get(text, caret, Shop)!;
        Assert.Equal("dbo.Discount", sig.Name);
        Assert.Equal(1, sig.ActiveParameter);
        Assert.Equal("money", sig.Returns);

        Assert.Null(SqlSignatureHelp.Get("SELECT * FROM t WHERE a IN (1, ", 31, Shop));
    }

    [Fact]
    public void SignatureHelpForExecSupportsNamedArguments()
    {
        var (text, caret) = At("EXEC dbo.GetOrders 1, |");
        Assert.Equal(1, SqlSignatureHelp.Get(text, caret, Shop)!.ActiveParameter);
        (text, caret) = At("EXEC dbo.GetOrders @Since = |");
        Assert.Equal(1, SqlSignatureHelp.Get(text, caret, Shop)!.ActiveParameter);
    }

    [Fact]
    public void QuickInfoDescribesColumnsAliasesAndVariables()
    {
        const string text = "DECLARE @n nvarchar(50);\nSELECT o.Total, Name FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
        var total = SqlQuickInfo.Get(text, text.IndexOf("Total"), Shop)!;
        Assert.Equal("Orders.Total", total.Title);
        Assert.StartsWith("decimal(18,2)", total.Body);
        Assert.Equal("Customers.Name", SqlQuickInfo.Get(text, text.IndexOf("Name"), Shop)!.Title);
        Assert.Equal("o → table dbo.Orders", SqlQuickInfo.Get(text, text.IndexOf("o.Total"), Shop)!.Title);
        Assert.Contains("→ dbo.Customers.CustomerId", SqlQuickInfo.Get(text, text.LastIndexOf("CustomerId"), Shop)!.Body);
        Assert.Equal("@n nvarchar(50)", SqlQuickInfo.Get(text, 9, Shop)!.Title);
    }

    [Fact]
    public void DiagnosticsReportSyntaxErrorsAndUnknownObjects()
    {
        var syntax = SqlDiagnostics.Analyze("SELECT * FORM dbo.Orders", Shop);
        Assert.Contains(syntax, d => d.Severity == DiagnosticSeverity.Error);

        const string script = "WITH x AS (SELECT 1 AS a) SELECT * FROM x;\nSELECT * FROM dbo.Nope n JOIN sys.objects s ON 1 = 1 JOIN #t ON 1 = 1;\nUPDATE o SET Total = 0 FROM dbo.Orders o";
        var warnings = SqlDiagnostics.Analyze(script, Shop);
        var warning = Assert.Single(warnings);
        Assert.Equal("Invalid object name 'dbo.Nope'.", warning.Message);
        Assert.Equal("dbo.Nope", script.Substring(warning.Start, warning.Length));
        Assert.Empty(SqlDiagnostics.Analyze("SELECT * FROM dbo.Nope", SchemaInfo.Empty));
    }

    [Fact]
    public void DocsTargetResolvesColumnsObjectsAndBuiltins()
    {
        const string text = "SELECT o.Total, GETDATE() FROM dbo.Orders o";
        var column = SqlQuickInfo.Target(text, text.IndexOf("Total") + 5, Shop)!;
        Assert.Equal(("Orders", "Total"), (column.Object!.Name, column.Column));
        Assert.Equal("GETDATE", SqlQuickInfo.Target(text, text.IndexOf("GETDATE") + 2, Shop)!.Builtin!.Name);
        Assert.Equal("Orders", SqlQuickInfo.Target(text, text.IndexOf("Orders") + 1, Shop)!.Object!.Name);
    }

    [Fact]
    public void FormatTypeShowsLengthsAndPrecision()
    {
        Assert.Equal("nvarchar(50)", SchemaInfo.FormatType("nvarchar", 100, 0, 0));
        Assert.Equal("varchar(max)", SchemaInfo.FormatType("varchar", -1, 0, 0));
        Assert.Equal("decimal(18,2)", SchemaInfo.FormatType("decimal", 9, 18, 2));
        Assert.Equal("datetime2(3)", SchemaInfo.FormatType("datetime2", 7, 23, 3));
        Assert.Equal("int", SchemaInfo.FormatType("int", 4, 10, 0));
    }
}
