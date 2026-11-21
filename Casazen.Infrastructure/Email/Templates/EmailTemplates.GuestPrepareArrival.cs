using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«Prepara il tuo arrivo»: the e-mail that carries the stay link of the guest portal, before the arrival (E50/F01/US03/T03, NF-152).</summary>
        public const string GuestPrepareArrival = "guest-prepare-arrival";
    }

    /// <summary>
    /// «Prepara il tuo arrivo» (E50/F01/US03/T03, NF-152, section 5.1), to the guest, three days before the arrival: with <c>Features:GuestPortal</c> on it <b>takes the place of</b>
    /// <see cref="GuestCheckInLink"/> (the e-mail that carries the check-in link, queued by the same job at the same time, with the same delivery tracking), and the host can send it again. The
    /// heading and the subject are «Prepara il tuo arrivo»; the main button is the stay link (<paramref name="stayUrl"/>, made by the link service of the portal and handed in: a template never builds it)
    /// with its fallback link; the check-in link, when there is one (<paramref name="checkInUrl"/>), stays as the second button, with the request of an invoice and the end of its validity, because until the
    /// portal has its own check-in section dropping it would leave the guest without a way to check in. Written in the language of the guest, with the brand of the host.
    /// </summary>
    /// <param name="checkInUrl">The check-in link of the session this e-mail delivers, or <c>null</c> when it carries none (the host sends the stay link again).</param>
    /// <param name="checkInExpiresAtUtc">End of validity of <paramref name="checkInUrl"/> (shown in Italian time).</param>
    public static EmailContent GuestPrepareArrival(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        string stayUrl,
        string? checkInUrl = null,
        DateTime? checkInExpiresAtUtc = null,
        GuestEmailBranding? branding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stayUrl);
        var builder = new EmailHtmlBuilder(culture, branding)
            .Heading("GuestPrepareArrival_Heading")
            .Paragraph("GuestPrepareArrival_Greeting", guestName)
            .Paragraph("GuestPrepareArrival_Body", propertyName, checkInDate)
            .Button("Guest_StayLinkCta", stayUrl)
            .LinkFallback("Booking_LinkFallback", stayUrl);

        if (checkInUrl is not null)
        {
            builder
                .Paragraph("GuestCheckInLink_Instructions")
                .Button("GuestCheckInLink_Cta", checkInUrl)
                .Paragraph("Guest_InvoiceRequest")
                .Button("Guest_InvoiceCta", InvoiceLink(checkInUrl));
            if (checkInExpiresAtUtc is { } expiresAtUtc)
                builder.Muted("GuestCheckInLink_Validity", builder.FormatInstant(expiresAtUtc));
        }

        return builder.Build("GuestPrepareArrival_Subject", propertyName);
    }
}
