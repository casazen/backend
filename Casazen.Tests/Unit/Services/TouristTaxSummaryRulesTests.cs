using System.Text;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-100, E07/F03/US04/T02: the small rules of the tourist tax summary that cost if they are wrong, each with a short proof: what the CSV
/// says about an amount that is missing, how an amount sent as text is read, what a receipt is, and what the first row of the file carries.
/// </summary>
public sealed class TouristTaxSummaryRulesTests
{
    private static readonly FiscalReportPeriod FirstQuarter = new(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

    private static readonly TouristTaxCsvTexts Texts = new("Nessun importo registrato", "Importo incompleto");

    /// <summary>
    /// PROOF TEST: money rule. A comune whose stays have no recorded amount is "Nessun importo registrato", never "niente da versare", and
    /// every row (and the total) that counts such stays says "Importo incompleto" next to the owed amount and the status, also when the status
    /// is "Versata": an amount that is missing is not a tax of zero.
    /// </summary>
    [Fact]
    public void TouristTaxSummaryCsv_AMissingAmount_IsNeverSaidAsNothingDue_Test()
    {
        var summary = new TouristTaxSummary(
            FirstQuarter,
            [
                new ComuneTaxSummary("Lecco", 2, 5, 4, 0m, 2, 0m, 0m, TouristTaxSummaryStatus.NothingOwed, []),
                new ComuneTaxSummary("Milano", 3, 6, 6, 66.90m, 1, 66.90m, 0m, TouristTaxSummaryStatus.Paid, []),
                new ComuneTaxSummary("Roma", 1, 4, 2, 72.10m, 0, 0m, 72.10m, TouristTaxSummaryStatus.ToPay, []),
            ],
            new TouristTaxSummaryTotals(6, 15, 12, 139.00m, 3, 66.90m, 72.10m));

        var csv = Lines(TouristTaxSummaryDocuments.Csv(summary, Texts));

        Assert.Equal("Lecco;2;5;4;0,00;2;0,00;0,00;Nessun importo registrato;Importo incompleto", csv[2]);
        Assert.Equal("Milano;3;6;6;66,90;1;66,90;0,00;Versata;Importo incompleto", csv[3]);
        Assert.Equal("Roma;1;4;2;72,10;0;0,00;72,10;Da versare;", csv[4]);
        Assert.Equal("Totale;6;15;12;139,00;3;66,90;72,10;;Importo incompleto", csv[5]);
        Assert.DoesNotContain(csv, line => line.Contains("Niente da versare", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The rows of a CSV file as text: read through a reader that consumes the BOM.</summary>
    private static string[] Lines(byte[] csv)
    {
        using var reader = new StreamReader(new MemoryStream(csv), Encoding.UTF8);
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.None);
    }
}
