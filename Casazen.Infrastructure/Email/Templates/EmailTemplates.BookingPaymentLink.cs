using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>The payment link of the balance of a booking the host entered (NF-74, E08/F01/US01/T05).</summary>
        public const string BookingPaymentLink = "booking-payment-link";
    }

    /// <summary>
    /// The payment link of a booking the host entered, to the guest (NF-74, E08/F01/US01/T05): the stay, the balance to pay and the secure link
    /// to the payment page of the booking site. Every value is HTML-encoded by the builder; the amount is written in the currency of the property.
    /// </summary>
    /// <remarks>
    /// <b>No technical supplier is named in an e-mail with the brand of the host</b> (E50/F01/US03/T01, decision A16). The sentence about the payment page was written, before the guest portal,
    /// with the name of the payment provider in Italian and English (<c>Legacy_BookingPaymentLink_Secure</c>): the CasaZen e-mail of today (no brand, Italian or English) keeps it, so that it stays
    /// identical to the byte while <c>Features:GuestPortal</c> is off; every e-mail with the brand of a host, and every language the portal added, reads the sentence without the name
    /// (<c>BookingPaymentLink_Secure</c>, in the five languages).
    /// </remarks>
    public static EmailContent BookingPaymentLink(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amount,
        string currency,
        string paymentUrl,
        GuestEmailBranding? branding = null)
    {
        var amountText = string.Create(culture, $"{amount:N2} {currency}");
        var language = culture.TwoLetterISOLanguageName;
        var securityKey = branding is null && (language == "it" || language == "en")
            ? "Legacy_BookingPaymentLink_Secure"
            : "BookingPaymentLink_Secure";
        return new EmailHtmlBuilder(culture, branding)
            .Paragraph("BookingPaymentLink_Greeting", guestName)
            .Paragraph("BookingPaymentLink_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("BookingPaymentLink_Amount", amountText)
            .Button("BookingPaymentLink_Cta", paymentUrl)
            .LinkFallback("BookingPaymentLink_LinkFallback", paymentUrl)
            .Muted(securityKey)
            .Muted("BookingPaymentLink_NotYou")
            .Build("BookingPaymentLink_Subject", propertyName);
    }
}
