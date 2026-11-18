using Sqlzilla.Sql;

namespace Sqlzilla.Tests;

public class SqlCommentTests
{
    [Fact]
    public void CommentsAtSmallestIndentAndSkipsBlankLines()
    {
        var result = SqlComment.ToggleLines(["SELECT a", "", "  FROM t"]);
        Assert.Equal(["-- SELECT a", "", "--   FROM t"], result);
        Assert.Equal(["  -- a", "  --   b"], SqlComment.ToggleLines(["  a", "    b"]));
    }

    [Fact]
    public void UncommentsWhenEveryLineIsCommented()
    {
        var result = SqlComment.ToggleLines(["-- SELECT a", "  --FROM t", ""]);
        Assert.Equal(["SELECT a", "  FROM t", ""], result);
    }

    [Fact]
    public void MixedSelectionGetsCommented()
    {
        var result = SqlComment.ToggleLines(["-- old", "SELECT 1"]);
        Assert.Equal(["-- -- old", "-- SELECT 1"], result);
    }
}
