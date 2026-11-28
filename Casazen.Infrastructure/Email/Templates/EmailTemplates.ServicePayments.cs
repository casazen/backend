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
        public const string ServicePaymentFailed = "service-payment-failed";
        public const string ServicePaymentRefundedPayer = "service-payment-refunded-payer";
        public const string ServicePaymentRefundedSupplier = "service-payment-refunded-supplier";
        public const string ServicePaymentAdminAlert = "service-payment-admin-alert";
    }

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

    public static EmailContent ServicePaymentFailed(
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
            .Paragraph("ServicePaymentFailed_Body", supplierName, serviceName, propertyName)
            .Paragraph("ServicePayment_Amount", FormatEuro(amountCents, culture))
            .Button("ServicePayment_Cta", payUrl)
            .LinkFallback("ServicePayment_LinkFallback", payUrl)
            .Muted("ServicePayment_Validity", builder.FormatInstant(validUntilUtc))
            .Muted("ServicePayment_Direct")
            .Muted("ServicePayment_Personal")
            .Build("ServicePaymentFailed_Subject", serviceName);
    }

    public static EmailContent ServicePaymentRefundedToPayer(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        string propertyName,
        int refundedCents)
    {
        var bodyKey = string.IsNullOrWhiteSpace(propertyName) ? "ServicePaymentRefundedCustomer_Body" : "ServicePaymentRefundedPayer_Body";
        return new EmailHtmlBuilder(culture)
            .Paragraph(bodyKey, supplierName, serviceName, propertyName, FormatEuro(refundedCents, culture))
            .Muted("ServicePaymentRefundedPayer_Hint")
            .Build("ServicePaymentRefundedPayer_Subject", serviceName);
    }

    public static EmailContent ServicePaymentRefundedToSupplier(
        CultureInfo culture,
        string serviceName,
        string propertyName,
        int refundedCents,
        int commissionRefundedCents,
        string consoleUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("ServicePaymentRefundedSupplier_Body", serviceName, propertyName, FormatEuro(refundedCents, culture));
        if (commissionRefundedCents > 0)
            builder.Paragraph("ServicePaymentRefundedSupplier_Commission", FormatEuro(commissionRefundedCents, culture));

        return builder
            .Muted("ServicePaymentRefundedSupplier_Hint")
            .Button("ServicePaymentReceived_Cta", consoleUrl)
            .Build("ServicePaymentRefundedSupplier_Subject", serviceName);
    }

    /// <summary>
    /// To the platform admins (SP-15b): a service payment needs them, either because the money that arrived does not match what was
    /// asked for (or arrived for a payment that was withdrawn), or because the payer disputed the charge. Ids, amounts and Stripe's
    /// codes only: no name, no address, no contact of the payer.
    /// </summary>
    public static EmailContent ServicePaymentAdminAlert(
        CultureInfo culture,
        bool dispute,
        Guid paymentId,
        Guid requestId,
        string supplierName,
        int amountCents,
        string? detail)
    {
        var builder = new EmailHtmlBuilder(culture);
        var subjectKey = dispute ? "ServicePaymentAlertDispute_Subject" : "ServicePaymentAlertReview_Subject";
        var bodyKey = dispute ? "ServicePaymentAlertDispute_Body" : "ServicePaymentAlertReview_Body";
        var actionKey = dispute ? "ServicePaymentAlertDispute_Action" : "ServicePaymentAlertReview_Action";

        var lines = new List<(string Key, object?[] Args)>
        {
            ("ServicePaymentAlert_Supplier", [supplierName]),
            ("ServicePaymentAlert_Amount", [FormatEuro(amountCents, culture)]),
            ("ServicePaymentAlert_Payment", [paymentId.ToString("D")]),
            ("ServicePaymentAlert_Request", [requestId.ToString("D")]),
        };
        if (!string.IsNullOrWhiteSpace(detail))
            lines.Add(("ServicePaymentAlert_Detail", [detail]));

        return builder
            .Paragraph(bodyKey)
            .List(lines)
            .Muted(actionKey)
            .Build(subjectKey, supplierName);
    }

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
