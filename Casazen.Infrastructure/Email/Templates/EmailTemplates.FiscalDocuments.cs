using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string FiscalDocumentDelivery = "fiscal-document-delivery";
    }

    public static EmailContent FiscalDocumentDelivery(
        CultureInfo culture,
        string guestName,
        string issuerName,
        FiscalDocumentKind kind,
        string number,
        DateTime issueDate,
        string documentUrl,
        DateTime expiresAtUtc,
        GuestEmailBranding? branding = null)
    {
        var builder = new EmailHtmlBuilder(culture, branding);
        var label = EmailTexts.Get(FiscalDocumentKindKey(kind), culture);
        return builder
            .Paragraph(string.IsNullOrWhiteSpace(guestName) ? "FiscalDocumentDelivery_GreetingNoName" : "FiscalDocumentDelivery_Greeting", guestName)
            .Paragraph("FiscalDocumentDelivery_Body", issuerName, label, number, issueDate)
            .Button("FiscalDocumentDelivery_Cta", documentUrl)
            .LinkFallback("FiscalDocumentDelivery_LinkFallback", documentUrl)
            .Muted("FiscalDocumentDelivery_Validity", builder.FormatInstant(expiresAtUtc))
            .Muted("FiscalDocumentDelivery_NotYou")
            .Build("FiscalDocumentDelivery_Subject", label, number, issuerName);
    }

    private static string FiscalDocumentKindKey(FiscalDocumentKind kind) => kind switch
    {
        FiscalDocumentKind.Receipt => "FiscalDocumentDelivery_Kind_Receipt",
        FiscalDocumentKind.CreditNote => "FiscalDocumentDelivery_Kind_CreditNote",
        _ => "FiscalDocumentDelivery_Kind_Invoice",
    };
}
