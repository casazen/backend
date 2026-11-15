using System.Globalization;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The text of the one message that carries the check-in link in the reservation thread of the portal chat (E23/F01/US01/T01, NF-122): plain text (a chat has no markup),
/// short, in the language of the guest (Italian, English, German, French or Spanish: <c>GuestCheckInChat_Body</c> of the five <c>EmailTexts</c> files), with the stay, the
/// link and its validity. It names no portal, no supplier and no assistant: it is a message of the host's house, written by CasaZen on its behalf.
/// </summary>
public static partial class EmailTemplates
{
    /// <summary>
    /// The message of the link. <paramref name="checkInDate"/> and <paramref name="expiresAtUtc"/> are instants: they are written as the day they fall on in Europe/Rome,
    /// in full (day of the week included) so that no format of a date can be read two ways.
    /// </summary>
    public static string GuestCheckInChatMessage(
        CultureInfo culture,
        string propertyName,
        DateTime checkInDate,
        string checkInUrl,
        DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkInUrl);

        return string.Format(
            culture,
            EmailTexts.Get("GuestCheckInChat_Body", culture),
            propertyName,
            RomeDay(checkInDate, culture),
            checkInUrl,
            RomeDay(expiresAtUtc, culture));
    }

    /// <summary>The day an instant falls on in Europe/Rome, in full, in the culture.</summary>
    private static string RomeDay(DateTime instantUtc, CultureInfo culture) =>
        RomeCalendar.DateInRome(instantUtc).ToDateTime(TimeOnly.MinValue).ToString("D", culture);
}
