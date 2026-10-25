using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Core.Leases;

/// <summary>
/// How the values typed in the wizard are read (LR-12): which ones are valid, and what a valid one means. Pure functions, no
/// database: <see cref="LeaseDraftProgress"/> and <see cref="LeaseReview"/> build on them, and the draft service uses the same
/// reading to write a valid value into its column. The rules are the ones the complete creation of a lease already applies
/// (<see cref="LeasePartyRules"/>, <see cref="ItalianFiscalCode"/>, <see cref="LeaseContractTerms"/>) and the ones in
/// <see cref="LongRentRules"/>; nothing here is a new figure of law.
/// </summary>
public static partial class LeaseDraftRules
{
    /// <summary>
    /// The longest text accepted for any field of the draft: a limit on the size of the request, not a rule about the value (a
    /// residence of 300 characters is stored and reported as not valid; one of 600 is refused).
    /// </summary>
    public const int MaxTextLength = 500;

    /// <summary>The highest rent or deposit accepted, in euros (as on the complete creation of a lease).</summary>
    public const decimal MaxAmount = 1_000_000m;

    /// <summary>The longest documentation of the need of a transitory contract (the need itself has <see cref="MaxTextLength"/>, the limit of the request).</summary>
    public const int TransitoryDocumentationMaxLength = 300;

    /// <summary>Earliest and latest date accepted for the start of a contract and for the verification of a document.</summary>
    public static readonly DateOnly MinDate = new(1970, 1, 1);

    public static readonly DateOnly MaxDate = new(2100, 12, 31);

    // ---- text ----------------------------------------------------------------------------------------------------

    /// <summary>The text trimmed, or <c>null</c> when it is empty.</summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // ---- type, regime, dates, amounts ----------------------------------------------------------------------------

    /// <summary>The contract type named by <paramref name="value"/> (case ignored), or <c>null</c> when it names none of the types.</summary>
    public static LeaseContractType? ParseContractType(string? value) =>
        Enum.TryParse<LeaseContractType>(Clean(value), ignoreCase: true, out var type) && Enum.IsDefined(type) ? type : null;

    /// <summary>The tax regime named by <paramref name="value"/> (case ignored), or <c>null</c>.</summary>
    public static LeaseTaxRegime? ParseTaxRegime(string? value) =>
        Enum.TryParse<LeaseTaxRegime>(Clean(value), ignoreCase: true, out var regime) && Enum.IsDefined(regime) ? regime : null;

    /// <summary>
    /// The party that has the need of a transitory contract (<c>Landlord</c> or <c>Tenant</c>, case ignored), or <c>null</c> when
    /// <paramref name="value"/> names neither.
    /// </summary>
    public static PartyRole? ParseNeedParty(string? value) =>
        Enum.TryParse<PartyRole>(Clean(value), ignoreCase: true, out var party) && Enum.IsDefined(party) ? party : null;

    /// <summary>
    /// True when the term of a contract of this <paramref name="type"/> is the one its landlord types (its <c>endDate</c>) and not the one
    /// the type gives: a contract already signed states its own term, whatever the type, and a transitory one made in CasaZen has a term
    /// of its own from 1 to 18 months. The libero and the concordato made in CasaZen follow their type.
    /// </summary>
    public static bool StatesItsTerm(LeaseContractType? type, bool typeAvailable, LeaseOrigin origin) =>
        origin == LeaseOrigin.Imported || (typeAvailable && type == LeaseContractType.Transitorio);

    /// <summary>A calendar date written <c>yyyy-MM-dd</c> between <see cref="MinDate"/> and <see cref="MaxDate"/>, or <c>null</c>.</summary>
    public static DateOnly? ParseDate(string? value)
    {
        if (!DateOnly.TryParseExact(Clean(value), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;

        return date >= MinDate && date <= MaxDate ? date : null;
    }

    /// <summary>The date as the lease stores it: midnight UTC of the calendar day (storage convention, FD-06).</summary>
    public static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>A rent: more than 0, at most <see cref="MaxAmount"/>, with cents at most.</summary>
    public static bool IsValidRent(decimal? value) =>
        value is > 0m and <= MaxAmount && HasAtMostCents(value.Value);

    /// <summary>A deposit: 0 or more, at most <see cref="MaxAmount"/>, with cents at most.</summary>
    public static bool IsValidDeposit(decimal? value) =>
        value is >= 0m and <= MaxAmount && HasAtMostCents(value.Value);

    private static bool HasAtMostCents(decimal value) => decimal.Round(value, 2) == value;

    // ---- parties -------------------------------------------------------------------------------------------------

    private static readonly EmailAddressAttribute EmailRule = new();

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex TwoLettersRule();

    /// <summary>True when nothing was typed in the entry (a blank row of the form): it is not kept.</summary>
    public static bool IsBlank(LeaseDraftParty party) =>
        Clean(party.FirstName) is null
        && Clean(party.LastName) is null
        && Clean(party.FiscalCode) is null
        && Clean(party.Citizenship) is null
        && Clean(party.Residence) is null
        && Clean(party.ContactEmail) is null
        && Clean(party.DocumentsVerifiedOn) is null;

    /// <summary>
    /// The fields of the entry that are missing or not valid, by name (<c>firstName</c>, <c>lastName</c>, <c>fiscalCode</c>,
    /// <c>citizenship</c>, <c>residence</c>, <c>contactEmail</c>). The e-mail is optional but must be an address when given. Empty:
    /// the entry has all the data of law of a party (decision LR-D8: name, fiscal code or VAT number, residence or registered
    /// office, citizenship).
    /// </summary>
    public static IReadOnlyList<string> PartyProblems(LeaseDraftParty party)
    {
        ArgumentNullException.ThrowIfNull(party);

        var problems = new List<string>(6);
        if (Clean(party.FirstName) is not { Length: <= 100 })
            problems.Add("firstName");
        if (Clean(party.LastName) is not { Length: <= 100 })
            problems.Add("lastName");
        if (!ItalianFiscalCode.IsValid(party.FiscalCode))
            problems.Add("fiscalCode");
        if (Clean(party.Citizenship) is not { } citizenship || !TwoLettersRule().IsMatch(citizenship))
            problems.Add("citizenship");
        if (Clean(party.Residence) is not { Length: <= Party.ResidenceMaxLength })
            problems.Add("residence");
        if (Clean(party.ContactEmail) is { } email && (email.Length > 255 || !EmailRule.IsValid(email)))
            problems.Add("contactEmail");
        return problems;
    }

    public static bool IsPartyComplete(LeaseDraftParty party) => PartyProblems(party).Count == 0;

    /// <summary>
    /// True when the fiscal code is an 11-digit one: a company, an association, or a person with a provisional code. For the
    /// rules about the tenant it is the "azienda" of the demo: the cedolare secca does not apply to it.
    /// </summary>
    public static bool IsCompany(LeaseDraftParty party) =>
        ItalianFiscalCode.Classify(party.FiscalCode) == ItalianFiscalCodeKind.Numeric;

    /// <summary>
    /// <c>true</c> when the date of the verification of the documents is there and is not valid: not a date, out of range, or
    /// after <paramref name="today"/> (a document cannot have been checked tomorrow).
    /// </summary>
    public static bool HasInvalidDocumentsDate(LeaseDraftParty party, DateOnly today)
    {
        if (Clean(party.DocumentsVerifiedOn) is null)
            return false;

        return ParseDate(party.DocumentsVerifiedOn) is not { } date || date > today;
    }

    // ---- canone concordato ---------------------------------------------------------------------------------------

    /// <summary>
    /// The characteristics are inside the limits of the complete creation of a lease (<c>CanoneConcordatoCharacteristicsDto</c>), so
    /// the calculator can be asked: 1 to 10,000 m² for the unit, element counts 0 to 100, appurtenances 0 to 10,000 m², green
    /// 0 to 100,000 m², the zone at most 100 characters and the sheet at most 20.
    /// </summary>
    public static bool IsConcordatoValid(LeaseDraftConcordato c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return c.Sqm is >= 1m and <= 10_000m
               && IsCount(c.TypeAElementCount) && IsCount(c.TypeBElementCount) && IsCount(c.TypeCElementCount)
               && IsCount(c.TypeDElementCount) && IsCount(c.QualifyingTypeDElementCount)
               && c.GarageSqm is >= 0m and <= 10_000m
               && c.BalconySqm is >= 0m and <= 10_000m
               && c.OtherAppurtenanceSqm is >= 0m and <= 10_000m
               && c.PrivateGreenSqm is >= 0m and <= 100_000m
               && (c.ZoneName?.Trim().Length ?? 0) <= 100
               && (c.CadastralSheet?.Trim().Length ?? 0) <= 20;
    }

    private static bool IsCount(int value) => value is >= 0 and <= 100;

    public static RentBandCharacteristics ToCharacteristics(LeaseDraftConcordato c)
    {
        ArgumentNullException.ThrowIfNull(c);

        return new RentBandCharacteristics
        {
            Sqm = c.Sqm,
            GarageSqm = c.GarageSqm,
            BalconySqm = c.BalconySqm,
            OtherAppurtenanceSqm = c.OtherAppurtenanceSqm,
            PrivateGreenSqm = c.PrivateGreenSqm,
            TypeAElementCount = c.TypeAElementCount,
            TypeBElementCount = c.TypeBElementCount,
            TypeCElementCount = c.TypeCElementCount,
            TypeDElementCount = c.TypeDElementCount,
            QualifyingTypeDElementCount = c.QualifyingTypeDElementCount,
            StoveHeating = c.StoveHeating,
            IsFurnished = c.IsFurnished,
            AirConditioning = c.AirConditioning,
            ZoneName = Clean(c.ZoneName),
            CadastralSheet = Clean(c.CadastralSheet),
        };
    }

    // ---- the values read -----------------------------------------------------------------------------------------

    /// <summary>
    /// The values of the contract as they are once read: each is <c>null</c> when it was not typed or is not valid (and then the
    /// column of the lease keeps its starting value, <see cref="LeaseDraftDefaults"/>). An <see cref="LeaseOrigin.Imported"/> contract
    /// is read with the rules of a contract already signed (LR-13): it can have any type and the term its contract states. A transitory
    /// contract made in CasaZen has a term of its own too (TR, <see cref="StatesItsTerm"/>).
    /// </summary>
    public static LeaseDraftReading Read(LeaseDraftValues values, LeaseOrigin origin = LeaseOrigin.Created)
    {
        ArgumentNullException.ThrowIfNull(values);

        var type = ParseContractType(values.ContractType);
        var typeAvailable = type is { } t && LongRentRules.IsAvailable(t, origin);
        var start = ParseDate(values.StartDate);
        var months = typeAvailable ? LongRentRules.FirstTermMonths(type!.Value) : null;
        DateOnly? end = null;
        if (start is { } startDate)
        {
            if (StatesItsTerm(type, typeAvailable, origin) && ParseDate(values.EndDate) is { } declared && declared > startDate)
            {
                // The term of a contract already signed is the one it states: the last day typed, whatever the type (the limits of the
                // type are a need of the flow, not a reason to lose what was typed). A transitory contract made in CasaZen has a term of
                // its own as well, free from 1 to 18 months.
                end = declared;
                months = typeAvailable ? LeaseTerm.Between(ToStoredDate(startDate), ToStoredDate(declared))?.Months : null;
            }
            else
            {
                // The end of the first period follows the type; with no valid type yet it follows the starting type, so the two dates
                // in the columns are always a whole first period apart (and a transitory contract without its end, which has no
                // first period of its own, is no different).
                var termMonths = months ?? LongRentRules.FirstTermMonths(LeaseDraftDefaults.PlaceholderType)!.Value;
                end = DateOnly.FromDateTime(LongRentRules.FirstTermEnd(ToStoredDate(startDate), termMonths));
            }
        }

        return new LeaseDraftReading(
            type,
            typeAvailable,
            ParseTaxRegime(values.TaxRegime),
            IsValidRent(values.MonthlyRent) ? values.MonthlyRent : null,
            values.SecurityDeposit is null ? null : IsValidDeposit(values.SecurityDeposit) ? values.SecurityDeposit : null,
            start,
            end,
            months);
    }
}

/// <summary>
/// The result of <see cref="LeaseDraftRules.Read"/>. <see cref="Type"/> is the type the landlord named (valid or not available),
/// <see cref="TypeAvailable"/> says whether it can be created; <see cref="End"/> is the last day of the first period when the
/// start is valid; <see cref="Months"/> the whole months of that period (null while the type is not an available one, and while the
/// term of an imported transitory contract is not stated).
/// </summary>
public sealed record LeaseDraftReading(
    LeaseContractType? Type,
    bool TypeAvailable,
    LeaseTaxRegime? TaxRegime,
    decimal? Rent,
    decimal? Deposit,
    DateOnly? Start,
    DateOnly? End,
    int? Months);
