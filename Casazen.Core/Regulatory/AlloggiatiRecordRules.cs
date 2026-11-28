using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Rules of the Alloggiati Web record for the guests of a stay (CO-12, A5-02). Source:
/// <c>.claude/context/regulations/alloggiati.md</c>, "Tracciato record" and "Tipi alloggiato" (RS-1). Only facts marked
/// verified (U) there are used: field lengths, the five kinds of guest, the document only for single guests and heads
/// of family or group, sex 1/2, comune and province only when born in Italy. The positions of the line and its text
/// (verified in CO-13) are in <see cref="AlloggiatiRecordFile"/>; table codes still come only from the imported tables.
/// </summary>
public static partial class AlloggiatiRecordRules
{
    public const int MaxLastNameLength = 50;

    public const int MaxFirstNameLength = 30;

    public const int MaxDocumentNumberLength = 20;

    public const int MaxStayGuests = 30;

    public const int AdultAge = 18;

    public const string FieldType = "type";
    public const string FieldLastName = "lastName";
    public const string FieldFirstName = "firstName";
    public const string FieldGender = "gender";
    public const string FieldDateOfBirth = "dateOfBirth";
    public const string FieldBornInItaly = "bornInItaly";
    public const string FieldBirthComune = "birthComune";
    public const string FieldBirthProvince = "birthProvince";
    public const string FieldBirthCountry = "birthCountry";
    public const string FieldCitizenship = "citizenship";
    public const string FieldDocumentType = "documentType";
    public const string FieldDocumentNumber = "documentNumber";
    public const string FieldDocumentIssuePlace = "documentIssuePlace";

    public const string ItalyNormalizedDescription = "ITALIA";

    public static bool RequiresDocument(StayGuestType type) =>
        type is StayGuestType.SingleGuest or StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup;

    public static bool IsHead(StayGuestType type) => type is StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup;

    public static bool IsMember(StayGuestType type) => type is StayGuestType.FamilyMember or StayGuestType.GroupMember;

    public static StayGuestType? MemberTypeOf(StayGuestType head) => head switch
    {
        StayGuestType.HeadOfFamily => StayGuestType.FamilyMember,
        StayGuestType.HeadOfGroup => StayGuestType.GroupMember,
        _ => null,
    };

    public static string OfficialNormalizedName(StayGuestType type) => type switch
    {
        StayGuestType.SingleGuest => "OSPITESINGOLO",
        StayGuestType.HeadOfFamily => "CAPOFAMIGLIA",
        StayGuestType.HeadOfGroup => "CAPOGRUPPO",
        StayGuestType.FamilyMember => "FAMILIARE",
        StayGuestType.GroupMember => "MEMBROGRUPPO",
        _ => string.Empty,
    };

    public static IReadOnlyList<StayGuestCompositionError> CompositionErrors(IReadOnlyList<StayGuestType> types)
    {
        var errors = new List<StayGuestCompositionError>();
        StayGuestType? leader = null;
        var leaderIndex = -1;
        var membersOfLeader = 0;

        void CloseLeader()
        {
            if (leader is { } current && IsHead(current) && membersOfLeader == 0)
                errors.Add(new StayGuestCompositionError(leaderIndex, StayGuestCompositionErrorKind.HeadWithoutMembers));
        }

        for (var i = 0; i < types.Count; i++)
        {
            var type = types[i];
            if (!Enum.IsDefined(type))
            {
                errors.Add(new StayGuestCompositionError(i, StayGuestCompositionErrorKind.InvalidType));
                continue;
            }

            if (IsMember(type))
            {
                if (leader is { } current && MemberTypeOf(current) == type)
                    membersOfLeader++;
                else
                    errors.Add(new StayGuestCompositionError(i, StayGuestCompositionErrorKind.MemberWithoutHead));
                continue;
            }

            CloseLeader();
            leader = type;
            leaderIndex = i;
            membersOfLeader = 0;
        }

        CloseLeader();
        return errors.OrderBy(e => e.Index).ToList();
    }

    public static IReadOnlyList<string> MissingFields(StayGuest guest)
    {
        var missing = new List<string>();
        if (!Enum.IsDefined(guest.Type)) missing.Add(FieldType);
        if (IsBlankOrLonger(guest.LastName, MaxLastNameLength)) missing.Add(FieldLastName);
        if (IsBlankOrLonger(guest.FirstName, MaxFirstNameLength)) missing.Add(FieldFirstName);
        if (guest.Gender is not (Gender.Male or Gender.Female)) missing.Add(FieldGender);
        if (!guest.DateOfBirth.HasValue) missing.Add(FieldDateOfBirth);

        switch (guest.BornInItaly)
        {
            case null:
                missing.Add(FieldBornInItaly);
                break;
            case true:
                if (string.IsNullOrWhiteSpace(guest.BirthComuneName) && string.IsNullOrWhiteSpace(guest.BirthComuneCode))
                    missing.Add(FieldBirthComune);
                if (!IsValidProvince(guest.BirthProvince)) missing.Add(FieldBirthProvince);
                break;
            case false:
                if (string.IsNullOrWhiteSpace(guest.BirthCountryName) && string.IsNullOrWhiteSpace(guest.BirthCountryCode))
                    missing.Add(FieldBirthCountry);
                break;
        }

        if (string.IsNullOrWhiteSpace(guest.CitizenshipName) && string.IsNullOrWhiteSpace(guest.CitizenshipCode))
            missing.Add(FieldCitizenship);

        if (RequiresDocument(guest.Type))
        {
            var hasKind = guest.DocumentType is GuestDocumentType.Passport or GuestDocumentType.IdentityCard
                or GuestDocumentType.DriversLicense;
            if (!hasKind && string.IsNullOrWhiteSpace(guest.DocumentTypeCode)) missing.Add(FieldDocumentType);
            if (!IsValidDocumentNumber(NormalizeDocumentNumber(guest.DocumentNumber))) missing.Add(FieldDocumentNumber);
            if (string.IsNullOrWhiteSpace(guest.DocumentIssuePlaceName) && string.IsNullOrWhiteSpace(guest.DocumentIssuePlaceCode))
                missing.Add(FieldDocumentIssuePlace);
        }

        return missing;
    }

    public static bool IsDataComplete(IReadOnlyList<StayGuest> guests) =>
        guests.Count > 0
        && CompositionErrors(guests.Select(g => g.Type).ToList()).Count == 0
        && guests.All(g => MissingFields(g).Count == 0);

    public static string CompositionIssueCode(StayGuestCompositionErrorKind kind) => kind switch
    {
        StayGuestCompositionErrorKind.MemberWithoutHead => "member_without_head",
        StayGuestCompositionErrorKind.HeadWithoutMembers => "head_without_members",
        _ => "invalid_type",
    };

    public static bool? IsMinor(DateTime? dateOfBirth, DateTime arrivalDate)
    {
        if (dateOfBirth is not { } birth)
            return null;

        var age = arrivalDate.Year - birth.Year;
        if (arrivalDate.Date < birth.Date.AddYears(age))
            age--;
        return age < AdultAge;
    }

    private static readonly HashSet<string> Provinces = new(StringComparer.Ordinal)
    {
        "AG", "AL", "AN", "AO", "AP", "AQ", "AR", "AT", "AV", "BA", "BG", "BI", "BL", "BN", "BO", "BR", "BS", "BT",
        "BZ", "CA", "CB", "CE", "CH", "CI", "CL", "CN", "CO", "CR", "CS", "CT", "CZ", "EN", "FC", "FE", "FG", "FI",
        "FM", "FR", "GE", "GO", "GR", "IM", "IS", "KR", "LC", "LE", "LI", "LO", "LT", "LU", "MB", "MC", "ME", "MI",
        "MN", "MO", "MS", "MT", "NA", "NO", "NU", "OG", "OR", "OT", "PA", "PC", "PD", "PE", "PG", "PI", "PN", "PO",
        "PR", "PT", "PU", "PV", "PZ", "RA", "RC", "RE", "RG", "RI", "RM", "RN", "RO", "SA", "SI", "SO", "SP", "SR",
        "SS", "SU", "SV", "TA", "TE", "TN", "TO", "TP", "TR", "TS", "TV", "UD", "VA", "VB", "VC", "VE", "VI", "VR",
        "VS", "VT", "VV",
    };

    public static bool IsValidProvince(string? province) =>
        province is not null && Provinces.Contains(province.Trim().ToUpperInvariant());

    public static int CodeLength(AlloggiatiCodeTable table) => table switch
    {
        AlloggiatiCodeTable.Comuni or AlloggiatiCodeTable.Stati => 9,
        AlloggiatiCodeTable.Documenti => 5,
        AlloggiatiCodeTable.TipiAlloggiato => 2,
        _ => 0,
    };

    public static bool IsValidCodeShape(AlloggiatiCodeTable table, string? code) =>
        code is not null
        && code.Length >= 1
        && code.Length <= CodeLength(table)
        && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    public static string? NormalizeCode(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string NormalizeDocumentNumber(string? documentNumber) =>
        string.Concat((documentNumber ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    public static bool IsValidDocumentNumber(string? documentNumber) =>
        documentNumber is not null && DocumentNumberRegex().IsMatch(documentNumber);

    public static string NormalizeDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    private static bool IsBlankOrLonger(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength;

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex DocumentNumberRegex();
}

public enum StayGuestCompositionErrorKind
{
    MemberWithoutHead,

    HeadWithoutMembers,

    InvalidType,
}

public sealed record StayGuestCompositionError(int Index, StayGuestCompositionErrorKind Kind);
