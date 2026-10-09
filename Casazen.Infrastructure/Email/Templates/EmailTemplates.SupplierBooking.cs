using System.Globalization;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mails of the booking a customer without an account makes from a supplier's public showcase (SP-10): the check of the
/// address, the receipt, the new request to the supplier, and what happens to the request afterwards (accepted, refused, another
/// time proposed, reminder, cancelled, lapsed). Texts in <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys
/// <c>SupplierBooking*</c>); the customer's mails are in the language the customer chose (<see cref="ServiceCustomerLocales"/>,
/// <see cref="CultureOf"/>), the supplier's in Italian like every mail to a supplier. Every dynamic value is HTML-encoded by the
/// builder; every link is built by <see cref="PublicSiteLinks"/> and passed in.
/// </summary>
/// <remarks>
/// <b>Only what is true</b> (decision D24): the deadlines named are the ones the jobs enforce (the supplier's answer time, the
/// time to check the e-mail, the time to answer a proposal), the reminder is promised only when it will be sent, nothing says
/// "the supplier answers in an hour" and nothing is said about how or when the work is paid: with this request nothing is paid.
/// </remarks>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string SupplierBookingVerification = "supplier-booking-verification";
        public const string SupplierBookingReceipt = "supplier-booking-receipt";
        public const string SupplierBookingNewRequest = "supplier-booking-new-request";
        public const string SupplierBookingAccepted = "supplier-booking-accepted";
        public const string SupplierBookingDeclined = "supplier-booking-declined";
        public const string SupplierBookingTimeProposed = "supplier-booking-time-proposed";
        public const string SupplierBookingReminder = "supplier-booking-reminder";
        public const string SupplierBookingCancelled = "supplier-booking-cancelled";
        public const string SupplierBookingExpired = "supplier-booking-expired";
    }

    /// <summary>
    /// The culture of the e-mails to a customer of the public showcase: English for <c>en</c>, Italian for everything else
    /// (<see cref="ServiceCustomerLocales.Normalize"/>).
    /// </summary>
    public static CultureInfo CultureOf(string? locale) =>
        ServiceCustomerLocales.Normalize(locale) == ServiceCustomerLocales.English ? CultureInfo.GetCultureInfo("en") : DefaultCulture;

    /// <summary>
    /// First mail of the booking, to the customer: the link that checks the address. Until the customer follows it the supplier
    /// receives nothing, and the slot is held for <paramref name="validMinutes"/> minutes.
    /// </summary>
    public static EmailContent SupplierBookingVerification(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        int validMinutes,
        string confirmUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingVerification_Body", serviceName, supplierName, builder.FormatInstant(startUtc))
            .Muted("SupplierBookingVerification_Hold", validMinutes)
            .Button("SupplierBookingVerification_Cta", confirmUrl)
            .LinkFallback("SupplierBookingVerification_LinkFallback", confirmUrl)
            .Muted("SupplierBookingVerification_Ignore")
            .Build("SupplierBookingVerification_Subject", supplierName);
    }

    /// <summary>
    /// The address is checked and the request is with the supplier: the receipt to the customer, with the code to manage it, the
    /// time by which the supplier has to answer (after it the request lapses) and, when the service had a price, the estimate.
    /// </summary>
    public static EmailContent SupplierBookingReceipt(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string publicCode,
        DateTime respondByUtc,
        int? estimatedAmountCents,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingReceipt_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Paragraph("SupplierBookingReceipt_Code", Casazen.Core.Services.BookingCodes.Format(publicCode))
            .Paragraph("SupplierBookingReceipt_Deadline", supplierName, builder.FormatInstant(respondByUtc));
        if (estimatedAmountCents is { } amount)
            builder.Paragraph("SupplierBookingReceipt_Price", FormatEuro(amount, culture));

        return builder
            .Muted("SupplierBookingReceipt_NoPayment")
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierBookingReceipt_Subject", supplierName);
    }

    /// <summary>
    /// A booking whose address was checked, to the supplier (Italian): the comune, the day and time, the estimate, who asks as
    /// "Nome C." and the time by which it has to answer. Not the address, not the contacts, not the full name: they come with
    /// the take (decision D9).
    /// </summary>
    public static EmailContent SupplierBookingNewRequest(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        DateTime startUtc,
        int? estimatedAmountCents,
        DateTime respondByUtc,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierBookingNewRequest_Body", serviceName, comune)
            .Paragraph("SupplierBookingNewRequest_Customer", customerShortName)
            .Paragraph("SupplierBookingNewRequest_When", builder.FormatInstant(startUtc));
        if (estimatedAmountCents is { } amount)
            builder.Paragraph("SupplierBookingNewRequest_Price", FormatEuro(amount, culture));

        return builder
            .Paragraph("SupplierBookingNewRequest_Deadline", builder.FormatInstant(respondByUtc))
            .Muted("SupplierBookingNewRequest_Hint")
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierBookingNewRequest_Subject", comune);
    }

    /// <summary>
    /// The supplier accepted the request, to the customer: when, the price (the supplier's own when it gave one, else the
    /// estimate) and, only when it will really be sent, the time of the reminder of the day before.
    /// </summary>
    public static EmailContent SupplierBookingAccepted(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        int? quotedAmountCents,
        int? estimatedAmountCents,
        bool reminderWillBeSent,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingAccepted_Body", supplierName, serviceName)
            .Paragraph("SupplierBookingAccepted_When", builder.FormatInstant(startUtc));
        if (quotedAmountCents is { } quoted)
            builder.Paragraph("SupplierBookingAccepted_Quoted", FormatEuro(quoted, culture));
        else if (estimatedAmountCents is { } estimated)
            builder.Paragraph("SupplierBookingAccepted_Estimated", FormatEuro(estimated, culture));
        if (reminderWillBeSent)
            builder.Muted("SupplierBookingAccepted_Reminder", ServiceRequestReminderRules.ReminderTime.ToString("HH:mm", CultureInfo.InvariantCulture));

        return builder
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierBookingAccepted_Subject", supplierName);
    }

    /// <summary>The supplier cannot accept the request, to the customer, with the reason the supplier gave.</summary>
    public static EmailContent SupplierBookingDeclined(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string? reason,
        string showcaseUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingDeclined_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Quote("SupplierBooking_ReasonLabel", reason)
            .Muted("SupplierBookingDeclined_Hint")
            .Button("SupplierBookingDeclined_Cta", showcaseUrl)
            .Build("SupplierBookingDeclined_Subject", supplierName);
    }

    /// <summary>
    /// The supplier proposes another time, to the customer: the time it cannot do, the one it proposes, its message, and the time
    /// by which the customer has to answer (after it the request lapses).
    /// </summary>
    public static EmailContent SupplierBookingTimeProposed(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime currentStartUtc,
        DateTime proposedStartUtc,
        string? message,
        DateTime answerByUtc,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph(
                "SupplierBookingTimeProposed_Body",
                supplierName,
                serviceName,
                builder.FormatInstant(currentStartUtc),
                builder.FormatInstant(proposedStartUtc))
            .Quote("SupplierBookingTimeProposed_MessageLabel", message)
            .Paragraph("SupplierBookingTimeProposed_Deadline", builder.FormatInstant(answerByUtc))
            .Button("SupplierBookingTimeProposed_Cta", requestUrl)
            .Build("SupplierBookingTimeProposed_Subject", supplierName);
    }

    /// <summary>The reminder of the day before, to the customer of a request the supplier took.</summary>
    public static EmailContent SupplierBookingReminder(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingReminder_Body", serviceName, supplierName, builder.FormatInstant(startUtc))
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierBookingReminder_Subject", serviceName, supplierName);
    }

    /// <summary>The supplier cancelled the request, to the customer, with the reason the supplier gave.</summary>
    public static EmailContent SupplierBookingCancelled(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string? reason,
        string showcaseUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingCancelled_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Quote("SupplierBooking_ReasonLabel", reason)
            .Muted("SupplierBookingCancelled_Hint")
            .Button("SupplierBookingCancelled_Cta", showcaseUrl)
            .Build("SupplierBookingCancelled_Subject", supplierName);
    }

    /// <summary>Nobody answered in time and CasaZen cancelled the request, to the customer.</summary>
    public static EmailContent SupplierBookingExpired(
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
            .Paragraph("SupplierBookingExpired_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Muted("SupplierBookingExpired_Hint")
            .Button("SupplierBookingExpired_Cta", showcaseUrl)
            .Build("SupplierBookingExpired_Subject", supplierName);
    }
}
