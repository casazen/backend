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

        // The customer's own area of a booking (SP-11).
        public const string SupplierBookingCancellationReceipt = "supplier-booking-cancellation-receipt";
        public const string SupplierBookingProposalExpired = "supplier-booking-proposal-expired";
        public const string SupplierBookingCancelledByCustomer = "supplier-booking-cancelled-by-customer";
        public const string SupplierBookingRescheduledByCustomer = "supplier-booking-rescheduled-by-customer";
        public const string SupplierBookingProposalAnsweredByCustomer = "supplier-booking-proposal-answered-by-customer";
        public const string SupplierBookingProposalLapsed = "supplier-booking-proposal-lapsed";
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

    // ─── The customer's own area of a booking (SP-11) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The customer cancelled its booking, to the customer: the receipt, with the reason it wrote (when it wrote one) and the words
    /// that nothing was paid and nothing is charged (decision D6: no exit cost in v1, so a late cancellation is the same).
    /// </summary>
    public static EmailContent SupplierBookingCancellationReceipt(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string publicCode,
        string? reason,
        string showcaseUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingCancellationReceipt_Body", serviceName, supplierName, builder.FormatInstant(startUtc))
            .Paragraph("SupplierBookingCancellationReceipt_Code", Casazen.Core.Services.BookingCodes.Format(publicCode))
            .Quote("SupplierBookingCancellationReceipt_ReasonLabel", reason)
            .Muted("SupplierBookingCancellationReceipt_Hint")
            .Button("SupplierBookingCancellationReceipt_Cta", showcaseUrl)
            .Build("SupplierBookingCancellationReceipt_Subject", supplierName);
    }

    /// <summary>
    /// The customer did not answer the other time the supplier proposed, and the request was cancelled, to the customer. Not the
    /// words of <see cref="SupplierBookingExpired"/>: there it is the supplier that did not answer, here it is the customer (decision D24).
    /// </summary>
    /// <param name="startUtc">The time the customer had asked for (the request keeps it while a proposal waits).</param>
    public static EmailContent SupplierBookingProposalExpired(
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
            .Paragraph("SupplierBookingProposalExpired_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Muted("SupplierBookingProposalExpired_Hint")
            .Button("SupplierBookingProposalExpired_Cta", showcaseUrl)
            .Build("SupplierBookingProposalExpired_Subject", supplierName);
    }

    /// <summary>
    /// The customer cancelled a request, to the supplier (Italian): the comune, the service and the time, "Nome C." and the reason it
    /// wrote. When the supplier had taken the request and the cancellation came with less notice than the free cancellation asks
    /// for, the mail says so (<paramref name="shortNoticeHours"/>); nothing else is claimed about it.
    /// </summary>
    public static EmailContent SupplierBookingCancelledByCustomer(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        DateTime startUtc,
        string? reason,
        int? shortNoticeHours,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierBookingCancelledByCustomer_Body", serviceName, comune, builder.FormatInstant(startUtc));
        CustomerLine(builder, customerShortName);
        builder.Quote("SupplierBooking_ReasonLabel", reason);
        if (shortNoticeHours is { } hours)
            builder.Paragraph("SupplierBookingCancelledByCustomer_ShortNotice", hours);

        return builder
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierBookingCancelledByCustomer_Subject", comune);
    }

    /// <summary>
    /// The customer moved a new request to another time, to the supplier (Italian): from when to when, that the request waits again
    /// and until when the supplier has to answer, and — when it had proposed another time — that the proposal no longer applies.
    /// </summary>
    public static EmailContent SupplierBookingRescheduledByCustomer(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        DateTime previousStartUtc,
        DateTime newStartUtc,
        DateTime respondByUtc,
        bool proposalDropped,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph(
                "SupplierBookingRescheduledByCustomer_Body",
                serviceName,
                comune,
                builder.FormatInstant(previousStartUtc),
                builder.FormatInstant(newStartUtc));
        CustomerLine(builder, customerShortName);
        if (proposalDropped)
            builder.Paragraph("SupplierBookingRescheduledByCustomer_Dropped");

        return builder
            .Paragraph("SupplierBookingRescheduledByCustomer_Deadline", builder.FormatInstant(respondByUtc))
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierBookingRescheduledByCustomer_Subject", comune);
    }

    /// <summary>
    /// The customer answered the time the supplier proposed, to the supplier (Italian). Accepted: the new time, and that the request
    /// is taken. Turned down: the request stays at the time first asked for, and the supplier has until
    /// <paramref name="respondByUtc"/> to answer it.
    /// </summary>
    /// <param name="startUtc">The new time when <paramref name="accepted"/>, the time first asked for when not.</param>
    /// <param name="respondByUtc">The supplier's new deadline; named only when the proposal was turned down.</param>
    public static EmailContent SupplierBookingProposalAnsweredByCustomer(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        bool accepted,
        DateTime startUtc,
        DateTime? respondByUtc,
        string inboxUrl)
    {
        var prefix = accepted ? "SupplierBookingProposalAccepted" : "SupplierBookingProposalRejected";
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("SupplierBooking_Greeting", supplierName);
        if (accepted)
        {
            builder.Paragraph($"{prefix}_Body", serviceName, comune, builder.FormatInstant(startUtc));
        }
        else
        {
            builder.Paragraph(
                $"{prefix}_Body",
                serviceName,
                comune,
                builder.FormatInstant(startUtc),
                builder.FormatInstant(respondByUtc ?? startUtc));
        }

        CustomerLine(builder, customerShortName);
        return builder
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build($"{prefix}_Subject", comune);
    }

    /// <summary>
    /// The customer did not answer the time the supplier proposed and the request was cancelled, to the supplier (Italian). Not the
    /// words of the request nobody answered: here the supplier did answer, and it is the customer who did not (decision D24).
    /// </summary>
    public static EmailContent SupplierBookingProposalLapsed(
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
            .Paragraph("SupplierBookingProposalLapsed_Body", serviceName, comune);
        CustomerLine(builder, customerShortName);
        return builder
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierBookingProposalLapsed_Subject", comune);
    }

    /// <summary>"Cliente: Nome C." — left out when there is no name to show (decision D9: never more than the short name).</summary>
    private static void CustomerLine(EmailHtmlBuilder builder, string customerShortName)
    {
        if (!string.IsNullOrWhiteSpace(customerShortName))
            builder.Paragraph("SupplierBookingByCustomer_Customer", customerShortName);
    }
}
