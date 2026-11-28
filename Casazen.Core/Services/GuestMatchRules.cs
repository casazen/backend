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
    public const int SameEmailScore = 100;

    public const int SameNameBirthScore = 90;

    public const int SamePhoneScore = 80;

    public const int SameNameSamePropertyScore = 60;

    public const string SameEmail = "same_email";

    public const string SameNameBirth = "same_name_birth";

    public const string SamePhone = "same_phone";

    public const string SameNameSameProperty = "same_name_same_property";

    public const int MaxProposalsPerProfile = 5;

    public const int MinNameWords = 2;

    public const int MinBirthYear = 1900;

    public static int ScoreOf(string reason) => reason switch
    {
        SameEmail => SameEmailScore,
        SameNameBirth => SameNameBirthScore,
        SamePhone => SamePhoneScore,
        SameNameSameProperty => SameNameSamePropertyScore,
        _ => 0,
    };

    public static int ScoreOf(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return reasons.Select(reason => ScoreOf(reason)).DefaultIfEmpty(0).Max();
    }

    public static IReadOnlyList<string> OrderReasons(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return reasons
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(reason => ScoreOf(reason))
            .ThenBy(reason => reason, StringComparer.Ordinal)
            .ToList();
    }

    public static string ReasonsJson(IEnumerable<string> reasons) => JsonSerializer.Serialize(OrderReasons(reasons));

    public static (Guid ProfileAId, Guid ProfileBId) OrderPair(Guid first, Guid second) =>
        first.CompareTo(second) <= 0 ? (first, second) : (second, first);

    public static bool IsMatchableName(string normalizedName) =>
        CrmNormalizer.WordCount(normalizedName) >= MinNameWords;

    public static bool IsPlausibleBirthDate(DateTime birthDate, DateTime today) =>
        birthDate.Year >= MinBirthYear && birthDate.Date <= today.Date;

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

    public static bool IsStay(BookingStatus status, DateTime checkOutDate, DateTime today)
    {
        if (status is BookingStatus.Cancelled or BookingStatus.Pending)
            return false;

        return status == BookingStatus.CheckedOut || checkOutDate < today;
    }

    public static Expression<Func<Booking, bool>> StayOf(DateTime today) =>
        booking => booking.Status != BookingStatus.Cancelled
            && booking.Status != BookingStatus.Pending
            && (booking.Status == BookingStatus.CheckedOut || booking.CheckOutDate < today);
}
