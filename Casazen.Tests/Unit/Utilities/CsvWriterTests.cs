using System.Text;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

/// <summary>
/// SR-10: the CSV the host downloads is safe to open. A text a spreadsheet would run as a formula (a property name typed by a user, for
/// example) is written with a leading apostrophe, and the file is the Italian one: BOM, <c>;</c>, decimal comma, <c>CRLF</c>.
/// </summary>
public class CsvWriterTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+39 333 1234567", "'+39 333 1234567")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("=HYPERLINK(\"http://evil\";\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\";\"\"x\"\")\"")]
    [InlineData("Trullo di Ostuni", "Trullo di Ostuni")]
    public void Write_AFormulaIsText_AndANumberIsANumber_InAnItalianFile(string name, string expectedCell)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { name, 12.5m, -3 } };

        var file = CsvWriter.Write(rows);

        Assert.Equal(Encoding.UTF8.GetPreamble(), file[..3]);
        Assert.Equal(expectedCell + ";12,50;-3\r\n", Encoding.UTF8.GetString(file, 3, file.Length - 3));
    }
}
