using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>Invitation to review a stay, to the guest, sent only when the host asks for it (DB-12a).</summary>
        public const string ReviewInvite = "review-invite";
    }

    /// <summary>
    /// The one email that asks a guest for the review of a stay (DB-12a), with the signed link to the page where the review is written.
    /// It is sent when the host asks, once per booking, never by a job or an event. It names the property and the dates and nothing else
    /// of the booking: no amount, no code, no address of the guest.
    /// </summary>
    /// <param name="expiresAtUtc">End of validity of the signed link (shown in Italian time): the end of the review window.</param>
    public static EmailContent ReviewInvite(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string reviewUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("ReviewInvite_Greeting", guestName)
            .Paragraph("ReviewInvite_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("ReviewInvite_How")
            .Button("ReviewInvite_Cta", reviewUrl)
            .LinkFallback("ReviewInvite_LinkFallback", reviewUrl)
            .Muted("ReviewInvite_Validity", builder.FormatInstant(expiresAtUtc))
            .Muted("ReviewInvite_Once")
            .Build("ReviewInvite_Subject", propertyName);
    }
}
