using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>Recurring rent of a long-term lease (LT-06, #269).</summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string RentPaymentRequest = "rent-payment-request";
        public const string RentPaymentFailed = "rent-payment-failed";
        public const string RentPaymentReceived = "rent-payment-received";
        public const string RentReminder = "rent-reminder";
    }

    /// <summary>
    /// An installment is coming due, to a tenant: amount, period and due date, with the personal link to pay online on
    /// the landlord's Stripe account. Paying offline as agreed with the landlord stays possible.
    /// </summary>
    public static EmailContent RentPaymentRequest(
        CultureInfo culture,
        string tenantName,
        string propertyName,
        string landlordName,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly dueDate,
        decimal amount,
        string payUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("Rent_Greeting", tenantName)
            .Paragraph(
                "RentPaymentRequest_Body",
                Amount(amount, culture),
                propertyName,
                AsDate(periodStart),
                AsDate(periodEnd),
                AsDate(dueDate))
            .Paragraph("RentPaymentRequest_Landlord", landlordName)
            .Button("RentPayment_Cta", payUrl)
            .LinkFallback("Rent_LinkFallback", payUrl)
            .Muted("Rent_Personal")
            .Build("RentPaymentRequest_Subject", propertyName, AsDate(dueDate));

    /// <summary>An online payment of an installment failed (e.g. a SEPA debit returned), to a tenant: a new link to pay again.</summary>
    public static EmailContent RentPaymentFailed(
        CultureInfo culture,
        string tenantName,
        string propertyName,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal amount,
        string payUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("Rent_Greeting", tenantName)
            .Paragraph(
                "RentPaymentFailed_Body",
                Amount(amount, culture),
                propertyName,
                AsDate(periodStart),
                AsDate(periodEnd))
            .Button("RentPayment_Cta", payUrl)
            .LinkFallback("Rent_LinkFallback", payUrl)
            .Muted("Rent_Personal")
            .Build("RentPaymentFailed_Subject", propertyName);

    /// <summary>An installment was paid online (Stripe confirmed it), to a landlord, with the link to the lease.</summary>
    public static EmailContent RentPaymentReceived(
        CultureInfo culture,
        string propertyName,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal amount,
        DateOnly paidOn,
        string leaseUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph(
                "RentPaymentReceived_Body",
                Amount(amount, culture),
                propertyName,
                AsDate(periodStart),
                AsDate(periodEnd),
                AsDate(paidOn))
            .Paragraph("RentPaymentReceived_Payout")
            .Button("RentPaymentReceived_Cta", leaseUrl)
            .Build("RentPaymentReceived_Subject", propertyName, AsDate(periodStart));

    /// <summary>
    /// The landlord reminds a tenant that an installment is not paid (LR-01, B1): amount, property, period and due date (with the
    /// wording of "overdue" once it has passed), the landlord's optional short note quoted as written (HTML-encoded, never
    /// markup), and either the personal payment link, when the org accepts rent online, or the invitation to pay the way the
    /// tenant agreed with the landlord.
    /// </summary>
    public static EmailContent RentReminder(
        CultureInfo culture,
        string tenantName,
        string propertyName,
        string landlordName,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly dueDate,
        decimal amount,
        bool overdue,
        string? note,
        string? payUrl)
    {
        var builder = new EmailHtmlBuilder(culture)
            .Paragraph("Rent_Greeting", tenantName)
            .Paragraph(
                overdue ? "RentReminder_BodyOverdue" : "RentReminder_BodyDue",
                Amount(amount, culture),
                propertyName,
                AsDate(periodStart),
                AsDate(periodEnd),
                AsDate(dueDate))
            .Quote("RentReminder_NoteLabel", note);

        if (payUrl is null)
        {
            builder.Paragraph("RentReminder_NoLink", landlordName).Muted("RentReminder_AlreadyPaid");
        }
        else
        {
            builder
                .Paragraph("RentPaymentRequest_Landlord", landlordName)
                .Button("RentPayment_Cta", payUrl)
                .LinkFallback("Rent_LinkFallback", payUrl)
                .Muted("Rent_Personal");
        }

        return builder.Build("RentReminder_Subject", propertyName, AsDate(dueDate));
    }

    /// <summary>Date-only values are formatted as dates (midnight UTC of the day, no time zone conversion).</summary>
    private static DateTime AsDate(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
