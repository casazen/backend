using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mails of the request on measure a customer without an account makes from a supplier's public showcase (SP-10b): the check of
/// the address, the receipt, and the new request to the supplier. Texts in <c>EmailTexts.resx</c> / <c>EmailTexts.en.resx</c> (keys
/// <c>SupplierInquiry*</c>); the customer's mails are in the language the customer chose, the supplier's in Italian like every mail to a
/// supplier. Every dynamic value is HTML-encoded by the builder; every link is built by <see cref="PublicSiteLinks"/> and passed in.
/// </summary>
/// <remarks>
/// <b>Only what is true</b> (decision D24): the deadline named is the one the expiry job enforces, nothing says "the supplier answers in an
/// hour", nothing is said about a price (there is none) and <b>never a word of the customer's message</b>: what the customer wrote is read
/// in the request, by the two parties, and in no e-mail, push or log (decision D9 and the privacy of the free text).
/// </remarks>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string SupplierInquiryVerification = "supplier-inquiry-verification";
        public const string SupplierInquiryReceipt = "supplier-inquiry-receipt";
        public const string SupplierInquiryNewRequest = "supplier-inquiry-new-request";
        public const string SupplierInquiryReply = "supplier-inquiry-reply";
        public const string SupplierInquiryDeclined = "supplier-inquiry-declined";
        public const string SupplierInquiryExpired = "supplier-inquiry-expired";
        public const string SupplierInquiryMessage = "supplier-inquiry-message";
        public const string SupplierInquiryClosedByCustomer = "supplier-inquiry-closed-by-customer";
        public const string SupplierInquiryOfferExpired = "supplier-inquiry-offer-expired";
        public const string SupplierInquiryOfferNotCompleted = "supplier-inquiry-offer-not-completed";
        public const string SupplierInquiryReplyReminder = "supplier-inquiry-reply-reminder";
    }

    public static EmailContent SupplierInquiryVerification(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string confirmUrl,
        int validMinutes)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryVerification_Body", supplierName)
            .Muted("SupplierInquiryVerification_Valid", validMinutes)
            .Button("SupplierInquiryVerification_Cta", confirmUrl)
            .LinkFallback("SupplierBookingVerification_LinkFallback", confirmUrl)
            .Muted("SupplierInquiryVerification_Ignore")
            .Build("SupplierInquiryVerification_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryReceipt(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string publicCode,
        DateTime respondByUtc,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryReceipt_Body", supplierName)
            .Paragraph("SupplierBookingReceipt_Code", Casazen.Core.Services.BookingCodes.Format(publicCode))
            .Paragraph("SupplierInquiryReceipt_Deadline", supplierName, builder.FormatInstant(respondByUtc))
            .Muted("SupplierBookingReceipt_NoPayment")
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierInquiryReceipt_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryReply(CultureInfo culture, string customerName, string supplierName, string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryReply_Body", supplierName)
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierInquiryReply_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryDeclined(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string? reason,
        string showcaseUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryDeclined_Body", supplierName)
            .Quote("SupplierBooking_ReasonLabel", reason)
            .Muted("SupplierInquiryDeclined_Hint")
            .Button("SupplierInquiryClosed_Cta", showcaseUrl)
            .Build("SupplierInquiryDeclined_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryExpired(
        CultureInfo culture,
        string customerName,
        string supplierName,
        string showcaseUrl,
        IReadOnlyList<EmailAlternative>? alternatives = null)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryExpired_Body", supplierName);
        AppendAlternatives(builder, culture, alternatives);
        return builder
            .Muted("SupplierInquiryExpired_Hint")
            .Button("SupplierInquiryClosed_Cta", showcaseUrl)
            .Build("SupplierInquiryExpired_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryOfferExpired(CultureInfo culture, string customerName, string supplierName, string showcaseUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryOfferExpired_Body", supplierName)
            .Muted("SupplierInquiryOfferExpired_Hint")
            .Button("SupplierInquiryClosed_Cta", showcaseUrl)
            .Build("SupplierInquiryOfferExpired_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryOfferNotCompleted(
        CultureInfo culture,
        string customerName,
        string supplierName,
        bool timeGone,
        string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryOfferNotCompleted_Body", supplierName)
            .Paragraph(timeGone ? "SupplierInquiryOfferNotCompleted_TimeGone" : "SupplierInquiryOfferNotCompleted_Payment")
            .Muted("SupplierInquiryOfferNotCompleted_Hint")
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierInquiryOfferNotCompleted_Subject", supplierName);
    }

    public static EmailContent SupplierInquiryReplyReminder(
        CultureInfo culture,
        string supplierName,
        string comune,
        string customerShortName,
        DateTime respondByUtc,
        bool lastCall,
        string inboxUrl)
    {
        var prefix = lastCall ? "SupplierInquiryLastCall" : "SupplierInquiryReminder";
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph($"{prefix}_Body", comune);
        if (!string.IsNullOrWhiteSpace(customerShortName))
            builder.Paragraph("SupplierBookingNewRequest_Customer", customerShortName);

        return builder
            .Paragraph("SupplierInquiryReminder_Deadline", builder.FormatInstant(respondByUtc))
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build($"{prefix}_Subject", comune);
    }

    public static EmailContent SupplierInquiryMessage(
        CultureInfo culture,
        string supplierName,
        string comune,
        string customerShortName,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierInquiryMessage_Body", customerShortName, comune)
            .Muted("SupplierInquiryMessage_Hint")
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierInquiryMessage_Subject", comune);
    }

    public static EmailContent SupplierInquiryClosedByCustomer(
        CultureInfo culture,
        string supplierName,
        string comune,
        string customerShortName,
        string? reason,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierInquiryClosedByCustomer_Body", customerShortName, comune)
            .Quote("SupplierBooking_ReasonLabel", reason)
            .Muted("SupplierInquiryClosedByCustomer_Hint")
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierInquiryClosedByCustomer_Subject", comune);
    }

    public static EmailContent SupplierInquiryNewRequest(
        CultureInfo culture,
        string supplierName,
        string? serviceName,
        string comune,
        string customerShortName,
        DateTime respondByUtc,
        string inboxUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder
            .Paragraph("SupplierBooking_Greeting", supplierName)
            .Paragraph("SupplierInquiryNewRequest_Body", comune);
        if (!string.IsNullOrWhiteSpace(serviceName))
            builder.Paragraph("SupplierInquiryNewRequest_Service", serviceName);

        return builder
            .Paragraph("SupplierBookingNewRequest_Customer", customerShortName)
            .Paragraph("SupplierInquiryNewRequest_Deadline", builder.FormatInstant(respondByUtc))
            .Muted("SupplierInquiryNewRequest_Hint")
            .Button("SupplierBookingNewRequest_Cta", inboxUrl)
            .Build("SupplierInquiryNewRequest_Subject", comune);
    }
}
