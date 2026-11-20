using Sqlzilla.Import;

namespace Sqlzilla.Tests;

public class ImportTests
{
    [Fact]
    public void DelimitedHandlesQuotesNewlinesBlankLinesAndRaggedRows()
    {
        const string csv = "\uFEFFId,Name,,Name\r\n1,\"Smith, \"\"J\"\"\",x\r\n\r\n2,\"two\nlines\"\n3";
        var data = ImportReaders.ReadDelimited(csv, ImportReaders.DetectDelimiter(csv), hasHeader: true);
        Assert.Equal(["Id", "Name", "Column3", "Name_2"], data.Columns);
        Assert.Equal(3, data.Rows.Count);
        Assert.Equal(["1", "Smith, \"J\"", "x", null], data.Rows[0].AsEnumerable());
        Assert.Equal("two\nlines", data.Rows[1][1]);
        Assert.Equal(["3", null, null, null], data.Rows[2].AsEnumerable());
    }

    [Theory]
    [InlineData("a\tb;c\n1\t2", '\t')]
    [InlineData("a;b;\"c,d\"\n", ';')]
    [InlineData("a|b|c", '|')]
    public void DetectsDelimiter(string text, char expected) => Assert.Equal(expected, ImportReaders.DetectDelimiter(text));

    [Fact]
    public void JsonUnionsPropertiesAndKeepsNestedValuesAsJson()
    {
        var data = ImportReaders.ReadJson("""{ "items": [ { "id": 1, "tags": ["a"] }, { "id": 2, "name": null, "ok": true } ] }""");
        Assert.Equal(["id", "tags", "name", "ok"], data.Columns);
        Assert.Equal(["1", "[\"a\"]", null, null], data.Rows[0].AsEnumerable());
        Assert.Equal(["2", null, null, "true"], data.Rows[1].AsEnumerable());

        var lines = ImportReaders.ReadJson("{\"a\":1}\n{\"a\":2}\n");
        Assert.Equal(["1", "2"], lines.Rows.Select(r => r[0]));
    }

    [Theory]
    [InlineData("int", false, "1", "-20", "300")]
    [InlineData("bigint", true, "1", "", "9999999999")]
    [InlineData("decimal(18,3)", false, "1.5", "-22.125")]
    [InlineData("float", false, "1e5", "2.5")]
    [InlineData("bit", false, "true", "FALSE")]
    [InlineData("nvarchar(50)", false, "007", "12")]
    [InlineData("date", false, "2024-01-31", "2024-02-01")]
    [InlineData("datetime2", false, "2024-01-31 10:15:00", "2024-02-01")]
    [InlineData("datetimeoffset", false, "2024-01-31T10:15:00Z", "2024-01-31T10:15:00+01:00")]
    [InlineData("time", false, "06:00", "23:59:30")]
    [InlineData("uniqueidentifier", false, "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("nvarchar(100)", false, "a", "this value is definitely longer than fifty characters in total length")]
    [InlineData("nvarchar(50)", true, null, " ")]
    public void InfersNarrowestType(string expected, bool nullable, params string?[] values) =>
        Assert.Equal((expected, nullable), ImportTypes.Infer(values));

    [Fact]
    public void ConvertsTextToSqlValues()
    {
        Assert.Equal(-12, ImportTypes.Convert("-12", "int"));
        Assert.Equal(1.5m, ImportTypes.Convert(" 1.5 ", "decimal(18,2)"));
        Assert.Equal(true, ImportTypes.Convert("yes", "bit"));
        Assert.Equal(new DateTime(2024, 1, 31), ImportTypes.Convert("2024-01-31", "date"));
        Assert.Equal(new byte[] { 0xAB, 0x01 }, ImportTypes.Convert("0xAB01", "varbinary(10)"));
        Assert.Equal(" keep ", ImportTypes.Convert(" keep ", "nvarchar(10)"));
        Assert.Equal(DBNull.Value, ImportTypes.Convert("", "int"));
        Assert.Throws<FormatException>(() => ImportTypes.Convert("abc", "int"));
        Assert.Throws<FormatException>(() => ImportTypes.Convert("1,5", "int"));
    }

    [Fact]
    public void CreateTableSqlQuotesNames() =>
        Assert.Equal("CREATE TABLE [dbo].[My]]Table]\n(\n    [Id] int NOT NULL,\n    [Full Name] nvarchar(50) NULL\n);",
            new ImportPlan("dbo", "My]Table", true, [new(0, "Id", "int", false), new(1, "Full Name", "nvarchar(50)")]).CreateTableSql());

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D }, ImportFormat.Delimited, false)] // PNG
    [InlineData(new byte[] { 0x61, 0x2C, 0x62, 0x0D, 0x0A, 0x31, 0x2C, 0x32 }, ImportFormat.Delimited, true)]              // "a,b\r\n1,2"
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, ImportFormat.Excel, true)]                              // xlsx zip
    [InlineData(new byte[] { 0x61, 0x2C, 0x62 }, ImportFormat.Excel, false)]                                                // text named .xlsx
    public void CheckFileRejectsWrongContent(byte[] content, ImportFormat format, bool ok)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            Assert.Equal(ok, ImportReaders.CheckFile(path, format) == null);
        }
        finally { File.Delete(path); }
    }
}
