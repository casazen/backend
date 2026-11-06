using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>The change of the dates or the guests of a booking of the booking site, to the guest (NF-75, E08/F02/US01/T01).</summary>
        public const string GuestBookingModified = "guest-booking-modified";
    }

    /// <summary>
    /// The host changed the dates or the guests of the booking, to the guest (NF-75, E08/F02/US01/T01): the stay as it is now, the new total and what
    /// happens to the money: a refund on the way (<paramref name="refundedAmount"/>), the saved card charged for the difference
    /// (<paramref name="chargedAmount"/>) or a payment link that follows in its own email (<paramref name="paymentLinkFollows"/>). The notes of the
    /// host are never part of it. Every value is HTML-encoded by the builder; the amounts are written in the currency of the property.
    /// </summary>
    public static EmailContent GuestBookingModified(
        CultureInfo culture,
        string guestName,
        BookingEmailSummary booking,
        decimal refundedAmount,
        decimal chargedAmount,
        bool paymentLinkFollows,
        BookingHostContact hostContact)
    {
        ArgumentNullException.ThrowIfNull(booking);
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("Booking_Greeting", guestName)
            .Paragraph("GuestBookingModified_Body", booking.PropertyName, booking.CheckInDate, booking.CheckOutDate, booking.Guests)
            .Paragraph("GuestBookingModified_Total", Money(booking.Total, booking.Currency, culture));
        if (refundedAmount > 0m)
        {
            builder
                .Paragraph("GuestBookingModified_Refund", Money(refundedAmount, booking.Currency, culture))
                .Muted("GuestRefundConfirmed_Timing");
        }

        if (chargedAmount > 0m)
            builder.Paragraph("GuestBookingModified_Charged", Money(chargedAmount, booking.Currency, culture));

        if (paymentLinkFollows)
            builder.Paragraph("GuestBookingModified_PaymentLink");

        return ContactHost(builder, hostContact).Build("GuestBookingModified_Subject", booking.PropertyName);
    }
}
