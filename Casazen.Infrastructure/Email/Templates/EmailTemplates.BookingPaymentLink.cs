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
    public static EmailContent BookingPaymentLink(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        decimal amount,
        string currency,
        string paymentUrl)
    {
        var amountText = string.Create(culture, $"{amount:N2} {currency}");
        return new EmailHtmlBuilder(culture)
            .Paragraph("BookingPaymentLink_Greeting", guestName)
            .Paragraph("BookingPaymentLink_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("BookingPaymentLink_Amount", amountText)
            .Button("BookingPaymentLink_Cta", paymentUrl)
            .LinkFallback("BookingPaymentLink_LinkFallback", paymentUrl)
            .Muted("BookingPaymentLink_Secure")
            .Muted("BookingPaymentLink_NotYou")
            .Build("BookingPaymentLink_Subject", propertyName);
    }
}
