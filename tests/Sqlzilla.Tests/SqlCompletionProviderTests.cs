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
    }

    [Fact]
    public void NoSuggestionsWithoutPrefixUnlessRequested()
    {
        Assert.Empty(Labels("SELECT |"));
        Assert.NotEmpty(Labels("SELECT |", explicitRequest: true));
    }

    [Fact]
    public void InsertTextQuotesAwkwardNames()
    {
        var item = SqlCompletionProvider.Complete("SELECT * FROM Order L", 21, Schema, false).Items.First(i => i.Label == "Order Lines");
        Assert.Equal("dbo.[Order Lines]", item.InsertText);
    }

    [Fact]
    public void SuggestsDeclaredVariables() =>
        Assert.Equal(["@customerId"], Labels("DECLARE @customerId int; SELECT @cu|"));
}
