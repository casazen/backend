using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mail that sends the guest the document of the stay (E12/F01/US02/T01, NF-83): who sent it, what it is, its number and day, and the link of the public page that shows it and gives
/// the PDF. <b>Never an amount, an address or a fiscal datum, and never the name of a portal or of a supplier</b>: the page behind the link has the rest. Every dynamic value is HTML-encoded by
/// <see cref="EmailHtmlBuilder"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>The document of a stay sent to the guest with its signed link (E12/F01/US02/T01).</summary>
        public const string FiscalDocumentDelivery = "fiscal-document-delivery";
    }

    /// <summary>
    /// A document of the stay, to the guest. <paramref name="guestName"/> is the first name (a generic greeting when it is empty), <paramref name="issuerName"/> who issued the document,
    /// <paramref name="issueDate"/> its day (a calendar day, no time zone) and <paramref name="documentUrl"/> the signed link, which stops opening at <paramref name="expiresAtUtc"/> (shown in Italian time).
    /// </summary>
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

    /// <summary>The key of the word for a kind of document: a receipt and a credit note have their own, every other document (an invoice) is an invoice.</summary>
    private static string FiscalDocumentKindKey(FiscalDocumentKind kind) => kind switch
    {
        FiscalDocumentKind.Receipt => "FiscalDocumentDelivery_Kind_Receipt",
        FiscalDocumentKind.CreditNote => "FiscalDocumentDelivery_Kind_CreditNote",
        _ => "FiscalDocumentDelivery_Kind_Invoice",
    };
}
