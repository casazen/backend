using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Core.Leases;

public static partial class LeaseDraftRules
{
    public const int MaxTextLength = 500;

    public const decimal MaxAmount = 1_000_000m;

    public const int TransitoryDocumentationMaxLength = 300;

    public static readonly DateOnly MinDate = new(1970, 1, 1);

    public static readonly DateOnly MaxDate = new(2100, 12, 31);

    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static LeaseContractType? ParseContractType(string? value) =>
        Enum.TryParse<LeaseContractType>(Clean(value), ignoreCase: true, out var type) && Enum.IsDefined(type) ? type : null;

    public static LeaseTaxRegime? ParseTaxRegime(string? value) =>
        Enum.TryParse<LeaseTaxRegime>(Clean(value), ignoreCase: true, out var regime) && Enum.IsDefined(regime) ? regime : null;

    public static PartyRole? ParseNeedParty(string? value) =>
        Enum.TryParse<PartyRole>(Clean(value), ignoreCase: true, out var party) && Enum.IsDefined(party) ? party : null;

    public static bool StatesItsTerm(LeaseContractType? type, bool typeAvailable, LeaseOrigin origin) =>
        origin == LeaseOrigin.Imported || (typeAvailable && type == LeaseContractType.Transitorio);

    public static DateOnly? ParseDate(string? value)
    {
        if (!DateOnly.TryParseExact(Clean(value), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;

        return date >= MinDate && date <= MaxDate ? date : null;
    }

    public static DateTime ToStoredDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public static bool IsValidRent(decimal? value) =>
        value is > 0m and <= MaxAmount && HasAtMostCents(value.Value);

    public static bool IsValidDeposit(decimal? value) =>
        value is >= 0m and <= MaxAmount && HasAtMostCents(value.Value);

    private static bool HasAtMostCents(decimal value) => decimal.Round(value, 2) == value;

    private static readonly EmailAddressAttribute EmailRule = new();

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex InternationalPhoneRule();

    [GeneratedRegex(@"^\+[0-9\s().-]+$")]
    private static partial Regex InternationalPhoneCharactersRule();

    [GeneratedRegex("^[A-Za-z]{2}$")]
    private static partial Regex TwoLettersRule();

    public static bool IsBlank(LeaseDraftParty party) =>
        Clean(party.FirstName) is null
        && Clean(party.LastName) is null
        && Clean(party.FiscalCode) is null
        && Clean(party.Citizenship) is null
        && Clean(party.Residence) is null
        && Clean(party.ContactEmail) is null
        && Clean(party.ContactPhone) is null
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
        if (Clean(party.ContactPhone) is { } phone && !IsInternationalPhone(phone))
            problems.Add("contactPhone");
        return problems;
    }

    public static bool IsInternationalPhone(string phone) =>
        InternationalPhoneCharactersRule().IsMatch(phone) && InternationalPhoneRule().IsMatch(NormalizePhone(phone));

    /// <summary>
    /// The phone as stored, or <c>null</c> when absent. Separators are dropped. A present number must be one
    /// <see cref="IsInternationalPhone"/> accepts. Throws <see cref="DomainRuleException"/>
    /// (<see cref="LeasePartyErrorCodes.PhoneInvalid"/>) otherwise.
    /// </summary>
    public static string? NormalizeContactPhone(string? phone)
    {
        if (Clean(phone) is not { } text)
            return null;
        if (!IsInternationalPhone(text))
            throw new DomainRuleException(LeasePartyErrorCodes.PhoneInvalid, "LeasePartyPhoneInvalid");

        return NormalizePhone(text);
    }

    public static string NormalizePhone(string phone)
    {
        var clean = phone.Trim();
        return (clean.StartsWith('+') ? "+" : string.Empty) + string.Concat(clean.Where(char.IsDigit));
    }

    public static bool IsPartyComplete(LeaseDraftParty party) => PartyProblems(party).Count == 0;

    /// <summary>
    /// True when the fiscal code is an 11-digit one: a company, an association, or a person with a provisional code. For the
    /// rules about the tenant it is the "azienda" of the demo: the cedolare secca does not apply to it.
    /// </summary>
    public static bool IsCompany(LeaseDraftParty party) =>
        ItalianFiscalCode.Classify(party.FiscalCode) == ItalianFiscalCodeKind.Numeric;

    public static bool HasInvalidDocumentsDate(LeaseDraftParty party, DateOnly today)
    {
        if (Clean(party.DocumentsVerifiedOn) is null)
            return false;

        return ParseDate(party.DocumentsVerifiedOn) is not { } date || date > today;
    }

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

public sealed record LeaseDraftReading(
    LeaseContractType? Type,
    bool TypeAvailable,
    LeaseTaxRegime? TaxRegime,
    decimal? Rent,
    decimal? Deposit,
    DateOnly? Start,
    DateOnly? End,
    int? Months);
