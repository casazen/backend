using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The money CasaZen holds for the services of the suppliers (SP-15c-a, decision VT-D1): the alert to the admins and the notice to a supplier whose
/// payment cannot be transferred yet. The texts say only what is true (decision D24): they promise nothing about when the supplier's bank account
/// is credited (the funds become the supplier's when Stripe's do) and nothing about a refund's time. Values are HTML-encoded by
/// <see cref="EmailHtmlBuilder"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string ServiceFundsAlert = "service-funds-alert";
        public const string ServiceFundsAccountNotReady = "service-funds-account-not-ready";
    }

    public static EmailContent ServiceFundsAlert(
        CultureInfo culture,
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
            ("ServicePaymentAlert_Payment", [paymentId.ToString("D")]),
            ("ServicePaymentAlert_Request", [requestId.ToString("D")]),
        };
        if (!string.IsNullOrWhiteSpace(detail))
            lines.Add(("ServicePaymentAlert_Detail", [detail]));

        return new EmailHtmlBuilder(culture)
            .Paragraph("ServiceFundsAlert_Body")
            .List(lines)
            .Muted("ServiceFundsAlert_Action")
            .Build("ServiceFundsAlert_Subject", supplierName);
    }

    public static EmailContent ServiceFundsAccountNotReady(CultureInfo culture, string serviceName, int netCents, string settingsUrl)
    {
        var amount = FormatEuro(netCents, culture);
        return new EmailHtmlBuilder(culture)
            .Paragraph("ServiceFundsAccountNotReady_Body", serviceName, amount)
            .Button("ServiceFundsAccountNotReady_Cta", settingsUrl)
            .LinkFallback("ServicePayment_LinkFallback", settingsUrl)
            .Build("ServiceFundsAccountNotReady_Subject", amount);
    }
}
