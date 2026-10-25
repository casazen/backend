using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases.Reading;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.CfSimulator;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-DOCS-REAL: the simulated module of the guided service delivers what the real one delivers — the signed contract, the attestation of
/// conformity and the calculation sheet in ONE file that is a scan, and the receipt of the Agenzia delle Entrate as a digital PDF — so the test
/// of the whole round on the test environment (<c>e2e/gj-l3/concordato-round.spec.ts</c>) exercises the real format: the OCR, the sections, the
/// reading of each part and the reading of the receipt.
/// </summary>
public sealed class CfSimulatorRealFormatTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);
    private static readonly CfSimulatorHouse House = new("Via Sparano 12", "Bari", "70121", "Bari", "BA");

    [Fact]
    public async Task TheSampleDocument_HasItsThreeSectionsEachOnAPageOfItsOwn_AndTheFiguresAgree()
    {
        var pdf = new MigraDocPdfDocumentRenderer().Render(CfSimulatorDocuments.ContractWithAttestation(House, Today));

        var text = await new ContractTextReader().ReadAsync(pdf);
        var pages = text.PageTexts();
        var sections = ContractDocumentSections.Recognize(pages);

        // The text of the PDF is read in process (no OCR): the sections start at the top of a page and come in the order of the real file.
        Assert.Equal(0, text.PagesOcr);
        Assert.Equal(
            [DocumentSectionKind.Contract, DocumentSectionKind.Attestation, DocumentSectionKind.CalculationSheet],
            sections.Sections.Select(s => s.Kind));
        Assert.Equal(1, sections.Sections[0].FirstPage);
        Assert.False(sections.OutOfOrder);

        var contractText = ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Contract);
        var reading = LeaseContractTextParser.Parse(contractText, new LeaseContractParseContext(House.Address, House.City));
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent, reading.MonthlyRent!.Value);
        Assert.Equal(CfSimulatorDocuments.SampleSecurityDeposit, reading.SecurityDeposit!.Value);
        Assert.Equal(CfSimulatorDocuments.LandlordFiscalCode, Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord).FiscalCode);

        var attestation = AttestationReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Attestation));
        var sheet = CalculationSheetReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.CalculationSheet));
        Assert.Equal(ConformityVerdict.Conform, attestation.Verdict);
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent, attestation.MonthlyRent);
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent, sheet.MonthlyRent);
        Assert.Equal(ConformityVerdict.Conform, sheet.Verdict);

        // The sample is coherent with itself: nothing to warn about.
        var codes = ContractFiscalCodes.Find(contractText).Select(f => f.Code).ToList();
        Assert.Empty(ContractDocumentValidator.Validate(new ContractDocumentFacts(sections, reading, codes, 0, attestation, sheet, House.City)));
    }

    [OcrToolsFact]
    public async Task TheSampleDocument_AsAScan_IsReadByTheRealOcr_InItsThreeSections()
    {
        var digital = new MigraDocPdfDocumentRenderer().Render(CfSimulatorDocuments.ContractWithAttestation(House, Today));
        var scan = await ScannedPdf.FromAsync(digital);

        var text = await new ContractTextReader().ReadAsync(scan!);
        var pages = text.PageTexts();
        var sections = ContractDocumentSections.Recognize(pages);

        // Every page is a picture: all of them are read by OCR, and the sections are told apart by what the OCR read.
        Assert.NotNull(scan);
        Assert.Equal(text.Pages, text.PagesOcr);
        Assert.Equal(
            [DocumentSectionKind.Contract, DocumentSectionKind.Attestation, DocumentSectionKind.CalculationSheet],
            sections.Sections.Select(s => s.Kind));

        var contractText = ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Contract);
        var reading = LeaseContractTextParser.Parse(contractText, new LeaseContractParseContext(House.Address, House.City));
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent, reading.MonthlyRent!.Value);
        Assert.Equal(CfSimulatorDocuments.SampleSecurityDeposit, reading.SecurityDeposit!.Value);
        Assert.Equal(LeaseContractType.Concordato, reading.ContractType!.Value);
        Assert.Equal(CfSimulatorDocuments.LandlordFiscalCode, Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord).FiscalCode);
        Assert.Equal(CfSimulatorDocuments.TenantFiscalCode, Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant).FiscalCode);

        var attestation = AttestationReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Attestation));
        Assert.Equal(ConformityVerdict.Conform, attestation.Verdict);
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent, attestation.MonthlyRent);
    }

    [Fact]
    public async Task TheSampleReceipt_IsReadLikeTheOneOfTheAgenzia_ForTheDeclarationOfTheRegistration()
    {
        var pdf = new MigraDocPdfDocumentRenderer().Render(CfSimulatorDocuments.Receipt(House, "P-0123456789abcdef", Today));

        var text = await new ContractTextReader().ReadDigitalAsync(pdf);
        var receipt = RegistrationReceiptReader.Read(text!.Text);

        // A digital PDF: its text is there with no OCR, and it says what the Agenzia says.
        Assert.NotNull(text);
        Assert.Equal(ReceiptOutcome.Registered, receipt.Outcome);
        Assert.StartsWith("SIM26T", receipt.Code, StringComparison.Ordinal);
        Assert.Equal(Today, receipt.RegistrationDate);
        Assert.Equal(CfSimulatorDocuments.SampleMonthlyRent * 12m, receipt.AnnualRent);
        Assert.Equal(CfSimulatorDocuments.StartOf(Today), receipt.Start);
        Assert.Equal(CfSimulatorDocuments.EndOf(CfSimulatorDocuments.StartOf(Today)), receipt.End);
        Assert.Equal(LeaseContractType.Concordato, receipt.TypeHint);
        Assert.Equal("BARI", receipt.Comune);
        Assert.Equal("BA", receipt.Province);
        Assert.False(receipt.CedolareSecca);
    }
}
