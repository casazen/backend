using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>Guest self-service cancellation link (BK-02, BK-07, PO 2026-10-08).</summary>
        public const string GuestCancelLink = "guest-cancel-link";
    }

    /// <summary>
    /// Tokenized self-cancellation link, to the guest (BK-02, BK-07, PO 2026-10-08).
    /// Sent by the host (or automatically) when the guest wants to cancel a booking.
    /// </summary>
    /// <param name="expiresAtUtc">End of validity of the signed link (shown in Italian time).</param>
    public static EmailContent GuestCancelLink(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string cancelUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("GuestCancelLink_Greeting", guestName)
            .Paragraph("GuestCancelLink_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("GuestCancelLink_Policy")
            .Button("GuestCancelLink_Cta", cancelUrl)
            .LinkFallback("GuestCancelLink_LinkFallback", cancelUrl)
            .Muted("GuestCancelLink_Validity", builder.FormatInstant(expiresAtUtc))
            .Muted("GuestCancelLink_NotYou")
            .Build("GuestCancelLink_Subject", propertyName);
    }
}
