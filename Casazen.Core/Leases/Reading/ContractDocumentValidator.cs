using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases.Reading;

/// <summary>
/// The stable codes of the things worth a second look that the reading of a contract document found (CF-DOCS-REAL). A warning never blocks
/// anything: the person goes on, and the screen puts each code in plain words (the server never writes the sentence).
/// </summary>
public static class ContractReadingWarnings
{
    /// <summary>Some pages hold no readable text (blank, too dark, or beyond the limit of pages read): <see cref="ReadingWarning.Pages"/> says which.</summary>
    public const string PagesUnreadable = "pages_unreadable";

    /// <summary>The sections come in an unusual order, or one of them is split by pages of another.</summary>
    public const string PagesOutOfOrder = "pages_out_of_order";

    /// <summary>A fiscal code is written in the contract that does not pass its check character: the person was not read from it.</summary>
    public const string FiscalCodeInvalid = "fiscal_code_invalid";

    /// <summary>The attestation says the rent does not conform to the territorial agreement.</summary>
    public const string AttestationNotConform = "attestation_not_conform";

    /// <summary>The attestation names a person (a fiscal code) the contract does not.</summary>
    public const string AttestationPartiesDiffer = "attestation_parties_differ";

    /// <summary>The rent the attestation attests is not the rent of the contract.</summary>
    public const string AttestationRentDiffers = "attestation_rent_differs";

    /// <summary>The rent of the contract is outside the lowest and highest rent the attestation gives for the house.</summary>
    public const string AttestationRentOutOfRange = "attestation_rent_out_of_range";

    /// <summary>The first day or the last day of the period in the attestation is not the contract's.</summary>
    public const string AttestationDatesDiffer = "attestation_dates_differ";

    /// <summary>The municipality of the attestation is not the one of the property.</summary>
    public const string AttestationComuneDiffers = "attestation_comune_differs";

    /// <summary>The sheet and the attestation do not give the same zone, surface or rent.</summary>
    public const string AttestationSheetDiffer = "attestation_sheet_differ";

    /// <summary>The calculation sheet says the rent does not conform to the territorial agreement.</summary>
    public const string SheetNotConform = "sheet_not_conform";

    /// <summary>The rent the calculation sheet works out is not the rent of the contract.</summary>
    public const string SheetRentDiffers = "sheet_rent_differs";

    /// <summary>A canone concordato lasts at least three years; the contract (or the attestation) says less.</summary>
    public const string TermTooShort = "term_too_short";
}

/// <summary>One warning: its code and, when it is about pages, which pages (counting from 1).</summary>
public sealed record ReadingWarning(string Code, IReadOnlyList<int>? Pages = null);

/// <summary>What was read of a contract document, to be checked for coherence: the sections, what each reader found and the property of the draft.</summary>
/// <param name="Sections">Where the sections are in the pages.</param>
/// <param name="Contract">What the reader of the contract found in the pages of the contract.</param>
/// <param name="ContractFiscalCodes">The valid fiscal codes in the pages of the contract.</param>
/// <param name="InvalidFiscalCodes">How many strings shaped like a fiscal code in the contract fail its check character.</param>
/// <param name="Attestation">The attestation, or <c>null</c> when the document has none.</param>
/// <param name="Sheet">The calculation sheet, or <c>null</c> when the document has none.</param>
/// <param name="PropertyCity">The city of the property of the draft, when known.</param>
public sealed record ContractDocumentFacts(
    RecognizedSections Sections,
    LeaseContractReading Contract,
    IReadOnlyList<string> ContractFiscalCodes,
    int InvalidFiscalCodes,
    AttestationReading? Attestation,
    CalculationSheetReading? Sheet,
    string? PropertyCity);

/// <summary>
/// Checks that the sections of a contract document agree with each other and with the draft (CF-DOCS-REAL): the contract and its
/// attestation speak of the same people, the same dates and the same town, the rent of the contract is the attested one and is within the
/// values of the territorial agreement, the sheet gives the same figures, and every page could be read. Pure function: the answer is codes.
/// </summary>
public static partial class ContractDocumentValidator
{
    /// <summary>Two rents that differ by less than this (the cents of a division) are the same.</summary>
    private const decimal RentTolerance = 0.5m;

    /// <summary>The first period of a canone concordato (3+2) is at least 36 months.</summary>
    private const int ConcordatoMinimumMonths = 36;

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]{6}\d{2}[A-Za-z]\d{2}[A-Za-z]\d{3}[A-Za-z](?![A-Za-z0-9])", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex FiscalCodeShape();

    /// <summary>How many strings shaped like a fiscal code the text holds that are not among the valid ones (<see cref="ContractFiscalCodes"/>).</summary>
    public static int CountInvalidFiscalCodes(string? text, IReadOnlyList<string> valid)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return FiscalCodeShape().Matches(text)
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count(code => !valid.Contains(code, StringComparer.Ordinal));
    }

    public static IReadOnlyList<ReadingWarning> Validate(ContractDocumentFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var warnings = new List<ReadingWarning>();
        if (facts.Sections.UnreadablePages.Count > 0)
            warnings.Add(new ReadingWarning(ContractReadingWarnings.PagesUnreadable, facts.Sections.UnreadablePages));
        if (facts.Sections.OutOfOrder)
            warnings.Add(new ReadingWarning(ContractReadingWarnings.PagesOutOfOrder));
        if (facts.InvalidFiscalCodes > 0)
            warnings.Add(new ReadingWarning(ContractReadingWarnings.FiscalCodeInvalid));

        var rent = facts.Contract.MonthlyRent is { IsAccepted: true } r ? r.Value : (decimal?)null;
        var start = facts.Contract.StartDate is { IsAccepted: true } s ? s.Value : (DateOnly?)null;
        var end = facts.Contract.EndDate is { IsAccepted: true, Derived: false } e ? e.Value : (DateOnly?)null;
        var term = facts.Contract.TermMonths is { IsAccepted: true } t ? t.Value : (int?)null;
        var concordato = facts.Contract.ContractType is { IsAccepted: true, Value: LeaseContractType.Concordato };

        if (facts.Attestation is { } attestation)
            CheckAttestation(attestation, facts, rent, start, end, warnings);

        if (facts.Sheet is { } sheet)
        {
            if (sheet.Verdict == ConformityVerdict.NotConform)
                warnings.Add(new ReadingWarning(ContractReadingWarnings.SheetNotConform));
            if (rent is { } contractRent && sheet.MonthlyRent is { } sheetRent && Differ(contractRent, sheetRent))
                warnings.Add(new ReadingWarning(ContractReadingWarnings.SheetRentDiffers));
        }

        if (facts.Attestation is { } a && facts.Sheet is { } sh && SectionsDiffer(a, sh))
            warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationSheetDiffer));

        var attestedTerm = facts.Attestation?.TermMonths;
        if ((concordato || facts.Attestation is not null) && (term ?? attestedTerm) is { } months && months < ConcordatoMinimumMonths)
            warnings.Add(new ReadingWarning(ContractReadingWarnings.TermTooShort));

        return warnings;
    }

    private static void CheckAttestation(
        AttestationReading attestation,
        ContractDocumentFacts facts,
        decimal? rent,
        DateOnly? start,
        DateOnly? end,
        List<ReadingWarning> warnings)
    {
        if (attestation.Verdict == ConformityVerdict.NotConform)
            warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationNotConform));

        if (attestation.FiscalCodes.Any(code => !facts.ContractFiscalCodes.Contains(code, StringComparer.Ordinal)) && facts.ContractFiscalCodes.Count > 0)
            warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationPartiesDiffer));

        if (rent is { } contractRent)
        {
            if (attestation.MonthlyRent is { } attested && Differ(contractRent, attested))
                warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationRentDiffers));
            if (attestation.RangeMinimum is { } low && attestation.RangeMaximum is { } high
                && (contractRent < low - RentTolerance || contractRent > high + RentTolerance))
            {
                warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationRentOutOfRange));
            }
        }

        if ((start is { } contractStart && attestation.Start is { } attestedStart && contractStart != attestedStart)
            || (end is { } contractEnd && attestation.End is { } attestedEnd && contractEnd != attestedEnd))
        {
            warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationDatesDiffer));
        }

        if (attestation.Comune is { } comune && facts.PropertyCity is { Length: > 0 } city && !RegistrationReceiptCheck.SameTown(comune, city))
            warnings.Add(new ReadingWarning(ContractReadingWarnings.AttestationComuneDiffers));
    }

    private static bool SectionsDiffer(AttestationReading attestation, CalculationSheetReading sheet) =>
        (attestation.MonthlyRent is { } a && sheet.MonthlyRent is { } b && Differ(a, b))
        || (attestation.Zone is { } za && sheet.Zone is { } zb && za != zb)
        || (attestation.SubBand is { } sa && sheet.SubBand is { } sb && sa != sb)
        || (attestation.Surface is { } ua && sheet.Surface is { } ub && ua != ub);

    private static bool Differ(decimal a, decimal b) => Math.Abs(a - b) > RentTolerance;
}
