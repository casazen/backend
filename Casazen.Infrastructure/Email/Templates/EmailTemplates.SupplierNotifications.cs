using System.Globalization;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The informative notices to a supplier that its settings switch on and off (SP-16d): the reminder of the day before the work and the push of
/// a payment received. Texts in <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys <c>ServiceRequestDayBefore_*</c>, <c>Push_ServiceRequestDayBefore_*</c>,
/// <c>Push_ServicePaymentReceived_*</c>). Every dynamic value is HTML-encoded by the builder; every link is built by <see cref="PublicSiteLinks"/>
/// and passed in.
/// </summary>
/// <remarks>
/// <b>Only what is true, and only what is safe</b> (decisions D9 and D24): the reminder names the service, the time and the <b>comune</b>, never
/// the address or the contact of the customer (they are in the console once the supplier took the request, and the arguments of a queued job are no
/// place for personal data), and the payment says the net before the fees of Stripe and nothing about when the money reaches the bank.
/// </remarks>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string ServiceRequestDayBefore = "service-request-day-before";
    }

    /// <summary>
    /// The reminder of tomorrow's job, to the supplier, for a request of any context (SP-16d). It carries the service, the time (and its end when the
    /// request has one) and the comune; the address and the contact of the customer are not in it.
    /// </summary>
    /// <param name="serviceName">The service of the request (the label of its category when it has none).</param>
    /// <param name="startUtc">When the work starts.</param>
    /// <param name="endUtc">When it ends, when the request has an end.</param>
    /// <param name="requestUrl">The page of the request in the supplier's console.</param>
    public static EmailContent ServiceRequestDayBefore(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        DateTime startUtc,
        DateTime? endUtc,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("ServiceRequestDayBefore_Body", serviceName, comune)
            .Paragraph(
                "ServiceRequestDayBefore_When",
                endUtc is { } end && end > startUtc ? FormatInterval(builder, startUtc, end) : builder.FormatInstant(startUtc))
            .Muted("ServiceRequestDayBefore_Hint")
            .Button("ServiceRequestDayBefore_Cta", requestUrl)
            .Build("ServiceRequestDayBefore_Subject", serviceName, comune);
    }

    /// <summary>The push of the same reminder: the service, the comune and the hour of Rome. No address.</summary>
    public static PushText ServiceRequestDayBeforePush(CultureInfo culture, string serviceName, string comune, DateTime startUtc) =>
        Push(culture, "Push_ServiceRequestDayBefore", serviceName, comune, RomeCalendar.ToRome(startUtc).ToString("HH:mm", culture));

    /// <summary>
    /// The push of a payment made through CasaZen, to the supplier (SP-16d): the work and the net, which is before the fees of Stripe. Nothing
    /// about when the money arrives.
    /// </summary>
    public static PushText ServicePaymentReceivedPush(CultureInfo culture, string serviceName, int netCents) =>
        Push(culture, "Push_ServicePaymentReceived", serviceName, FormatEuro(netCents, culture));
}
