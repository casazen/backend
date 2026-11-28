using System.Globalization;
using System.Text;

namespace Casazen.Core.Services;

/// <summary>
/// The QR code at the door (E22/F01/US02/T01, NF-24, NF-50, decision of the operations plan): a random door token per property, printed as a QR code
/// that opens the page <c>/checkin/porta/{doorToken}</c>, where the guest who arrives proves its stay with the booking code (or the surname of the head guest)
/// and the arrival date and is taken to the identity step of its check-in. The token says only which property the page is about; the stay is proved by
/// the guest, never by the address.
/// </summary>
public interface IDoorCheckInService
{
    Task<string?> GetOrCreateTokenAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    Task<string?> RotateTokenAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The guest at the door: finds the stay of THAT property that <paramref name="input"/> says (not cancelled, not a no-show, not checked out, arriving
    /// yesterday, today or tomorrow or already in progress, with the arrival date the guest typed and the booking code or the surname it typed, and
    /// only when exactly one stay matches) and gives it a check-in link: the open link of the stay is renewed in place (<see cref="IGuestCheckInService.ReissueLinkAsync"/>: only
    /// the hash of a token is stored, so the link that exists cannot be given back; the session gets a new token and the old one dies, with the consent, the document read and the
    /// attempts already spent kept on it), or a new session is made when the stay has no open link. Everything that does not open (an unknown or made-up door token, a stay of another
    /// property, wrong data, a check-in already completed, a door that already had its <see cref="DoorCheckInRules.FailureLimit"/> failed attempts) answers the same
    /// <see cref="DoorCheckInResult.NotFound"/>, never which part was wrong.
    /// </summary>
    Task<DoorCheckInResult> OpenAsync(string doorToken, DoorCheckInInput input, CancellationToken cancellationToken = default);
}

public sealed record DoorCheckInInput(string? BookingCode, string? Surname, DateOnly? ArrivalDate);

public sealed record DoorCheckInResult(string? Token)
{
    public static DoorCheckInResult NotFound { get; } = new((string?)null);

    public bool Found => Token is not null;
}

/// <summary>
/// The failed attempts of each door, counted for every client together (E22/F01/US02 post-review): the limit of the endpoint is per client IP, so a surname and a date could be guessed from
/// many addresses at no cost. A door that had <see cref="DoorCheckInRules.FailureLimit"/> failed attempts within <see cref="DoorCheckInRules.FailureWindow"/> is closed to everybody, the right data
/// included, and answers the same <see cref="DoorCheckInResult.NotFound"/> as for any wrong datum (the answer never tells that the door is closed). Counters live in memory, per replica; only the
/// doors that exist are counted, so a made-up token costs nothing; a replaced token (<c>Rigenera</c>) is a new door with no failures.
/// </summary>
public interface IDoorCheckInFailureTracker
{
    bool IsLocked(string doorToken);

    bool RecordFailure(string doorToken);
}

public static class DoorCheckInRules
{
    public const int SurnameMinLength = 2;

    public const int InputMaxLength = 100;

    public const int FailureLimit = 20;

    public static readonly TimeSpan FailureWindow = TimeSpan.FromHours(1);

    public static bool IsAtTheDoor(DateOnly today, DateOnly checkIn, DateOnly checkOut)
    {
        var arrivesAround = checkIn >= today.AddDays(-1) && checkIn <= today.AddDays(1);
        var inProgress = checkIn <= today && checkOut >= today;
        return arrivesAround || inProgress;
    }

    public static string? NormalizeSurname(string? surname)
    {
        if (string.IsNullOrWhiteSpace(surname) || surname.Length > InputMaxLength)
            return null;

        var decomposed = surname.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToUpperInvariant(c));
        }

        return builder.Length >= SurnameMinLength ? builder.ToString() : null;
    }
}
