using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Rules of the Alloggiati Web record for the guests of a stay (CO-12, A5-02). Source:
/// <c>.claude/context/regulations/alloggiati.md</c>, "Tracciato record" and "Tipi alloggiato" (RS-1). Only facts marked
/// verified (U) there are used: field lengths, the five kinds of guest, the document only for single guests and heads
/// of family or group, sex 1/2, comune and province only when born in Italy. Positions and table codes are not used.
/// </summary>
public static partial class AlloggiatiRecordRules
{
    /// <summary>Cognome: 50 characters in the record.</summary>
    public const int MaxLastNameLength = 50;

    /// <summary>Nome: 30 characters in the record.</summary>
    public const int MaxFirstNameLength = 30;

    /// <summary>Numero documento: 20 characters in the record.</summary>
    public const int MaxDocumentNumberLength = 20;

    /// <summary>Guests per booking accepted by CasaZen (a sanity limit of the application, not a legal one).</summary>
    public const int MaxStayGuests = 30;

    /// <summary>Age under which a guest is shown as a minor.</summary>
    public const int AdultAge = 18;

    // Field names of the record, as reported in MissingFields/CodesToComplete (camelCase, shared with the frontend).
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

    /// <summary>Normalized description of Italy in the "Stati" table: the state of birth of those born in Italy.</summary>
    public const string ItalyNormalizedDescription = "ITALIA";

    /// <summary>True for the kinds of guest that carry the identity document (single guest, head of family or group).</summary>
    public static bool RequiresDocument(StayGuestType type) =>
        type is StayGuestType.SingleGuest or StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup;

    public static bool IsHead(StayGuestType type) => type is StayGuestType.HeadOfFamily or StayGuestType.HeadOfGroup;

    public static bool IsMember(StayGuestType type) => type is StayGuestType.FamilyMember or StayGuestType.GroupMember;

    /// <summary>The kind of the guests that follow a head: family member or group member; null for a single guest.</summary>
    public static StayGuestType? MemberTypeOf(StayGuestType head) => head switch
    {
        StayGuestType.HeadOfFamily => StayGuestType.FamilyMember,
        StayGuestType.HeadOfGroup => StayGuestType.GroupMember,
        _ => null,
    };

    /// <summary>
    /// Normalized official name of each kind in the "Tipi alloggiato" table (Ospite singolo, Capo famiglia, Capo gruppo,
    /// Familiare, Membro gruppo; RS-1, verified U). The code comes from the imported table by this name, never from here.
    /// </summary>
    public static string OfficialNormalizedName(StayGuestType type) => type switch
    {
        StayGuestType.SingleGuest => "OSPITESINGOLO",
        StayGuestType.HeadOfFamily => "CAPOFAMIGLIA",
        StayGuestType.HeadOfGroup => "CAPOGRUPPO",
        StayGuestType.FamilyMember => "FAMILIARE",
        StayGuestType.GroupMember => "MEMBROGRUPPO",
        _ => string.Empty,
    };

    /// <summary>
    /// Checks the order of the guests of a stay: a family member follows its head of family (or another family member
    /// of that head), a group member its head of group, and a head is followed by at least one member ("Capofamiglia e
    /// capogruppo richiedono l'inserimento delle schedine collegate", RS-1). Returns the error of each position at fault.
    /// </summary>
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

    /// <summary>
    /// Fields of the record the guest lacks or has in a form the record does not accept (too long, sex other than
    /// male/female, document kind "other"), as camelCase names (<c>Field*</c> constants). Codes are checked apart
    /// (<see cref="AlloggiatiCodeBook"/>): a missing code does not make the data incomplete, it only blocks the export.
    /// </summary>
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

    /// <summary>
    /// True when the stay has at least one guest, the order of the guests is valid and no guest lacks a record field.
    /// Codes are not part of it: they block only the export.
    /// </summary>
    public static bool IsDataComplete(IReadOnlyList<StayGuest> guests) =>
        guests.Count > 0
        && CompositionErrors(guests.Select(g => g.Type).ToList()).Count == 0
        && guests.All(g => MissingFields(g).Count == 0);

    /// <summary>Stable snake_case code of a composition error, shown per guest.</summary>
    public static string CompositionIssueCode(StayGuestCompositionErrorKind kind) => kind switch
    {
        StayGuestCompositionErrorKind.MemberWithoutHead => "member_without_head",
        StayGuestCompositionErrorKind.HeadWithoutMembers => "head_without_members",
        _ => "invalid_type",
    };

    /// <summary>True when the guest was under <see cref="AdultAge"/> on the arrival date; null without a date of birth.</summary>
    public static bool? IsMinor(DateTime? dateOfBirth, DateTime arrivalDate)
    {
        if (dateOfBirth is not { } birth)
            return null;

        var age = arrivalDate.Year - birth.Year;
        if (arrivalDate.Date < birth.Date.AddYears(age))
            age--;
        return age < AdultAge;
    }

    /// <summary>Province of birth: two-letter car plate code (e.g. RM), verified format of the record.</summary>
    public static bool IsValidProvince(string? province) =>
        province is not null && ProvinceRegex().IsMatch(province);

    /// <summary>Length of the codes of a table in the record: 9 (comuni, stati), 5 (documents), 2 (kind of guest).</summary>
    public static int CodeLength(AlloggiatiCodeTable table) => table switch
    {
        AlloggiatiCodeTable.Comuni or AlloggiatiCodeTable.Stati => 9,
        AlloggiatiCodeTable.Documenti => 5,
        AlloggiatiCodeTable.TipiAlloggiato => 2,
        _ => 0,
    };

    /// <summary>
    /// Shape check of a table code: uppercase letters and digits, at most the length of the field in the record. The
    /// exact code list comes only from the imported official table.
    /// </summary>
    public static bool IsValidCodeShape(AlloggiatiCodeTable table, string? code) =>
        code is not null
        && code.Length >= 1
        && code.Length <= CodeLength(table)
        && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    /// <summary>Trimmed, uppercase code, or null when blank.</summary>
    public static string? NormalizeCode(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Document number without spaces, uppercase.</summary>
    public static string NormalizeDocumentNumber(string? documentNumber) =>
        string.Concat((documentNumber ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>Letters and digits only (no special symbols in the record), at most 20 characters.</summary>
    public static bool IsValidDocumentNumber(string? documentNumber) =>
        documentNumber is not null && DocumentNumberRegex().IsMatch(documentNumber);

    /// <summary>
    /// Description normalized for matching a table entry: accents removed, uppercase, letters and digits only
    /// ("Reggio nell'Emilia" → "REGGIONELLEMILIA"). Used for search and for the unique-name match of a code.
    /// </summary>
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

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex ProvinceRegex();

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex DocumentNumberRegex();
}

/// <summary>Why a guest does not fit in the order of the stay.</summary>
public enum StayGuestCompositionErrorKind
{
    /// <summary>A family or group member without its head before it.</summary>
    MemberWithoutHead,

    /// <summary>A head of family or group with no member after it.</summary>
    HeadWithoutMembers,

    /// <summary>Unknown kind of guest.</summary>
    InvalidType,
}

/// <param name="Index">Position (0-based) of the guest at fault.</param>
public sealed record StayGuestCompositionError(int Index, StayGuestCompositionErrorKind Kind);
