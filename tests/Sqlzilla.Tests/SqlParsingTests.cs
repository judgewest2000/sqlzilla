using Sqlzilla.Sql;

namespace Sqlzilla.Tests;

public class SqlBatchSplitterTests
{
    [Fact]
    public void SplitsOnGoLinesAndTracksStartLines()
    {
        var batches = SqlBatchSplitter.Split("SELECT 1\nGO\n\nSELECT 2\n  go  -- next\nSELECT 3");

        Assert.Equal(["SELECT 1", "\nSELECT 2", "SELECT 3"], batches.Select(b => b.Text));
        Assert.Equal([0, 2, 5], batches.Select(b => b.StartLine));
    }

    [Fact]
    public void GoWithCountRepeatsBatch()
    {
        var batch = Assert.Single(SqlBatchSplitter.Split("INSERT t DEFAULT VALUES\nGO 5"));
        Assert.Equal(5, batch.RepeatCount);
    }

    [Fact]
    public void IgnoresGoInsideBlockCommentsAndStrings()
    {
        var script = "SELECT 'a\nGO\nb'\n/* x\nGO\n*/\nSELECT 1";
        var batch = Assert.Single(SqlBatchSplitter.Split(script));
        Assert.Equal(script, batch.Text);
    }

    [Fact]
    public void GoAsPartOfALineIsNotASeparator()
    {
        Assert.Single(SqlBatchSplitter.Split("SELECT 1 AS GO\nSELECT GOAL FROM t"));
    }

    [Fact]
    public void SkipsEmptyAndCommentOnlyBatches()
    {
        Assert.Single(SqlBatchSplitter.Split("GO\n-- nothing\nGO\nSELECT 1\nGO\n"));
    }

    [Fact]
    public void NormalisesCrLf()
    {
        var batches = SqlBatchSplitter.Split("SELECT 1\r\nGO\r\nSELECT 2");
        Assert.Equal(["SELECT 1", "SELECT 2"], batches.Select(b => b.Text));
    }
}

public class SqlWriteDetectorTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("SELECT 'DELETE FROM t' -- DROP TABLE t")]
    [InlineData("SELECT [update], [delete] FROM t /* INSERT */")]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM t FOR UPDATE")]
    [InlineData("SET NOCOUNT ON; WITH x AS (SELECT 1 a) SELECT a FROM x")]
    [InlineData("create table #abc (id int)\n\ninsert into #abc values (1)")]
    [InlineData("SELECT * INTO #copy FROM t; UPDATE #copy SET a = 1; DELETE FROM #copy; TRUNCATE TABLE #copy")]
    [InlineData("DROP TABLE IF EXISTS #a, ##b; ALTER TABLE #copy ADD b int; CREATE NONCLUSTERED INDEX ix ON #copy (a)")]
    [InlineData("DECLARE @t TABLE (a int); INSERT TOP (5) INTO @t SELECT a FROM t; MERGE INTO @t USING s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (a) VALUES (1);")]
    public void ReadOnlyScriptsHaveNoWrites(string sql) => Assert.Empty(SqlWriteDetector.Find(sql));

    [Theory]
    [InlineData("update t set a = 1", "UPDATE")]
    [InlineData("DELETE t WHERE id = 1", "DELETE")]
    [InlineData("INSERT INTO t VALUES (1)", "INSERT")]
    [InlineData("SELECT * INTO copy FROM t", "SELECT INTO")]
    [InlineData("DROP TABLE #a, b", "DROP")]
    [InlineData("INSERT INTO #t EXEC dbo.DoThings", "EXEC")]
    [InlineData("UPDATE a SET x = 1 FROM t a", "UPDATE")]
    [InlineData("TRUNCATE TABLE t", "TRUNCATE")]
    [InlineData("EXEC dbo.DoThings", "EXEC")]
    [InlineData("CREATE TABLE x (a int)", "CREATE")]
    public void DetectsWrites(string sql, string expected) => Assert.Contains(expected, SqlWriteDetector.Find(sql));

    [Fact]
    public void InsertIntoIsNotReportedAsSelectInto() =>
        Assert.Equal(["INSERT"], SqlWriteDetector.Find("INSERT INTO t SELECT * FROM s"));
}

public class SqlTokenizerTests
{
    [Fact]
    public void ClassifiesTokens()
    {
        const string sql = "SELECT N'it''s', [my col], @x, #tmp, 0x1F, GETDATE() FROM dbo.t -- c";
        var kinds = SqlTokenizer.Tokenize(sql).Where(t => t.Kind != SqlTokenKind.Operator).Select(t => (t.Kind, t.Text(sql))).ToList();

        Assert.Equal(
        [
            (SqlTokenKind.Keyword, "SELECT"), (SqlTokenKind.String, "N'it''s'"), (SqlTokenKind.QuotedIdentifier, "[my col]"),
            (SqlTokenKind.Variable, "@x"), (SqlTokenKind.TempTable, "#tmp"), (SqlTokenKind.Number, "0x1F"),
            (SqlTokenKind.Function, "GETDATE"), (SqlTokenKind.Keyword, "FROM"), (SqlTokenKind.Identifier, "dbo"),
            (SqlTokenKind.Identifier, "t"), (SqlTokenKind.Comment, "-- c"),
        ], kinds);
    }

    [Fact]
    public void BlockCommentsNest()
    {
        const string sql = "/* a /* b */ c */SELECT";
        var tokens = SqlTokenizer.Tokenize(sql);
        Assert.Equal(SqlTokenKind.Comment, tokens[0].Kind);
        Assert.Equal("SELECT", tokens[1].Text(sql));
    }
}
