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

    /// <summary>
    /// First mail of the request on measure, to the customer: the link that checks the address. Until the customer follows it the supplier
    /// receives nothing.
    /// </summary>
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

    /// <summary>
    /// The address is checked and the request is with the supplier: the receipt to the customer, with the code to find it again and the
    /// time by which the supplier has to answer (after it the request closes).
    /// </summary>
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

    /// <summary>
    /// The supplier replied in the thread, to the customer: that there is a reply and where to read it. <b>Never the words</b> of the reply:
    /// they are read in the request, by the two parties.
    /// </summary>
    public static EmailContent SupplierInquiryReply(CultureInfo culture, string customerName, string supplierName, string requestUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("SupplierBooking_Greeting", customerName)
            .Paragraph("SupplierInquiryReply_Body", supplierName)
            .Button("SupplierBooking_RequestCta", requestUrl)
            .Build("SupplierInquiryReply_Subject", supplierName);
    }

    /// <summary>The supplier cannot take the request on measure, to the customer, with the reason the supplier gave.</summary>
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

    /// <summary>
    /// The supplier did not answer the request on measure in time, so it was closed: to the customer, with the other suppliers that could help when
    /// they were looked for (<paramref name="alternatives"/>, SP-10c: an empty list says that there is none to suggest for now).
    /// </summary>
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

    /// <summary>
    /// The supplier did answer the request on measure, with an offer, and the customer did not answer the offer in time, so the request was closed:
    /// to the customer. Not the words of <see cref="SupplierInquiryExpired"/>, where it is the supplier that did not answer (decision D24).
    /// </summary>
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

    /// <summary>
    /// The customer accepted an offer that is paid inside CasaZen, blocked its card, and the acceptance could not be completed (the time of the offer
    /// is no longer the supplier's to give, or the card could not be charged): to the customer, who is told that <b>nothing was charged</b> (the block is
    /// released), that the request is still open and where to go on.
    /// </summary>
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

    /// <summary>
    /// A request on measure that nobody answered yet, to its supplier (Italian), SP-10c: at half of the time it has (<paramref name="lastCall"/> false)
    /// and ten minutes before the deadline (true). The comune, "Nome C." and the time to answer by: nothing of what the customer wrote, no address, no
    /// contact (decision D9). It is an operational notice, like the reminder of a host: no switch of «Notifiche» hides it, the supplier has to act on it.
    /// <b>Only what is true</b> (decision D24): after the deadline the request is closed and the customer told, and that is all it says.
    /// </summary>
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

    /// <summary>
    /// The customer wrote in the thread, to the supplier (Italian): that there is a message, from whom ("Nome C.") and where to read it.
    /// <b>Never the words</b>: they are read in the request, by the two parties (decision D9 and the privacy of the free text).
    /// </summary>
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

    /// <summary>
    /// The customer closed its request on measure, to the supplier (Italian): who ("Nome C."), where, and the reason the customer wrote. No
    /// day and no time: the request has none.
    /// </summary>
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

    /// <summary>
    /// A request on measure whose address was checked, to the supplier (Italian): the comune, the service when it is about one, who asks as
    /// "Nome C." and the time by which it has to answer. Not the words of the customer, not the address, not the contacts, not the full
    /// name: the words are read in the request, the rest comes with the take (decision D9).
    /// </summary>
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
