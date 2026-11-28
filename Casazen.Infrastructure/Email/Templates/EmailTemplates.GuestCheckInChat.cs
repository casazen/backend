using System.Globalization;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    /// <summary>
    /// The message of the link. <paramref name="checkInDate"/> and <paramref name="expiresAtUtc"/> are instants: they are written as the day they fall on in Europe/Rome,
    /// in full (day of the week included) so that no format of a date can be read two ways. With <paramref name="stayUrl"/> (E50/F01/US03/T03, only while the guest portal is on: the
    /// address is made by its link service and handed in) a line with the stay link follows the message, on a line of its own; without it the message is the one it has always been.
    /// </summary>
    public static string GuestCheckInChatMessage(
        CultureInfo culture,
        string propertyName,
        DateTime checkInDate,
        string checkInUrl,
        DateTime expiresAtUtc,
        string? stayUrl = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkInUrl);

        var message = string.Format(
            culture,
            EmailTexts.Get("GuestCheckInChat_Body", culture),
            propertyName,
            RomeDay(checkInDate, culture),
            checkInUrl,
            RomeDay(expiresAtUtc, culture));

        return string.IsNullOrWhiteSpace(stayUrl)
            ? message
            : message + "\n" + string.Format(culture, EmailTexts.Get("GuestCheckInChat_StayLink", culture), stayUrl);
    }

    private static string RomeDay(DateTime instantUtc, CultureInfo culture) =>
        RomeCalendar.DateInRome(instantUtc).ToDateTime(TimeOnly.MinValue).ToString("D", culture);
}
