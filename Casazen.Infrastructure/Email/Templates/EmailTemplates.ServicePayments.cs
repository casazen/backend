using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The payment of a service request inside CasaZen (SP-15a, decisions D2, D3 and D24): the request and the reminder to the payer,
/// the receipt to the supplier, and the notice to the host when the supplier records a payment received outside CasaZen. The
/// texts promise nothing about when the supplier is paid out (the payout follows the supplier's Stripe settings); the split is
/// "price gross, CasaZen commission, net to the supplier". Values are HTML-encoded by <see cref="EmailHtmlBuilder"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string ServicePaymentRequest = "service-payment-request";
        public const string ServicePaymentReminder = "service-payment-reminder";
        public const string ServicePaymentReceived = "service-payment-received";
        public const string ServicePaymentOfflineRecorded = "service-payment-offline-recorded";
    }

    /// <summary>
    /// The supplier completed a job and asks for its payment, to the payer: who, what, where, how much, and the personal link to
    /// pay online on the supplier's Stripe account. The link stops working at <paramref name="validUntilUtc"/>.
    /// </summary>
    public static EmailContent ServicePaymentRequest(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string propertyName,
        int amountCents,
        DateTime validUntilUtc,
        string payUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("ServicePaymentRequest_Body", supplierName, serviceName, propertyName)
            .Paragraph("ServicePayment_Amount", FormatEuro(amountCents, culture))
            .Button("ServicePayment_Cta", payUrl)
            .LinkFallback("ServicePayment_LinkFallback", payUrl)
            .Muted("ServicePayment_Validity", builder.FormatInstant(validUntilUtc))
            .Muted("ServicePayment_Direct")
            .Muted("ServicePayment_Personal")
            .Build("ServicePaymentRequest_Subject", serviceName, supplierName);
    }

    /// <summary>
    /// A reminder to the payer of a payment that is still to be made (the supplier asked again, or the daily job of SP-15b): the
    /// same facts and a new personal link, which replaces the previous one.
    /// </summary>
    public static EmailContent ServicePaymentReminder(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string propertyName,
        int amountCents,
        DateTime validUntilUtc,
        string payUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("ServicePaymentReminder_Body", supplierName, serviceName, propertyName)
            .Paragraph("ServicePayment_Amount", FormatEuro(amountCents, culture))
            .Button("ServicePayment_Cta", payUrl)
            .LinkFallback("ServicePayment_LinkFallback", payUrl)
            .Muted("ServicePayment_Validity", builder.FormatInstant(validUntilUtc))
            .Muted("ServicePayment_Direct")
            .Muted("ServicePayment_Personal")
            .Build("ServicePaymentReminder_Subject", serviceName);
    }

    /// <summary>
    /// A payment was paid online (Stripe confirmed it), to the supplier: the split the supplier gets, as "price gross, CasaZen
    /// commission, net" (the commission line only when one was charged), and the console link. The net is before Stripe's own
    /// fees, and nothing is said about when the money reaches the bank account: that follows the supplier's Stripe settings.
    /// </summary>
    public static EmailContent ServicePaymentReceived(
        CultureInfo culture,
        string serviceName,
        string propertyName,
        int grossCents,
        decimal commissionPercent,
        int commissionCents,
        int netCents,
        DateTime paidAtUtc,
        string consoleUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("ServicePaymentReceived_Body", serviceName, propertyName, builder.FormatInstant(paidAtUtc));
        var lines = new List<(string Key, object?[] Args)> { ("ServicePaymentReceived_Gross", [FormatEuro(grossCents, culture)]) };
        if (commissionCents > 0)
            lines.Add(("ServicePaymentReceived_Commission", [commissionPercent.ToString("0.##", culture), FormatEuro(commissionCents, culture)]));
        lines.Add(("ServicePaymentReceived_Net", [FormatEuro(netCents, culture)]));
        return builder
            .List(lines)
            .Muted("ServicePaymentReceived_Payout")
            .Button("ServicePaymentReceived_Cta", consoleUrl)
            .Build("ServicePaymentReceived_Subject", serviceName);
    }

    /// <summary>
    /// The supplier recorded a payment received outside CasaZen, to the host that owed it: the request is now paid. When the
    /// request was to be paid inside CasaZen the supplier's reason is shown (the trace of the exception, decision D5).
    /// </summary>
    public static EmailContent ServicePaymentOfflineRecorded(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string propertyName,
        int amountCents,
        string? reason)
    {
        return new EmailHtmlBuilder(culture)
            .Paragraph("ServicePaymentOfflineRecorded_Body", supplierName, serviceName, propertyName, FormatEuro(amountCents, culture))
            .Quote("ServicePaymentOfflineRecorded_ReasonLabel", reason)
            .Muted("ServicePaymentOfflineRecorded_Hint")
            .Build("ServicePaymentOfflineRecorded_Subject", serviceName);
    }
}
