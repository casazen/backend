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

        public const string SupplierBookingCancellationReceipt = "supplier-booking-cancellation-receipt";
        public const string SupplierBookingProposalExpired = "supplier-booking-proposal-expired";
        public const string SupplierBookingCancelledByCustomer = "supplier-booking-cancelled-by-customer";
        public const string SupplierBookingRescheduledByCustomer = "supplier-booking-rescheduled-by-customer";
        public const string SupplierBookingProposalAnsweredByCustomer = "supplier-booking-proposal-answered-by-customer";
        public const string SupplierBookingProposalLapsed = "supplier-booking-proposal-lapsed";
    }

    public static CultureInfo CultureOf(string? locale) =>
        ServiceCustomerLocales.Normalize(locale) == ServiceCustomerLocales.English ? CultureInfo.GetCultureInfo("en") : DefaultCulture;

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
    /// <paramref name="cardBlocked"/> (SP-15c-a): the customer's card is blocked for the total — the receipt says how much, that nothing is charged
    /// unless the supplier accepts and that the block is released otherwise, instead of the estimate and of "you pay nothing now", which would not
    /// be true.
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
        string requestUrl,
        bool cardBlocked = false)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingReceipt_Body", supplierName, serviceName, builder.FormatInstant(startUtc))
            .Paragraph("SupplierBookingReceipt_Code", Casazen.Core.Services.BookingCodes.Format(publicCode))
            .Paragraph("SupplierBookingReceipt_Deadline", supplierName, builder.FormatInstant(respondByUtc));

        if (cardBlocked && estimatedAmountCents is { } blocked)
        {
            builder
                .Paragraph("SupplierBookingReceipt_Blocked", FormatEuro(blocked, culture), supplierName)
                .Muted("SupplierBookingReceipt_BlockedNote");
        }
        else
        {
            if (estimatedAmountCents is { } amount)
                builder.Paragraph("SupplierBookingReceipt_Price", FormatEuro(amount, culture));

            builder.Muted("SupplierBookingReceipt_NoPayment");
        }

        return builder
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierBookingReceipt_Subject", supplierName);
    }

    public static EmailContent SupplierBookingNewRequest(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string comune,
        string customerShortName,
        DateTime startUtc,
        int? estimatedAmountCents,
        DateTime respondByUtc,
        string inboxUrl,
        bool cardBlocked = false)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierBookingNewRequest_Body", serviceName, comune)
            .Paragraph("SupplierBookingNewRequest_Customer", customerShortName)
            .Paragraph("SupplierBookingNewRequest_When", builder.FormatInstant(startUtc));
        if (estimatedAmountCents is { } amount)
            builder.Paragraph(cardBlocked ? "SupplierBookingNewRequest_Blocked" : "SupplierBookingNewRequest_Price", FormatEuro(amount, culture));

        return builder
            .Paragraph("SupplierBookingNewRequest_Deadline", builder.FormatInstant(respondByUtc))
            .Muted("SupplierBookingNewRequest_Hint")
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierBookingNewRequest_Subject", comune);
    }

    public static EmailContent SupplierBookingAccepted(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        int? quotedAmountCents,
        int? estimatedAmountCents,
        bool reminderWillBeSent,
        string requestUrl,
        bool cardCharged = false)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingAccepted_Body", supplierName, serviceName)
            .Paragraph("SupplierBookingAccepted_When", builder.FormatInstant(startUtc));
        if (cardCharged && (quotedAmountCents ?? estimatedAmountCents) is { } charged)
            builder.Paragraph("SupplierBookingAccepted_Charged", FormatEuro(charged, culture));
        else if (quotedAmountCents is { } quoted)
            builder.Paragraph("SupplierBookingAccepted_Quoted", FormatEuro(quoted, culture));
        else if (estimatedAmountCents is { } estimated)
            builder.Paragraph("SupplierBookingAccepted_Estimated", FormatEuro(estimated, culture));
        if (reminderWillBeSent)
            builder.Muted("SupplierBookingAccepted_Reminder", ServiceRequestReminderRules.ReminderTime.ToString("HH:mm", CultureInfo.InvariantCulture));

        return builder
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierBookingAccepted_Subject", supplierName);
    }

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

    public static EmailContent SupplierBookingExpired(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string serviceName,
        DateTime startUtc,
        string showcaseUrl,
        IReadOnlyList<EmailAlternative>? alternatives = null)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierBookingExpired_Body", supplierName, serviceName, builder.FormatInstant(startUtc));
        AppendAlternatives(builder, culture, alternatives);
        return builder
            .Muted("SupplierBookingExpired_Hint")
            .Button("SupplierBookingExpired_Cta", showcaseUrl)
            .Build("SupplierBookingExpired_Subject", supplierName);
    }

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

    private static void CustomerLine(EmailHtmlBuilder builder, string customerShortName)
    {
        if (!string.IsNullOrWhiteSpace(customerShortName))
            builder.Paragraph("SupplierBookingByCustomer_Customer", customerShortName);
    }
}
