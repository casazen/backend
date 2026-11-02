using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>«A visitor wrote to you», to the host, from the contact form of the booking site (DB-03b).</summary>
        public const string HostContactMessage = "host-contact-message";
    }

    /// <summary>
    /// The email to the host when a visitor of the booking site writes through «Scrivi a {host}» (DB-03b). Unlike the messages of a booking
    /// (<see cref="GuestMessageToHost"/>) there is no booking to read it in, so the text of the visitor <b>is</b> in the email, HTML-encoded
    /// with its line breaks kept; the host answers by replying to the email, whose <c>Reply-To</c> is the visitor's address (set by the
    /// caller on the returned content). The subject carries no personal data.
    /// </summary>
    /// <param name="visitorName">The name the visitor gave, or empty: the address stands in for it.</param>
    /// <param name="propertyName">The property the visitor was looking at, when it is a published property of the org.</param>
    /// <param name="propertyUrl">Its public page; <c>null</c> when the public URL is not configured.</param>
    /// <param name="lang">The language of the site the visitor was reading (<c>it</c> or <c>en</c>), when known.</param>
    public static EmailContent HostContactMessage(
        CultureInfo culture,
        string visitorName,
        string visitorEmail,
        string message,
        string? propertyName,
        string? propertyUrl,
        string? lang)
    {
        var builder = new EmailHtmlBuilder(culture);
        builder.Paragraph("HostContact_Body", string.IsNullOrWhiteSpace(visitorName) ? visitorEmail : visitorName);
        if (!string.IsNullOrWhiteSpace(propertyName))
            builder.Paragraph("HostContact_Property", propertyName);
        if (propertyUrl is not null)
            builder.LinkFallback("HostContact_PropertyLink", propertyUrl);

        builder.Quote("HostContact_MessageLabel", message);
        builder.Paragraph("HostContact_Reply", visitorEmail);
        if (lang is "it" or "en")
            builder.Muted("HostContact_Lang", EmailTexts.Get($"HostContact_Lang_{lang}", culture));

        return builder
            .Muted("HostContact_Why")
            .Build("HostContact_Subject");
    }
}
