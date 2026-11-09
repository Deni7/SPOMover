using SPOMover.Core.Reports;

namespace SPOMover.Host.Tests;

public class ImportExportTests
{
    [Fact]
    public void XlsxRoundTrip()
    {
        using var ms = new MemoryStream();
        SimpleXlsxWriter.Write(ms, "mapping", [["Старий", "Новий"], ["https://h/Звіт", "https://h/sites/x"], ["/a/", "/b/"], ["42", "x&<y>"]]);
        ms.Position = 0;
        var rows = SimpleXlsxReader.ReadFirstSheet(ms);
        Assert.Equal(4, rows.Count);
        Assert.Equal("https://h/Звіт", rows[1][0]);
        Assert.Equal("x&<y>", rows[3][1]);
        Assert.Equal("42", rows[3][0]);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnNames(int index, string name) => Assert.Equal(name, SimpleXlsxWriter.Column(index));
}
