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
    /// <summary>
    /// The door token of a property of the org, made the first time it is asked (<see cref="Casazen.Core.Entities.PropertyDoorTokens"/>): a token that exists is
    /// never changed by this call. <c>null</c> when the property is not one of the org (or is deleted).
    /// </summary>
    Task<string?> GetOrCreateTokenAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the door token of a property of the org with a new one: the printed code of the old token stops working at once (the public page answers it
    /// like a code that never existed). <c>null</c> when the property is not one of the org (or is deleted).
    /// </summary>
    Task<string?> RotateTokenAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The guest at the door: finds the stay of THAT property that <paramref name="input"/> says (not cancelled, not a no-show, not checked out, arriving
    /// yesterday, today or tomorrow or already in progress, with the arrival date the guest typed and the booking code or the surname it typed, and
    /// only when exactly one stay matches) and gives it a check-in link: the open link of the stay is renewed in place (<see cref="IGuestCheckInService.ReissueLinkAsync"/>: only
    /// the hash of a token is stored, so the link that exists cannot be given back; the session gets a new token and the old one dies, with the consent, the document read and the
    /// attempts already spent kept on it), or a new session is made when the stay has no open link. Everything that does not open (an unknown or made-up door token, a stay of another
    /// property, wrong data, a check-in already completed) answers the same <see cref="DoorCheckInResult.NotFound"/>, never which part was wrong.
    /// </summary>
    Task<DoorCheckInResult> OpenAsync(string doorToken, DoorCheckInInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the guest typed at the door: the booking code (the code of the confirmation e-mail) or, when it has none, the surname of the head guest, and the
/// arrival date of the stay. The code wins when both are given.
/// </summary>
public sealed record DoorCheckInInput(string? BookingCode, string? Surname, DateOnly? ArrivalDate);

/// <summary>
/// The answer at the door: the raw token of the check-in link, and nothing else (no booking data), or <see cref="NotFound"/>, the one answer for everything
/// that does not open. Two results of <see cref="NotFound"/> are equal.
/// </summary>
public sealed record DoorCheckInResult(string? Token)
{
    /// <summary>The one answer for a door token that is not valid and for data that do not name a stay of the property.</summary>
    public static DoorCheckInResult NotFound { get; } = new((string?)null);

    /// <summary>True when a link was issued.</summary>
    public bool Found => Token is not null;
}

/// <summary>The rules of the door that do not need a database: which stays can use it and how a surname is compared.</summary>
public static class DoorCheckInRules
{
    /// <summary>Shortest surname that is compared (after the normalisation): a single letter is never a surname.</summary>
    public const int SurnameMinLength = 2;

    /// <summary>Longest text of a booking code or surname the door reads: longer is not a code and not a surname.</summary>
    public const int InputMaxLength = 100;

    /// <summary>
    /// True when a stay can use the door on <paramref name="today"/> (the Europe/Rome day): it arrives yesterday, today or tomorrow, or it is in progress
    /// (it started on or before today and ends on or after today). A stay that is already over, or that starts later, does not.
    /// </summary>
    public static bool IsAtTheDoor(DateOnly today, DateOnly checkIn, DateOnly checkOut)
    {
        var arrivesAround = checkIn >= today.AddDays(-1) && checkIn <= today.AddDays(1);
        var inProgress = checkIn <= today && checkOut >= today;
        return arrivesAround || inProgress;
    }

    /// <summary>
    /// A surname as it is compared: accents, case, spaces, apostrophes and dashes are ignored (<c>De Luca</c>, <c>DE-LUCA</c> and <c>deluca</c> are the same,
    /// so is <c>D'Angelo</c> and <c>DAngelo</c>); <c>null</c> when what is left is shorter than <see cref="SurnameMinLength"/> letters or digits.
    /// </summary>
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
