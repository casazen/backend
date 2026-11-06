using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The decision table of the matching of a guest to a CRM profile (E07/F01/US02/T01, NF-88, ADR-021) and the pure rules around it: what a piece
/// of evidence is worth, what is decided by itself and what is only proposed to a person, how a pair is stored, which booking is a stay.
/// Everything the matching decides is here, so the table is read in one place and tested without a database.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Evidence</term><description>Reason, score and what the matching does</description></listheader>
/// <item><term>Same e-mail address, not an OTA forward</term><description><see cref="SameEmail"/>, 100: the guest is linked to the profile of the
/// e-mail contact. Two profiles that hold the same address are proposed with this reason.</description></item>
/// <item><term>Same normalized name and birth date</term><description><see cref="SameNameBirth"/>, 90: the guest is linked to the profile with the
/// same keyed name-and-birth hash; when the e-mail points to another profile, that pair is proposed.</description></item>
/// <item><term>Same phone number</term><description><see cref="SamePhone"/>, 80: only a proposal. A phone is shared by a family, a company, an
/// agent who books for others.</description></item>
/// <item><term>Same normalized name and a property stayed in</term><description><see cref="SameNameSameProperty"/>, 60: only a proposal.</description></item>
/// </list>
/// A merge is never executed by the matching: a row of <see cref="GuestMergeCandidate"/> waits for a person.
/// </remarks>
public static class GuestMatchRules
{
    /// <summary>Score of the same e-mail address.</summary>
    public const int SameEmailScore = 100;

    /// <summary>Score of the same normalized name and birth date.</summary>
    public const int SameNameBirthScore = 90;

    /// <summary>Score of the same phone number.</summary>
    public const int SamePhoneScore = 80;

    /// <summary>Score of the same normalized name at a property both stayed in.</summary>
    public const int SameNameSamePropertyScore = 60;

    /// <summary>Reason code: the same e-mail address (<see cref="GuestMergeCandidate.ReasonsJson"/>).</summary>
    public const string SameEmail = "same_email";

    /// <summary>Reason code: the same normalized name and the same birth date.</summary>
    public const string SameNameBirth = "same_name_birth";

    /// <summary>Reason code: the same phone number.</summary>
    public const string SamePhone = "same_phone";

    /// <summary>Reason code: the same normalized name and a property both stayed in.</summary>
    public const string SameNameSameProperty = "same_name_same_property";

    /// <summary>
    /// Most proposals a profile holds at once: the matching adds none to a profile that has this many waiting for a person, and none that
    /// would take a profile it points at past it. A phone shared by an agent with a hundred guests is five questions to a person, not a hundred.
    /// </summary>
    public const int MaxProposalsPerProfile = 5;

    /// <summary>
    /// Fewest words a normalized name needs to be evidence: a first name alone (<c>elena</c>) belongs to too many people. A name in one field
    /// with both words counts (<c>Mario Rossi</c> in the first name and nothing in the last).
    /// </summary>
    public const int MinNameWords = 2;

    /// <summary>Oldest birth date taken as a date: the year before it is a form default, not a person.</summary>
    public const int MinBirthYear = 1900;

    /// <summary>What the reason is worth, from 0 to 100; 0 for a code the table does not know.</summary>
    public static int ScoreOf(string reason) => reason switch
    {
        SameEmail => SameEmailScore,
        SameNameBirth => SameNameBirthScore,
        SamePhone => SamePhoneScore,
        SameNameSameProperty => SameNameSamePropertyScore,
        _ => 0,
    };

    /// <summary>What a pair is worth when several reasons agree: the highest of them. 0 for no reason.</summary>
    public static int ScoreOf(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return reasons.Select(reason => ScoreOf(reason)).DefaultIfEmpty(0).Max();
    }

    /// <summary>The distinct reasons, the strongest first and alphabetical among equals.</summary>
    public static IReadOnlyList<string> OrderReasons(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return reasons
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(reason => ScoreOf(reason))
            .ThenBy(reason => reason, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The reasons as the JSON array a <see cref="GuestMergeCandidate"/> stores: codes only, never what matched.</summary>
    public static string ReasonsJson(IEnumerable<string> reasons) => JsonSerializer.Serialize(OrderReasons(reasons));

    /// <summary>
    /// The two profile ids in the order a pair is stored: the smaller first by <see cref="Guid.CompareTo(Guid)"/>, which is the order the
    /// database check <c>CK_GuestMergeCandidates_ProfileOrder</c> accepts. (A, B) and (B, A) are one pair.
    /// </summary>
    public static (Guid ProfileAId, Guid ProfileBId) OrderPair(Guid first, Guid second) =>
        first.CompareTo(second) <= 0 ? (first, second) : (second, first);

    /// <summary>True when the normalized name (<see cref="CrmNormalizer.NormalizeName"/>) has enough words to be evidence of a person.</summary>
    public static bool IsMatchableName(string normalizedName) =>
        CrmNormalizer.WordCount(normalizedName) >= MinNameWords;

    /// <summary>True when <paramref name="birthDate"/> can be a person's: after <see cref="MinBirthYear"/> and not after <paramref name="today"/>.</summary>
    public static bool IsPlausibleBirthDate(DateTime birthDate, DateTime today) =>
        birthDate.Year >= MinBirthYear && birthDate.Date <= today.Date;

    /// <summary>
    /// The text the keyed hash of the composite key is computed over (ADR-021-R2): e-mail, name, surname, birth date as <c>yyyyMMdd</c> and
    /// fiscal code, each trimmed and lowercase (the fiscal code uppercase), joined with <c>|</c>. Hashed with the keyed index, never stored.
    /// </summary>
    public static string CompositeKeyText(string? email, string? firstName, string? lastName, DateTime? dateOfBirth, string? fiscalCode)
    {
        var normalizedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedFirstName = (firstName ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedLastName = (lastName ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedDob = dateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;
        var normalizedFiscalCode = (fiscalCode ?? string.Empty).Trim().ToUpperInvariant();
        return $"{normalizedEmail}|{normalizedFirstName}|{normalizedLastName}|{normalizedDob}|{normalizedFiscalCode}";
    }

    /// <summary>
    /// The text the keyed name-and-birth hash is computed over: the normalized name and the birth date as <c>yyyyMMdd</c>. <c>null</c> unless the
    /// name is evidence (<see cref="IsMatchableName"/>) and the birth date is a date a person can have (<see cref="IsPlausibleBirthDate"/>): the
    /// profile never holds the birth date, only the keyed hash of this text.
    /// </summary>
    public static string? NameBirthKeyText(string normalizedName, DateTime? birthDate, DateTime today)
    {
        if (birthDate is not { } date || !IsMatchableName(normalizedName) || !IsPlausibleBirthDate(date, today))
            return null;

        return $"{normalizedName}|{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// True when a booking is a stay that happened: not cancelled, not pending (a request nobody confirmed) and either checked out or with a
    /// check-out date before today (the Rome calendar day, <c>TodayInRome</c> of the injected clock). A stay in progress and an arrival still to
    /// come are not stays yet.
    /// </summary>
    public static bool IsStay(BookingStatus status, DateTime checkOutDate, DateTime today)
    {
        if (status is BookingStatus.Cancelled or BookingStatus.Pending)
            return false;

        return status == BookingStatus.CheckedOut || checkOutDate < today;
    }

    /// <summary>
    /// <see cref="IsStay"/> as a condition on a booking, for a query that counts or adds up the stays in SQL (the profile list of a caller who
    /// reaches only some properties, E07/F01/US03/T01). The same rule: not cancelled, not pending, and either checked out or with a check-out
    /// before <paramref name="today"/>.
    /// </summary>
    public static Expression<Func<Booking, bool>> StayOf(DateTime today) =>
        booking => booking.Status != BookingStatus.Cancelled
            && booking.Status != BookingStatus.Pending
            && (booking.Status == BookingStatus.CheckedOut || booking.CheckOutDate < today);
}
