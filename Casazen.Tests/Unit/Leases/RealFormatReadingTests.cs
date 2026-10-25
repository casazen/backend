using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases.Reading;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// CF-DOCS-REAL: the readers on a synthetic document in the format of the real one (<see cref="RealFormatDocument"/>): the contract, the
/// attestation of conformity and the calculation sheet in one file, and the receipt of the registration of the Agenzia delle Entrate. One small
/// proof for each rule: the sections are told apart (also with the mistakes of an OCR), each part is read on its own pages, what disagrees is
/// said with a code, and a discarded request is not a registration.
/// </summary>
public sealed class RealFormatReadingTests
{
    private static readonly LeaseContractParseContext House = new("Via Verdi 12", "Bollate");

    // ---- the sections ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDocument_IsSplitInItsContract_ItsAttestation_AndItsSheet()
    {
        var sections = ContractDocumentSections.Recognize(RealFormatDocument.Pages());

        Assert.Equal(
            [(DocumentSectionKind.Contract, 1, 6), (DocumentSectionKind.Attestation, 7, 10), (DocumentSectionKind.CalculationSheet, 11, 14)],
            sections.Sections.Select(s => (s.Kind, s.FirstPage, s.LastPage)));
        Assert.True(sections.ContainsAttestation);
        Assert.True(sections.ContainsCalculationSheet);
        Assert.False(sections.OutOfOrder);
        Assert.Empty(sections.UnreadablePages);
    }

    [Fact]
    public void TheMistakesOfAnOcr_DoNotMoveTheSections()
    {
        // A wrong letter in each title, the watermark of the sample at the top or at the bottom of every page.
        var pages = RealFormatDocument.Pages().Select((page, index) =>
        {
            var text = page
                .Replace("Attestazione di Rispondenza", "Attestazlone di Rispondenza", StringComparison.Ordinal)
                .Replace("Scheda Riepilogativa Canone", "Scheda Riepllogativa Canone", StringComparison.Ordinal)
                .Replace("Contratto di Locazione Abitativa Agevolata", "Contratto dl Locazione Abitativa Agevolata", StringComparison.Ordinal);
            return index % 2 == 0 ? "ESEMPIO OSCURATO PRIVO DI VALORE\n" + text : text + "\nESEMPIO OSCURATO PRIVO DI VALORE";
        }).ToArray();

        var sections = ContractDocumentSections.Recognize(pages);

        Assert.Equal(
            [(DocumentSectionKind.Contract, 1, 6), (DocumentSectionKind.Attestation, 7, 10), (DocumentSectionKind.CalculationSheet, 11, 14)],
            sections.Sections.Select(s => (s.Kind, s.FirstPage, s.LastPage)));
    }

    [Fact]
    public void AContractWithNoAttestation_IsOneContract_NoSectionFoundIsNotAnError_AndABlankPageIsSaid()
    {
        var pages = RealFormatDocument.Pages().Take(6).ToArray();
        pages[3] = string.Empty;

        var sections = ContractDocumentSections.Recognize(pages);

        var only = Assert.Single(sections.Sections);
        Assert.Equal((DocumentSectionKind.Contract, 1, 6), (only.Kind, only.FirstPage, only.LastPage));
        Assert.False(sections.ContainsAttestation);
        Assert.False(sections.ContainsCalculationSheet);
        Assert.Equal([4], sections.UnreadablePages);
    }

    // ---- each part on its own pages ---------------------------------------------------------------------------------------

    [Fact]
    public void TheContract_IsReadOnItsOwnPages_AndNotConfusedByTheDatesOfItsDocumentsOrByTheOtherSections()
    {
        var pages = RealFormatDocument.Pages();
        var sections = ContractDocumentSections.Recognize(pages);

        var reading = LeaseContractTextParser.Parse(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Contract), House);

        Assert.Equal(LeaseContractType.Concordato, reading.ContractType!.Value);
        Assert.Equal(LeaseTaxRegime.CedolareSecca, reading.TaxRegime!.Value);
        Assert.Equal(595m, reading.MonthlyRent!.Value);
        Assert.Equal(1785m, reading.SecurityDeposit!.Value);

        // The passport of the tenant and the energy certificate of the house expire on the first page: not the end of the lease.
        Assert.Equal(new DateOnly(2026, 9, 19), reading.StartDate!.Value);
        Assert.Equal(new DateOnly(2029, 9, 18), reading.EndDate!.Value);
        Assert.Equal(36, reading.TermMonths!.Value);

        // "Letto, approvato e sottoscritto. Bollate, li 19-9-26" is the signature; the territorial agreement "sottoscritto in Milano il 20 maggio 2025" is not.
        Assert.Equal(new DateOnly(2026, 9, 19), reading.StipulaDate!.Value);
        Assert.Equal(PropertyAddressMatch.Match, reading.Property!.Value);

        // A woman who rents ("denominata conduttrice") is a tenant.
        var landlord = Assert.Single(reading.Parties, p => p.Role == PartyRole.Landlord);
        var tenant = Assert.Single(reading.Parties, p => p.Role == PartyRole.Tenant);
        Assert.Equal(RealFormatDocument.LandlordFiscalCode, landlord.FiscalCode);
        Assert.Equal("Giulia", tenant.FirstName);
        Assert.Equal("Verdi", tenant.LastName);
        Assert.Equal(RealFormatDocument.TenantFiscalCode, tenant.FiscalCode);
        Assert.Equal("IT", tenant.Citizenship);
    }

    [Fact]
    public void TheAttestationAndTheSheet_AreRead_AndAgreeWithTheContract()
    {
        var pages = RealFormatDocument.Pages();
        var sections = ContractDocumentSections.Recognize(pages);
        var contractText = ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Contract);
        var reading = LeaseContractTextParser.Parse(contractText, House);

        var attestation = AttestationReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Attestation));
        var sheet = CalculationSheetReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.CalculationSheet));

        Assert.Equal("Bollate", attestation.Comune);
        Assert.Equal("MI", attestation.Province);
        Assert.Equal("Garbagnate Milanese", attestation.AgreementArea);
        Assert.Equal(new DateOnly(2025, 5, 20), attestation.AgreementDate);
        Assert.Equal("FEDERCASA.CONFSAL", attestation.TenantsOrganization);
        Assert.Contains("Confproprietà", attestation.OwnersOrganization, StringComparison.Ordinal);
        Assert.Equal(ConformityVerdict.Conform, attestation.Verdict);
        Assert.Equal(595m, attestation.MonthlyRent);
        Assert.Equal(594m, attestation.RangeMinimum);
        Assert.Equal(726m, attestation.RangeMaximum);
        Assert.Equal("1", attestation.Zone);
        Assert.Equal(3, attestation.SubBand);
        Assert.Equal(66m, attestation.Surface);
        Assert.Equal(25, attestation.Points);
        Assert.Equal(new DateOnly(2026, 9, 19), attestation.Start);
        Assert.Equal(new DateOnly(2029, 9, 18), attestation.End);
        Assert.Equal(36, attestation.TermMonths);
        Assert.Equal<string>([RealFormatDocument.LandlordFiscalCode, RealFormatDocument.TenantFiscalCode], attestation.FiscalCodes);

        Assert.Equal("1", sheet.Zone);
        Assert.Equal("B1", sheet.OmiZone);
        Assert.Equal(3, sheet.SubBand);
        Assert.Equal(66m, sheet.Surface);
        Assert.Equal(595m, sheet.MonthlyRent);
        Assert.Equal(594m, sheet.RangeMinimum);
        Assert.Equal(726m, sheet.RangeMaximum);
        Assert.Equal(ConformityVerdict.Conform, sheet.Verdict);

        var codes = ContractFiscalCodes.Find(contractText).Select(f => f.Code).ToList();
        var warnings = ContractDocumentValidator.Validate(new ContractDocumentFacts(sections, reading, codes, 0, attestation, sheet, "Bollate"));
        Assert.Empty(warnings);
    }

    // ---- what disagrees is said with a code -----------------------------------------------------------------------------------

    [Fact]
    public void WhatDisagrees_IsSaidWithStableCodes_AndNeverBlocks()
    {
        var pages = RealFormatDocument.Pages();
        var sections = ContractDocumentSections.Recognize(pages);
        var contractText = ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Contract);
        var reading = LeaseContractTextParser.Parse(contractText, House);
        var codes = ContractFiscalCodes.Find(contractText).Select(f => f.Code).ToList();
        var attestation = AttestationReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.Attestation));
        var sheet = CalculationSheetReader.Read(ContractDocumentSections.TextOf(pages, sections, DocumentSectionKind.CalculationSheet));

        // A contract that asks 800 a month where the attestation attests 595, between values that stop at 726, and the sheet works out 595.
        var dearer = pages.ToArray();
        dearer[1] = dearer[1].Replace("595,00", "800,00", StringComparison.Ordinal).Replace("7.140,00", "9.600,00", StringComparison.Ordinal);
        var dearerText = ContractDocumentSections.TextOf(dearer, ContractDocumentSections.Recognize(dearer), DocumentSectionKind.Contract);
        var dearerReading = LeaseContractTextParser.Parse(dearerText, House);
        Assert.Equal(800m, dearerReading.MonthlyRent!.Value);
        var found = ContractDocumentValidator.Validate(new ContractDocumentFacts(sections, dearerReading, codes, 0, attestation, sheet, "Bollate")).Select(w => w.Code).ToList();
        Assert.Contains(ContractReadingWarnings.AttestationRentDiffers, found);
        Assert.Contains(ContractReadingWarnings.AttestationRentOutOfRange, found);
        Assert.Contains(ContractReadingWarnings.SheetRentDiffers, found);

        // An attestation of another town, which does not give the figures the sheet gives.
        var other = pages.ToArray();
        for (var page = 6; page < 10; page++)
            other[page] = other[page].Replace("Comune di Bollate", "Comune di Rho", StringComparison.Ordinal).Replace("66,00", "70,00", StringComparison.Ordinal);
        var otherAttestation = AttestationReader.Read(ContractDocumentSections.TextOf(other, ContractDocumentSections.Recognize(other), DocumentSectionKind.Attestation));
        var otherFound = ContractDocumentValidator.Validate(new ContractDocumentFacts(sections, reading, codes, 0, otherAttestation, sheet, "Bollate")).Select(w => w.Code).ToList();
        Assert.Contains(ContractReadingWarnings.AttestationComuneDiffers, otherFound);
        Assert.Contains(ContractReadingWarnings.AttestationSheetDiffer, otherFound);

        // A page nobody can read is said with its number, and the sections around it stay.
        var blank = pages.ToArray();
        blank[8] = string.Empty;
        var unreadable = ContractDocumentValidator.Validate(new ContractDocumentFacts(ContractDocumentSections.Recognize(blank), reading, codes, 0, null, null, "Bollate"));
        var warning = Assert.Single(unreadable);
        Assert.Equal(ContractReadingWarnings.PagesUnreadable, warning.Code);
        Assert.Equal([9], warning.Pages);
    }

    // ---- the receipt of the Agenzia delle Entrate ------------------------------------------------------------------------------

    [Fact]
    public void TheReceipt_IsRead_ForTheDeclarationOfTheRegistration_AndComparedWithTheLease()
    {
        var receipt = RegistrationReceiptReader.Read(RealFormatDocument.Receipt());

        Assert.Equal(ReceiptOutcome.Registered, receipt.Outcome);
        Assert.Equal("TKM26T012345000AB", receipt.Code);
        Assert.Equal("012345", receipt.Number);
        Assert.Equal("3T", receipt.Series);
        Assert.Equal(new DateOnly(2026, 9, 21), receipt.RegistrationDate);
        Assert.Equal("MILANO 1", receipt.Office);
        Assert.Equal(7140m, receipt.AnnualRent);
        Assert.Equal(new DateOnly(2026, 9, 19), receipt.Start);
        Assert.Equal(new DateOnly(2029, 9, 18), receipt.End);
        Assert.Equal(new DateOnly(2026, 9, 19), receipt.StipulationDate);
        Assert.Equal("Locazione agevolata immobile uso abitativo", receipt.Typology);
        Assert.Equal(LeaseContractType.Concordato, receipt.TypeHint);
        Assert.True(receipt.CedolareSecca);
        Assert.Equal("BOLLATE", receipt.Comune);
        Assert.Equal("MI", receipt.Province);

        var lease = new ReceiptLeaseFacts(595m, new DateOnly(2026, 9, 19), new DateOnly(2029, 9, 18), new DateOnly(2026, 9, 19), LeaseContractType.Concordato, LeaseTaxRegime.CedolareSecca, "Bollate");
        Assert.Empty(RegistrationReceiptCheck.Compare(receipt, lease));

        // A mismatch is a warning with a code, never a block.
        var other = lease with { MonthlyRent = 600m, Start = new DateOnly(2026, 10, 1), City = "Rho", TaxRegime = LeaseTaxRegime.Ordinario };
        Assert.Equal(
            [RegistrationReceiptWarnings.RentDiffers, RegistrationReceiptWarnings.StartDiffers, RegistrationReceiptWarnings.ComuneDiffers, RegistrationReceiptWarnings.RegimeDiffers],
            RegistrationReceiptCheck.Compare(receipt, other));
    }

    [Fact]
    public void ARequestTheAgenziaDiscarded_IsSaidClearly_AndIsNotARegistration()
    {
        var receipt = RegistrationReceiptReader.Read(RealFormatDocument.Receipt(discarded: true));

        Assert.Equal(ReceiptOutcome.Rejected, receipt.Outcome);
        Assert.Null(receipt.Code);
        Assert.Null(receipt.RegistrationDate);
        Assert.Equal(ReceiptOutcome.Unknown, RegistrationReceiptReader.Read("Contratto di locazione tra le parti, canone 595,00").Outcome);
    }
}
