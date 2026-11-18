using Sqlzilla.Services;

namespace Sqlzilla.Tests;

public class GridSelectionTests
{
    private static readonly ResultColumn[] Columns = [new("A", "int"), new("B", "int"), new("C", "int")];
    private static readonly string?[][] Rows = [["1", "2", "3"], ["4", "5", "6"], ["7", "8", "9"]];

    [Fact]
    public void BoxSelectionCopiesJustThatBlock()
    {
        var selection = new GridSelection();
        selection.Set(new CellRange(2, 2, 1, 1));
        var (columns, rows) = selection.Extract(Columns, Rows);
        Assert.Equal(["B", "C"], columns.Select(c => c.Name));
        Assert.Equal([["5", "6"], ["8", "9"]], rows);
    }

    [Fact]
    public void CtrlAddedRangesMergeAndBlankUnselectedCells()
    {
        var selection = new GridSelection();
        selection.Set(new CellRange(0, 0, 0, 0));
        selection.Add(new CellRange(2, 2, 2, 2));
        var (columns, rows) = selection.Extract(Columns, Rows);
        Assert.Equal(["A", "C"], columns.Select(c => c.Name));
        Assert.Equal([["1", ""], ["", "9"]], rows);
        Assert.True(selection.RowTouched(2));
        Assert.False(selection.ColumnTouched(1));
    }
}
