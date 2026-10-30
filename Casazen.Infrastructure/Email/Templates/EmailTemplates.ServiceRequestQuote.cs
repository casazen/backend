using System.Globalization;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mails of the quote a supplier sends on a new request (SP-04b): the quote to the host or to the private customer of a showcase
/// request, the answer of the customer to the supplier, and the quote nobody answered in time, to the customer and to the supplier. Texts in
/// <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys <c>ServiceRequestQuote*</c> and <c>SupplierBookingQuote*</c>). Every dynamic
/// value is HTML-encoded by the builder; every link is built by <see cref="PublicSiteLinks"/> and passed in.
/// </summary>
/// <remarks>
/// <b>Only what is true</b> (decision D24): the deadline named is the one the job enforces (the time to answer, for a showcase customer
/// only: a host has none), the time of the work is said not to be reserved before the customer accepts, and nothing is said about how or
/// when the work is paid. A supplier is told "Nome C." and never more before the take (decision D9).
/// </remarks>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string ServiceRequestQuoteReceived = "service-request-quote-received";
        public const string ServiceRequestQuoteAnswered = "service-request-quote-answered";
        public const string SupplierBookingQuoteExpired = "supplier-booking-quote-expired";
        public const string SupplierBookingQuoteLapsed = "supplier-booking-quote-lapsed";
    }

    /// <summary>
    /// The supplier answered a new request with a quote, to the customer of the request: the host (no greeting by name, no deadline, the
    /// link to its page when it has one) or the private customer of a showcase request (greeted by name, with the time to answer).
    /// </summary>
    /// <param name="customerName">The name the mail greets; <c>null</c> for a host (its mails name no one).</param>
    /// <param name="serviceName">The service of the request (the label of its category when it has none).</param>
    /// <param name="startUtc">The time of the work: the one the quote offers instead, else the one the request has.</param>
    /// <param name="endUtc">The end of that time, when the quote offers another one.</param>
    /// <param name="answerByUtc">Until when a showcase customer can answer (after it the request is cancelled); <c>null</c> for a host.</param>
    /// <param name="url">The page to answer from; <c>null</c> when there is none (a long-rent request has no page of the stay).</param>
    public static EmailContent ServiceRequestQuoteReceived(
        CultureInfo culture,
        string? customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        DateTime? endUtc,
        IReadOnlyList<ServiceRequestQuoteLine> lines,
        int totalCents,
        string? note,
        DateTime? answerByUtc,
        string? url)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (!string.IsNullOrWhiteSpace(customerName))
            builder.Paragraph("SupplierBooking_Greeting", customerName);

        builder
            .Paragraph("ServiceRequestQuoteReceived_Body", supplierName, serviceName, FormatEuro(totalCents, culture))
            .Paragraph("ServiceRequestQuoteReceived_Lines")
            .ValueList(lines.Select(line => $"{line.Label}: {FormatEuro(line.AmountCents, culture)}"))
            .Paragraph(
                "ServiceRequestQuoteReceived_When",
                endUtc is { } end ? FormatInterval(builder, startUtc, end) : builder.FormatInstant(startUtc))
            .Quote("ServiceRequestQuoteReceived_NoteLabel", note)
            .Muted("ServiceRequestQuoteReceived_Hint");
        if (answerByUtc is { } answerBy)
            builder.Paragraph("ServiceRequestQuoteReceived_Deadline", builder.FormatInstant(answerBy));
        if (url is not null)
            builder.Button("ServiceRequestQuoteReceived_Cta", url);

        return builder.Build("ServiceRequestQuoteReceived_Subject", supplierName);
    }

    /// <summary>
    /// The customer (the host, or the private customer of a showcase request) accepted or turned down the quote, to the supplier (Italian).
    /// Accepted: the price and the time of the work, and that the request is the supplier's. Turned down: the request waits for its answer
    /// again. The comune and, for a showcase customer, "Nome C." only: the rest comes with the take (decision D9).
    /// </summary>
    /// <param name="customerShortName">"Nome C." of a showcase customer; empty for a host, whose mails name no one.</param>
    /// <param name="startUtc">The time of the work; named only when the quote was accepted.</param>
    public static EmailContent ServiceRequestQuoteAnswered(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        bool accepted,
        int totalCents,
        DateTime? startUtc,
        string inboxUrl)
    {
        var prefix = accepted ? "ServiceRequestQuoteAccepted" : "ServiceRequestQuoteRejected";
        var builder = new EmailHtmlBuilder(culture).Paragraph("SupplierBooking_Greeting", supplierName);
        if (accepted)
        {
            builder.Paragraph($"{prefix}_Body", serviceName, comune, FormatEuro(totalCents, culture));
            if (startUtc is { } start)
                builder.Paragraph("ServiceRequestQuoteAccepted_When", builder.FormatInstant(start));
        }
        else
        {
            builder.Paragraph($"{prefix}_Body", serviceName, comune);
        }

        CustomerLine(builder, customerShortName);
        return builder
            .Button("ServiceRequestQuoteAnswered_Cta", inboxUrl)
            .Build($"{prefix}_Subject", comune);
    }

    /// <summary>
    /// The customer did not answer the quote and the request was cancelled, to the customer. Not the words of
    /// <see cref="SupplierBookingExpired"/>: there it is the supplier that did not answer, here it is the customer (decision D24).
    /// </summary>
    /// <param name="startUtc">The time the customer had asked for (the request keeps it while a quote waits).</param>
    public static EmailContent SupplierBookingQuoteExpired(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string showcaseUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingQuoteExpired_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Muted("SupplierBookingQuoteExpired_Hint")
            .Button("SupplierBookingQuoteExpired_Cta", showcaseUrl)
            .Build("SupplierBookingQuoteExpired_Subject", supplierName);
    }

    /// <summary>
    /// The customer did not answer the quote and the request was cancelled, to the supplier (Italian). Not the words of the request nobody
    /// answered: here the supplier did answer, and it is the customer who did not (decision D24).
    /// </summary>
    public static EmailContent SupplierBookingQuoteLapsed(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierBookingQuoteLapsed_Body", serviceName, comune);
        CustomerLine(builder, customerShortName);
        return builder
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierBookingQuoteLapsed_Subject", comune);
    }
}
