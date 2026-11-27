using System.Security.Cryptography;
using System.Xml.Linq;
using System.Xml.Schema;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F01/US02/T03, E17/F01/US01/T03 (NF-83, NF-87, NF-116): the amounts of a guest document are computed in one place and are cent-accurate, the XML is written
/// from the real data with every text escaped, and every document the builder writes is validated against the official FatturaPA schema embedded in the build
/// (FPR12 1.2.3 and the XML signature schema it imports, their SHA-256 checked here): the invoice of an owner company with the agency as third-party issuer (TZ),
/// a foreign private recipient (XXXXXXX), a credit note and a self-billed TD17. The validator reads no DTD, no external entity and no network.
/// </summary>
public class FatturaPaProofTests
{
    private static readonly FiscalStampDutyRule Stamp = new(77.47m, 2.00m, ChargedToRecipient: false);

    private static readonly FatturaPaXmlBuilder Builder = new(new FatturaPaSchemaProvider(), NullLogger<FatturaPaXmlBuilder>.Instance);

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

    /// <summary>Every text is escaped and the document still validates against the official schema (names and towns with accents and apostrophes).</summary>
    [Fact]
    public void FatturaPa_SpecialCharactersEscaped_Test()
    {
        var input = Input(recipientName: "Rossi & Figli <S.r.l.> \"Àèìòù\"", city: "Sant'Agata de' Goti");

        var xml = Builder.Build(input);

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
    /// The official schema is in the build (E17/F01/US01/T03): a built document validates and an altered one does not, never an XML that was not validated.
    /// </summary>
    [Fact]
    public void FatturaPa_Xml_ValidatesAgainstOfficialSchema_Test()
    {
        Assert.True(FatturaPaSchemaProvider.HasOfficialSchema);

        var xml = Builder.Build(Input());
        Assert.Empty(XsdValidator.Validate(xml, new FatturaPaSchemaProvider().GetSchemas()));

        var altered = XDocument.Parse(xml);
        altered.Descendants("Numero").Single().Remove();
        Assert.NotEmpty(XsdValidator.Validate(altered.ToString(), new FatturaPaSchemaProvider().GetSchemas()));
        var refused = Assert.Throws<DomainRuleException>(() => Builder.Build(Input() with { Number = new string('9', 21) }));
        Assert.Equal(FatturaPaErrors.XmlInvalid, refused.Code);
    }

    /// <summary>
    /// The embedded schemas are the files the publishers give, byte for byte: the SHA-256 recorded in the runbook (docs/runbooks/fiscal-documents.md), the FatturaPA
    /// target namespace compiled with the signature schema it imports, nothing fetched.
    /// </summary>
    [Fact]
    public void FatturaPaSchemaProvider_EmbeddedSchemas_AreTheOfficialFiles_Test()
    {
        var assembly = typeof(FatturaPaSchemaProvider).Assembly;
        Assert.Equal(
            FatturaPaSchemaProvider.OfficialFiles.Select(f => FatturaPaSchemaProvider.ResourcePrefix + f.FileName).Order(StringComparer.Ordinal),
            FatturaPaSchemaProvider.EmbeddedSchemaNames());
        foreach (var file in FatturaPaSchemaProvider.OfficialFiles)
        {
            using var stream = assembly.GetManifestResourceStream(FatturaPaSchemaProvider.ResourcePrefix + file.FileName)!;
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        var schemas = new FatturaPaSchemaProvider().GetSchemas();
        Assert.True(schemas.Contains(FatturaPaSchemaProvider.FatturaPaNamespace));
        Assert.True(schemas.Contains("http://www.w3.org/2000/09/xmldsig#"));
    }

    /// <summary>
    /// PROOF TEST: data rule (E17/F01/US01/T03). The invoice of an owner company issued by CasaZen for the agency: the company is the cedente (its VAT number, its
    /// regime), the agency is the third-party issuer (<c>TerzoIntermediarioOSoggettoEmittente</c> with its tax ids and <c>SoggettoEmittente</c> <c>TZ</c>) and the
    /// transmitter, and the XML validates against the official schema.
    /// </summary>
    [Fact]
    public void FatturaPa_OwnerCompanyWithThirdPartyIssuer_ValidXsd_Test()
    {
        var document = Document(DocumentKind.Invoice, "SRL2026/1");
        var company = new OrgFiscalIssuerProfile
        {
            OrgId = document.OrgId,
            PropertyOwnerId = Guid.NewGuid(),
            DocumentRegime = DocumentRegime.VatOrdinary,
            LegalName = "TEST Immobiliare Lago Srl",
            VatNumber = "01234567897",
            FiscalCode = "01234567897",
            AddressStreet = "Via del Lago",
            AddressCivic = "3",
            PostalCode = "22100",
            City = "Como",
            Province = "CO",
            CountryCode = "IT",
        };
        var agency = new OrgEntity
        {
            Id = document.OrgId,
            Name = "TEST Agenzia",
            Slug = "test-agenzia-tz",
            DisplayName = "TEST Agenzia",
            ContactEmail = "agenzia@example.com",
            HasPartitaIva = true,
            PartitaIvaNumber = "09876543217",
            FiscalCode = "09876543217",
        };
        var snapshot = FiscalIssuerSnapshot.From(company, agency, "RF01") with
        {
            Intermediary = new FiscalIssuerIntermediary("TEST Agenzia Lago Srl", "09876543217", "09876543217", "IT"),
        };
        var rule = TaxRuleCatalogs.Resolver().Resolve("IT", DocumentRegime.VatOrdinary, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2026, 11, 4));

        var input = FiscalDocumentService.FatturaPaInputOf(document, snapshot, rule, credited: null);
        var xml = Builder.Build(input);

        var parsed = XDocument.Parse(xml);
        var cedente = parsed.Descendants("CedentePrestatore").Single();
        Assert.Equal("01234567897", cedente.Descendants("IdCodice").Single().Value);
        Assert.Equal("TEST Immobiliare Lago Srl", cedente.Descendants("Denominazione").Single().Value);
        Assert.Equal("RF01", cedente.Descendants("RegimeFiscale").Single().Value);
        var third = parsed.Descendants("TerzoIntermediarioOSoggettoEmittente").Single();
        Assert.Equal(("IT", "09876543217"), (third.Descendants("IdPaese").Single().Value, third.Descendants("IdCodice").Single().Value));
        Assert.Equal("TEST Agenzia Lago Srl", third.Descendants("Denominazione").Single().Value);
        Assert.Equal(FatturaPaXmlBuilder.ThirdPartyIssuerCode, parsed.Descendants("SoggettoEmittente").Single().Value);
        Assert.Equal("09876543217", parsed.Descendants("IdTrasmittente").Single().Element("IdCodice")!.Value);
        Assert.Empty(XsdValidator.Validate(xml, new FatturaPaSchemaProvider().GetSchemas()));

        // The agency's own invoice names no third party.
        var own = FiscalDocumentService.FatturaPaInputOf(document, FiscalIssuerSnapshot.From(new OrgFiscalIssuerProfile { OrgId = document.OrgId, CountryCode = "IT" }, agency, "RF01"), rule, null);
        Assert.Equal(FatturaPaIssuedBy.Issuer, own.IssuedBy);
        Assert.Empty(XDocument.Parse(FatturaPaXmlBuilder.Write(own)).Descendants("SoggettoEmittente"));
    }

    /// <summary>
    /// A private recipient abroad (B2C): <c>CodiceDestinatario</c> <c>XXXXXXX</c>, the country and the code of a foreign party without a VAT number (eleven nines),
    /// <c>CAP</c> 00000 and no province; a credit note names the invoice it credits. Both validate against the official schema.
    /// </summary>
    [Fact]
    public void FatturaPa_ForeignPrivateRecipient_AndCreditNote_ValidXsd()
    {
        var input = Input() with
        {
            Recipient = new FatturaPaParty(
                new FatturaPaId("DE", FatturaPaParties.ForeignPrivateCode),
                null,
                null,
                "TEST Hans",
                "Gast",
                new FatturaPaAddress("Hauptstrasse", "1", "10115", "Berlin", "BE", "DE")),
        };

        var xml = Builder.Build(input);
        var parsed = XDocument.Parse(xml);
        Assert.Equal(FatturaPaXmlBuilder.ForeignRecipientCode, parsed.Descendants("CodiceDestinatario").Single().Value);
        var recipient = parsed.Descendants("CessionarioCommittente").Single();
        Assert.Equal(("DE", FatturaPaParties.ForeignPrivateCode), (recipient.Descendants("IdPaese").Single().Value, recipient.Descendants("IdCodice").Single().Value));
        Assert.Equal(FatturaPaParties.ForeignPostalCode, recipient.Descendants("CAP").Single().Value);
        Assert.Empty(recipient.Descendants("Provincia"));

        var note = Builder.Build(Input() with { DocumentTypeCode = "TD04", Number = "2026/2", RelatedDocument = new FatturaPaRelatedDocument("2026/1", new DateOnly(2026, 11, 12)) });
        Assert.Equal("2026/1", XDocument.Parse(note).Descendants("IdDocumento").Single().Value);
    }

    /// <summary>
    /// TD17 (E17/F01/US03): the self-billed invoice of a service bought abroad is issued by its recipient (<c>SoggettoEmittente</c> <c>CC</c>), with the foreign
    /// supplier as cedente (its country and VAT number, regime RF18, an address abroad) and the supplier's invoice as the related document; it validates.
    /// </summary>
    [Fact]
    public void FatturaPa_SelfBillingTD17_ValidXsd()
    {
        var amounts = FiscalDocumentTotals.FromGross([new FiscalDocumentGrossLine("TEST commissione del portale", 1m, 122m, 22m, null, FiscalLineType.Commission)], null);
        var input = new FatturaPaInput(
            new FatturaPaId("IT", "01234567897"),
            "TD17TEST01",
            new FatturaPaParty(
                new FatturaPaId("NL", "805734958B01"),
                null,
                "TEST Portale B.V.",
                null,
                null,
                new FatturaPaAddress("Herengracht", "597", "1017 CE", "Amsterdam", null, "NL"),
                "RF18"),
            new FatturaPaParty(
                new FatturaPaId("IT", "01234567897"),
                "01234567897",
                "TEST Immobiliare Lago Srl",
                null,
                null,
                new FatturaPaAddress("Via del Lago", "3", "22100", "Como", "CO", "IT")),
            "ABC1234",
            null,
            "TD17",
            "AF2026/1",
            new DateOnly(2026, 11, 30),
            "EUR",
            amounts.Lines,
            amounts.Summary,
            amounts.StampDuty,
            amounts.Total,
            new FatturaPaRelatedDocument("TEST-INV-2026-11", new DateOnly(2026, 11, 30)))
        {
            IssuedBy = FatturaPaIssuedBy.Recipient,
        };

        var xml = Builder.Build(input);

        var parsed = XDocument.Parse(xml);
        Assert.Equal(FatturaPaXmlBuilder.RecipientIssuerCode, parsed.Descendants("SoggettoEmittente").Single().Value);
        Assert.Equal("TD17", parsed.Descendants("TipoDocumento").Single().Value);
        Assert.Equal(FatturaPaParties.ForeignPostalCode, parsed.Descendants("CedentePrestatore").Single().Descendants("CAP").Single().Value);
        Assert.Empty(parsed.Descendants("TerzoIntermediarioOSoggettoEmittente"));
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

    /// <summary>An issued invoice of a stay with the lines of «ordinary-10» and an Italian company as recipient.</summary>
    private static FiscalDocument Document(DocumentKind kind, string number)
    {
        var document = new FiscalDocument
        {
            OrgId = Guid.NewGuid(),
            Kind = kind,
            Status = FiscalDocumentStatus.Issued,
            Number = number,
            IssueDate = new DateOnly(2026, 11, 12),
            Currency = "EUR",
            RecipientKind = InvoiceRecipientKind.Company,
            RecipientName = "TEST Cliente Srl",
            RecipientVatNumber = "IT09876543217",
            RecipientAddressStreet = "Via del Cliente",
            RecipientAddressCivic = "5",
            RecipientPostalCode = "00100",
            RecipientCity = "Roma",
            RecipientProvince = "RM",
            RecipientCountryCode = "IT",
            RecipientSdiCode = "ABC1234",
        };
        FiscalDocumentService.ApplyAmounts(document, FiscalDocumentTotals.FromGross(Lines("ordinary-10"), Stamp));
        return document;
    }

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
