using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class QuotedCsvTests
{
    [Fact]
    public void ReadRecords_QuotedHeaderWithNewlines_IsOneRecord()
    {
        const string text =
            "Codice Regione;\"Codice dell'Unità territoriale sovracomunale \r\n(valida a fini statistici)\";Codice Comune formato alfanumerico\r\n01;201;001001\r\n";

        var records = QuotedCsv.ReadRecords(text);

        Assert.Equal(2, records.Count);
        Assert.Equal(3, records[0].Cells.Count);
        Assert.Equal("Codice Regione", records[0].Cells[0]);
        Assert.Contains("sovracomunale", records[0].Cells[1]);
        Assert.Equal("001001", records[1].Cells[2]);
        Assert.Equal(';', QuotedCsv.DetectDelimiter(text, QuotedCsv.DefaultDelimiters));
    }

    [Fact]
    public void ReadRecords_EscapedQuotes_AreKept()
    {
        var records = QuotedCsv.ReadRecords("a;\"b\"\"c\";d");

        Assert.Equal(["a", "b\"c", "d"], records[0].Cells);
    }
}
