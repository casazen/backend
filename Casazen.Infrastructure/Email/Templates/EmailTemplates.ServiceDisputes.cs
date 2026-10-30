using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The complaint about a job paid through CasaZen and its dispute (SP-15c-c, decision VT-D16): what the supplier, the payer and the admins are told
/// when a complaint is opened, answered, decided or taken back. The texts say only what is true (decision D24): they promise nothing about when a
/// refund reaches the payer's bank or when the funds reach the supplier's (that depends on the banks and on Stripe), and they never carry the words of
/// the parties — the payer's and the supplier's texts are read in the app, where they are protected. Values are HTML-encoded by
/// <see cref="EmailHtmlBuilder"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string ServiceDisputeOpenedSupplier = "service-dispute-opened-supplier";
        public const string ServiceDisputeOpenedPayer = "service-dispute-opened-payer";
        public const string ServiceDisputeRepliedPayer = "service-dispute-replied-payer";
        public const string ServiceDisputeResolvedPayer = "service-dispute-resolved-payer";
        public const string ServiceDisputeResolvedSupplier = "service-dispute-resolved-supplier";
        public const string ServiceDisputeWithdrawnSupplier = "service-dispute-withdrawn-supplier";
        public const string ServiceDisputeAdminAlert = "service-dispute-admin-alert";
    }

    /// <summary>What the payer said was wrong, as a phrase the sentence of an e-mail goes on with.</summary>
    public static string ServiceDisputeReasonLabel(CultureInfo culture, ServiceDisputeReason reason) =>
        EmailTexts.Get(
            reason switch
            {
                ServiceDisputeReason.NotDone => "ServiceDisputeReason_NotDone",
                ServiceDisputeReason.Incomplete => "ServiceDisputeReason_Incomplete",
                ServiceDisputeReason.PoorQuality => "ServiceDisputeReason_PoorQuality",
                ServiceDisputeReason.NotAsDescribed => "ServiceDisputeReason_NotAsDescribed",
                _ => "ServiceDisputeReason_Other",
            },
            culture);

    /// <summary>
    /// A complaint was opened, to the supplier: what was reported (the reason, not the words), that the payment is held until it is decided, and until
    /// when it can reply. For a job nobody marked completed (<paramref name="noShow"/>) the sentence says what CasaZen saw, not that anybody is at fault.
    /// </summary>
    public static EmailContent ServiceDisputeOpenedToSupplier(
        CultureInfo culture,
        string serviceName,
        ServiceDisputeReason reason,
        bool noShow,
        int netCents,
        DateTime replyDueUtc,
        string consoleUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        var amount = FormatEuro(netCents, culture);
        if (noShow)
            builder.Paragraph("ServiceDisputeOpenedSupplier_NoShowBody", serviceName, amount);
        else
            builder.Paragraph("ServiceDisputeOpenedSupplier_Body", serviceName, ServiceDisputeReasonLabel(culture, reason), amount);

        return builder
            .Paragraph("ServiceDisputeOpenedSupplier_Reply", builder.FormatInstant(replyDueUtc))
            .Button("ServiceDisputeOpenedSupplier_Cta", consoleUrl)
            .Muted("ServiceDisputeOpenedSupplier_Hint")
            .Build("ServiceDisputeOpenedSupplier_Subject", builder.FormatInstant(replyDueUtc));
    }

    /// <summary>
    /// A complaint was opened, to the payer: received, the money stays held until it is decided, the supplier has until a time to reply. When CasaZen opened
    /// it for a job nobody marked completed (<paramref name="byCasaZen"/>) the sentence says so and that the payer may take it back if the work was done.
    /// <paramref name="manageUrl"/> is the page of the booking of a showcase customer; a host reads it in its own area and gets none.
    /// </summary>
    public static EmailContent ServiceDisputeOpenedToPayer(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        bool byCasaZen,
        DateTime replyDueUtc,
        string? manageUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph(byCasaZen ? "ServiceDisputeOpenedPayer_NoShowBody" : "ServiceDisputeOpenedPayer_Body", serviceName, supplierName);
        builder.Paragraph("ServiceDisputeOpenedPayer_Reply", builder.FormatInstant(replyDueUtc));
        if (manageUrl is not null)
        {
            builder.Button("ServiceDisputeOpenedPayer_Cta", manageUrl)
                .LinkFallback("ServicePayment_LinkFallback", manageUrl);
        }

        return builder.Build("ServiceDisputeOpenedPayer_Subject", serviceName);
    }

    /// <summary>The supplier replied, to the payer: the team of CasaZen reads both and decides; the money stays held until then.</summary>
    public static EmailContent ServiceDisputeRepliedToPayer(CultureInfo culture, string supplierName, string serviceName, string? manageUrl)
    {
        var builder = new EmailHtmlBuilder(culture).Paragraph("ServiceDisputeRepliedPayer_Body", supplierName, serviceName);
        if (manageUrl is not null)
            builder.Button("ServiceDisputeOpenedPayer_Cta", manageUrl);

        return builder.Build("ServiceDisputeRepliedPayer_Subject", serviceName);
    }

    /// <summary>
    /// A complaint was decided, to the payer. For the supplier the payment is paid out; for the customer the amount that goes back is told (and nothing
    /// about when it reaches the bank): with <paramref name="refundCents"/> zero the sentence only says the complaint was accepted.
    /// </summary>
    public static EmailContent ServiceDisputeResolvedToPayer(
        CultureInfo culture,
        string supplierName,
        string serviceName,
        bool forCustomer,
        int refundCents)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (!forCustomer)
            builder.Paragraph("ServiceDisputeResolvedPayer_ForSupplier", serviceName, supplierName);
        else if (refundCents > 0)
            builder.Paragraph("ServiceDisputeResolvedPayer_ForCustomer", serviceName, supplierName, FormatEuro(refundCents, culture)).Muted("ServiceDisputeResolvedPayer_Hint");
        else
            builder.Paragraph("ServiceDisputeResolvedPayer_ForCustomerNoRefund", serviceName, supplierName);

        return builder.Build("ServiceDisputeResolvedPayer_Subject", serviceName);
    }

    /// <summary>
    /// A complaint was decided, to the supplier: in its favour the payment goes to its Stripe account (no promise about when the funds are available there);
    /// against it, what went back to the payer and what, if anything, it is still paid.
    /// </summary>
    public static EmailContent ServiceDisputeResolvedToSupplier(
        CultureInfo culture,
        string serviceName,
        bool forCustomer,
        int netCents,
        int refundCents,
        int remainingCents,
        string consoleUrl)
    {
        var builder = new EmailHtmlBuilder(culture);
        if (!forCustomer)
        {
            builder.Paragraph("ServiceDisputeResolvedSupplier_ForSupplier", serviceName, FormatEuro(netCents, culture));
        }
        else
        {
            builder.Paragraph("ServiceDisputeResolvedSupplier_ForCustomer", serviceName, FormatEuro(refundCents, culture));
            builder.Paragraph(
                remainingCents > 0 ? "ServiceDisputeResolvedSupplier_Remaining" : "ServiceDisputeResolvedSupplier_NothingLeft",
                FormatEuro(remainingCents, culture));
        }

        return builder
            .Button("ServiceDisputeResolvedSupplier_Cta", consoleUrl)
            .Build("ServiceDisputeResolvedSupplier_Subject", serviceName);
    }

    /// <summary>The payer took the complaint back, to the supplier: nothing is wrong, the payment is paid out.</summary>
    public static EmailContent ServiceDisputeWithdrawnToSupplier(CultureInfo culture, string serviceName, int netCents) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("ServiceDisputeWithdrawnSupplier_Body", serviceName, FormatEuro(netCents, culture))
            .Build("ServiceDisputeWithdrawnSupplier_Subject", serviceName);

    /// <summary>
    /// To every active platform admin: a dispute needs a decision (the supplier replied, or did not in time), the target to decide has passed, or the
    /// refund of a decision did not go through. Ids, an amount and a code only: no name, no address, no word of either party.
    /// </summary>
    public static EmailContent ServiceDisputeAdminAlert(
        CultureInfo culture,
        Guid disputeId,
        Guid paymentId,
        Guid requestId,
        string supplierName,
        int amountCents,
        string? detail)
    {
        var lines = new List<(string Key, object?[] Args)>
        {
            ("ServicePaymentAlert_Supplier", [supplierName]),
            ("ServicePaymentAlert_Amount", [FormatEuro(amountCents, culture)]),
            ("ServiceDisputeAlert_Dispute", [disputeId.ToString("D")]),
            ("ServicePaymentAlert_Payment", [paymentId.ToString("D")]),
            ("ServicePaymentAlert_Request", [requestId.ToString("D")]),
        };
        if (!string.IsNullOrWhiteSpace(detail))
            lines.Add(("ServicePaymentAlert_Detail", [detail]));

        return new EmailHtmlBuilder(culture)
            .Paragraph("ServiceDisputeAlert_Body")
            .List(lines)
            .Muted("ServiceDisputeAlert_Action")
            .Build("ServiceDisputeAlert_Subject", supplierName);
    }
}
