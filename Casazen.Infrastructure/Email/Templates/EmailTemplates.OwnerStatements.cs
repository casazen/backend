using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mail that tells an owner a statement was published (E13/F01/US02/T02, NF-111): the agency and the link to the page «Contabilità» of the owner portal,
/// <b>never an amount, a line, a period, a stay or a guest</b> (the statement is in the portal, behind the sign-in of the owner).
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OwnerStatementPublished = "owner-statement-published";
    }

    /// <summary>A new statement is in the portal: the agency that published it and the link, nothing of its content.</summary>
    public static EmailContent OwnerStatementPublished(CultureInfo culture, string agencyName, string accountingUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OwnerStatementPublished_Body", agencyName)
            .Button("OwnerStatementPublished_Cta", accountingUrl)
            .LinkFallback("OwnerStatementPublished_LinkFallback", accountingUrl)
            .Build("OwnerStatementPublished_Subject", agencyName);
}
