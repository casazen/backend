using System.Xml.Linq;
using System.Xml.Schema;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F01/US02/T03 (NF-83, NF-87): the amounts of a guest document are computed in one place and are cent-accurate, the XML is written from the real
/// data with every text escaped, and it is validated against the official schema before it is used (or not produced at all while the schema is not
/// installed). The validator reads no DTD, no external entity and no network.
/// </summary>
public class FatturaPaProofTests
{
    private static readonly FiscalStampDutyRule Stamp = new(77.47m, 2.00m, ChargedToRecipient: false);

    public static TheoryData<string, decimal, decimal, decimal, decimal, decimal> TotalsCases() => new()
    {
        // Lodging 300 and cleaning 50 at 10%, tourist tax 12 without VAT (N1): VAT separated from what the guest paid, no stamp duty.
        { "ordinary-10", 330.18m, 31.82m, 0m, 362.00m, 0m },

        // Flat-rate regime, everything without VAT (N2.2) and the tourist tax (N1): 248 without VAT, stamp duty due and not charged.
        { "flat-rate", 248.00m, 0m, 2.00m, 248.00m, 0m },

        // One line of 100 at 22%: 81.97 + 18.03.
        { "single-22", 81.97m, 18.03m, 0m, 100.00m, 0m },

        // Two lines of 10.01 at 22%: the group is 16.41 + 3.61, the cent of difference goes to the first (largest) line.
        { "remainder-22", 16.41m, 3.61m, 0m, 20.02m, 8.21m },
    };

    [Theory]
    [MemberData(nameof(TotalsCases))]
    public void FatturaPa_Totals_CentAccurate_Test(string scenario, decimal taxableBase, decimal vat, decimal stampDuty, decimal total, decimal firstLineNet)
    {
        var amounts = FiscalDocumentTotals.FromGross(Lines(scenario), Stamp);

        Assert.Equal(taxableBase, amounts.TaxableBase);
        Assert.Equal(vat, amounts.VatAmount);
        Assert.Equal(stampDuty, amounts.StampDuty);
        Assert.Equal(total, amounts.Total);
        Assert.Equal(Lines(scenario).Sum(l => l.GrossAmount), amounts.TaxableBase + amounts.VatAmount);
        Assert.Equal(amounts.TaxableBase, amounts.Lines.Sum(l => l.TotalPrice));
        Assert.All(amounts.Summary, row => Assert.Equal(row.TaxableAmount, amounts.Lines.Where(l => l.VatRatePercent == row.VatRatePercent && l.NatureCode == row.NatureCode).Sum(l => l.TotalPrice)));
        if (firstLineNet != 0m)
            Assert.Equal(firstLineNet, amounts.Lines[0].TotalPrice);
        Assert.Equal(FiscalDocumentTotals.RuleVersion, amounts.RuleVersion);
    }

    [Fact]
    public void FatturaPa_Totals_StampDutyChargedToTheRecipient_IsInTheTotal_AndACreditNeverExceedsTheDocument()
    {
        var charged = FiscalDocumentTotals.FromGross(Lines("flat-rate"), Stamp with { ChargedToRecipient = true });
        Assert.Equal(250.00m, charged.Total);

        var credit = FiscalDocumentTotals.ScaleForCredit(Lines("ordinary-10"), 100m);
        Assert.Equal(100m, credit.Sum(l => l.GrossAmount));
        Assert.Throws<DomainRuleException>(() => FiscalDocumentTotals.ScaleForCredit(Lines("ordinary-10"), 362.01m));
        Assert.Throws<DomainRuleException>(() => FiscalDocumentTotals.FromGross([new FiscalDocumentGrossLine("TEST", 1m, -1m, 10m, null)], Stamp));
    }

    [Fact]
    public void FatturaPa_SpecialCharactersEscaped_Test()
    {
        var input = Input(recipientName: "Rossi & Figli <S.r.l.> \"Àèìòù\"", city: "Sant'Agata de' Goti");

        var xml = FatturaPaXmlBuilder.Write(input);

        Assert.Contains("Rossi &amp; Figli &lt;S.r.l.&gt;", xml, StringComparison.Ordinal);
        var document = XDocument.Parse(xml);
        var recipient = document.Descendants("CessionarioCommittente").Single();
        Assert.Equal("Rossi & Figli <S.r.l.> \"Àèìòù\"", recipient.Descendants("Denominazione").Single().Value);
        Assert.Equal("Sant'Agata de' Goti", recipient.Descendants("Comune").Single().Value);
        Assert.Equal("362.00", document.Descendants("ImportoTotaleDocumento").Single().Value);
        Assert.Equal("2026-11-12", document.Descendants("Data").First().Value);
        Assert.Equal("0000000", document.Descendants("CodiceDestinatario").Single().Value);
        Assert.Equal(FatturaPaXmlBuilder.ForeignRecipientCode, FatturaPaXmlBuilder.DestinationCode("CH", "ABC1234"));
    }

    /// <summary>
    /// The official schema is installed only once its files are committed with their checksums (docs/runbooks/fiscal-documents.md): with it, a built
    /// document validates and an altered one does not; without it, nothing is produced (422 <c>fatturapa_schema_unavailable</c>), never an XML that
    /// was not validated.
    /// </summary>
    [Fact]
    public void FatturaPa_Xml_ValidatesAgainstOfficialSchema_Test()
    {
        var builder = new FatturaPaXmlBuilder(new FatturaPaSchemaProvider(), NullLogger<FatturaPaXmlBuilder>.Instance);

        if (!FatturaPaSchemaProvider.HasOfficialSchema)
        {
            var unavailable = Assert.Throws<DomainRuleException>(() => builder.Build(Input()));
            Assert.Equal(FatturaPaErrors.SchemaUnavailable, unavailable.Code);
            return;
        }

        var xml = builder.Build(Input());
        Assert.Empty(XsdValidator.Validate(xml, new FatturaPaSchemaProvider().GetSchemas()));

        var altered = XDocument.Parse(xml);
        altered.Descendants("Numero").Single().Remove();
        Assert.NotEmpty(XsdValidator.Validate(altered.ToString(), new FatturaPaSchemaProvider().GetSchemas()));
    }

    [Fact]
    public void XsdValidator_ReportsInvalidDocuments_AndNeverReadsADtdOrAnExternalEntity()
    {
        var schemas = new XmlSchemaSet { XmlResolver = null };
        schemas.Add(null, System.Xml.XmlReader.Create(new StringReader(
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"amount\" type=\"xs:decimal\"/></xs:schema>")));

        Assert.Empty(XsdValidator.Validate("<amount>12.50</amount>", schemas));
        var invalid = Assert.Single(XsdValidator.Validate("<amount>dodici</amount>", schemas));
        Assert.Equal(1, invalid.Line);

        var external = "<!DOCTYPE amount [<!ENTITY e SYSTEM \"http://example.com/never\">]><amount>&e;</amount>";
        Assert.NotEmpty(XsdValidator.Validate(external, schemas));
    }

    private static IReadOnlyList<FiscalDocumentGrossLine> Lines(string scenario) => scenario switch
    {
        "ordinary-10" =>
        [
            new("TEST soggiorno", 1m, 300m, 10m, null),
            new("TEST pulizie", 1m, 50m, 10m, null),
            new("TEST imposta di soggiorno", 1m, 12m, 0m, "N1"),
        ],
        "flat-rate" =>
        [
            new("TEST soggiorno", 1m, 200m, 0m, "N2.2"),
            new("TEST pulizie", 1m, 40m, 0m, "N2.2"),
            new("TEST imposta di soggiorno", 1m, 8m, 0m, "N1"),
        ],
        "single-22" => [new("TEST soggiorno", 1m, 100m, 22m, null)],
        "remainder-22" => [new("TEST notte 1", 1m, 10.01m, 22m, null), new("TEST notte 2", 1m, 10.01m, 22m, null)],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static FatturaPaInput Input(string recipientName = "TEST Ospite S.r.l.", string city = "Roma")
    {
        var amounts = FiscalDocumentTotals.FromGross(Lines("ordinary-10"), Stamp);
        return new FatturaPaInput(
            new FatturaPaId("IT", "01234567897"),
            "A1B2C3D4E5",
            new FatturaPaParty(
                new FatturaPaId("IT", "01234567897"),
                "01234567897",
                "TEST Casa Vacanze S.r.l.",
                null,
                null,
                new FatturaPaAddress("Via di Test", "1", "00187", "Roma", "RM", "IT"),
                "RF01"),
            new FatturaPaParty(
                new FatturaPaId("IT", "09876543217"),
                null,
                recipientName,
                null,
                null,
                new FatturaPaAddress("Via del Cliente", "5", "82019", city, "BN", "IT")),
            null,
            null,
            "TD01",
            "2026/1",
            new DateOnly(2026, 11, 12),
            "EUR",
            amounts.Lines,
            amounts.Summary,
            amounts.StampDuty,
            amounts.Total,
            null);
    }
}
