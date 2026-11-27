using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

/// <summary>
/// The e-mail that tells the accountant of an organization its package is ready (E15/F01/US02/T02, NF-101): who prepared it, which month it is of, the signed link that downloads it and the last day the link
/// opens. <b>The queue has no attachments and the package holds financial data, so the e-mail carries the link, never the package</b>, and never an amount, a name of a guest or an owner, a document or a
/// figure. Every dynamic value is HTML-encoded by <see cref="EmailHtmlBuilder"/>.
/// </summary>
public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>The package of a month sent to the accountant with its signed link (E15/F01/US02/T02).</summary>
        public const string AccountantPackage = "accountant-package";
    }

    /// <summary>
    /// The package of <paramref name="period"/> (<c>yyyy-MM</c>, written as the month name of the culture) prepared by <paramref name="orgName"/>, to its accountant. <paramref name="downloadUrl"/> is the signed link, which
    /// stops opening at <paramref name="linkExpiresAtUtc"/> (shown in Italian time); the e-mail says the package is not attached and that the link is personal.
    /// </summary>
    public static EmailContent AccountantPackage(CultureInfo culture, string orgName, string period, string downloadUrl, DateTime linkExpiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        var month = MonthLabel(period, culture);
        return builder
            .Paragraph("AccountantPackage_Greeting")
            .Paragraph("AccountantPackage_Body", orgName, month)
            .Button("AccountantPackage_Cta", downloadUrl)
            .LinkFallback("AccountantPackage_LinkFallback", downloadUrl)
            .Muted("AccountantPackage_Validity", builder.FormatInstant(linkExpiresAtUtc))
            .Muted("AccountantPackage_NoAttachment")
            .Muted("AccountantPackage_NotYou", orgName)
            .Build("AccountantPackage_Subject", month, orgName);
    }
}
