using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>The double opt-in e-mail of a marketing consent, to the address that was given (E07/F04/US01/T01).</summary>
        public const string MarketingConfirmation = "marketing-confirmation";
    }

    /// <summary>
    /// The e-mail that asks a person to confirm that an address is theirs and that they want the offers and news of an org (the double opt-in
    /// of the marketing consent, E07/F04/US01/T01, NF-90). It says what the person signed up to and with whom, carries the signed link to the
    /// page that records the confirmation, says how long the link works and that, if it was not the person, ignoring the message is enough and
    /// nothing will be sent. It holds no tracking pixel and names nobody: the address that receives it is the only personal datum, and it is
    /// not in the body. Every dynamic value is HTML-encoded by the builder.
    /// </summary>
    /// <param name="orgName">The org the person subscribed to (its public name).</param>
    /// <param name="confirmUrl">The page of the link, with the signed values in its fragment (<see cref="PublicSiteLinks.MarketingConfirm"/>).</param>
    /// <param name="expiresAtUtc">End of validity of the link (shown in Italian time).</param>
    public static EmailContent MarketingConfirmation(
        CultureInfo culture,
        string orgName,
        string confirmUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("MarketingConfirmation_Greeting")
            .Paragraph("MarketingConfirmation_Body", orgName)
            .Button("MarketingConfirmation_Cta", confirmUrl)
            .LinkFallback("MarketingConfirmation_LinkFallback", confirmUrl)
            .Muted("MarketingConfirmation_Validity", builder.FormatInstant(expiresAtUtc))
            .Muted("MarketingConfirmation_Ignore")
            .Build("MarketingConfirmation_Subject", orgName);
    }
}
