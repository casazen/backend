using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        public const string OwnerStatementPublished = "owner-statement-published";
    }

    public static EmailContent OwnerStatementPublished(CultureInfo culture, string agencyName, string accountingUrl) =>
        new EmailHtmlBuilder(culture)
            .Paragraph("OwnerStatementPublished_Body", agencyName)
            .Button("OwnerStatementPublished_Cta", accountingUrl)
            .LinkFallback("OwnerStatementPublished_LinkFallback", accountingUrl)
            .Build("OwnerStatementPublished_Subject", agencyName);
}
