namespace Sqlzilla.Services;

/// <summary>An inclusive rectangle of cells; corners may be given in any order.</summary>
public readonly record struct CellRange(int Row1, int Column1, int Row2, int Column2)
{
    public int Top => Math.Min(Row1, Row2);
    public int Bottom => Math.Max(Row1, Row2);
    public int Left => Math.Min(Column1, Column2);
    public int Right => Math.Max(Column1, Column2);

    public bool Contains(int row, int column) => row >= Top && row <= Bottom && column >= Left && column <= Right;
}

/// <summary>Excel-style selection: one or more cell ranges (Ctrl adds a range, Shift extends the last one).</summary>
public sealed class GridSelection
{
    private readonly List<CellRange> _ranges = new();

    public IReadOnlyList<CellRange> Ranges => _ranges;
    public bool IsEmpty => _ranges.Count == 0;

    public void Clear() => _ranges.Clear();

    public void Set(CellRange range)
    {
        _ranges.Clear();
        _ranges.Add(range);
    }

    public void Add(CellRange range) => _ranges.Add(range);

    /// <summary>Replaces the most recent range (used while dragging or Shift-extending).</summary>
    public void ReplaceLast(CellRange range)
    {
        if (_ranges.Count == 0) _ranges.Add(range);
        else _ranges[^1] = range;
    }

    public bool Contains(int row, int column)
    {
        foreach (var r in _ranges)
            if (r.Contains(row, column)) return true;
        return false;
    }

    public bool RowTouched(int row)
    {
        foreach (var r in _ranges)
            if (row >= r.Top && row <= r.Bottom) return true;
        return false;
    }

    public bool ColumnTouched(int column)
    {
        foreach (var r in _ranges)
            if (column >= r.Left && column <= r.Right) return true;
        return false;
    }

    /// <summary>
    /// The selected block as columns and rows for copying. Several ranges are merged into the rows and columns they
    /// cover; cells inside that block but outside every range come out empty.
    /// </summary>
    public (List<ResultColumn> Columns, List<string?[]> Rows) Extract(IReadOnlyList<ResultColumn> columns, IReadOnlyList<string?[]> rows)
    {
        var rowIndexes = new SortedSet<int>();
        var columnIndexes = new SortedSet<int>();
        foreach (var r in _ranges)
        {
            for (var i = Math.Max(0, r.Top); i <= Math.Min(rows.Count - 1, r.Bottom); i++) rowIndexes.Add(i);
            for (var c = Math.Max(0, r.Left); c <= Math.Min(columns.Count - 1, r.Right); c++) columnIndexes.Add(c);
        }

        var single = _ranges.Count == 1;
        var outColumns = columnIndexes.Select(c => columns[c]).ToList();
        var outRows = rowIndexes
            .Select(i => columnIndexes.Select(c => single || Contains(i, c) ? rows[i][c] : "").ToArray())
            .ToList();
        return (outColumns, outRows);
    }
}
