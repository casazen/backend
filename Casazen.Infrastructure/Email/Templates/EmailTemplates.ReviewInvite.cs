using System.Globalization;

namespace Casazen.Infrastructure.Email.Templates;

public static partial class EmailTemplates
{
    public static partial class Names
    {
        /// <summary>Invitation to review a stay, to the guest, sent only when the host asks for it, by hand or through a routine (DB-12a, E26/F01/US01/T01).</summary>
        public const string ReviewInvite = "review-invite";
    }

    /// <summary>
    /// The one email that asks a guest for the review of a stay (DB-12a), with the signed link to the page where the review is written.
    /// It is sent when the host asks, or by the action <c>RequestReview</c> of a routine the host switched on (E26/F01/US01/T01), once per booking in
    /// both cases, never by a job or a reminder of its own. It names the property and the dates and nothing else of the booking: no amount, no code,
    /// no address of the guest.
    /// </summary>
    /// <param name="culture">The language of the guest (<see cref="ReviewInviteCulture"/>): the texts exist in Italian and English.</param>
    /// <param name="expiresAtUtc">End of validity of the signed link (shown in Italian time): the end of the review window.</param>
    public static EmailContent ReviewInvite(
        CultureInfo culture,
        string guestName,
        string propertyName,
        DateTime checkInDate,
        DateTime checkOutDate,
        string reviewUrl,
        DateTime expiresAtUtc)
    {
        var builder = new EmailHtmlBuilder(culture);
        return builder
            .Paragraph("ReviewInvite_Greeting", guestName)
            .Paragraph("ReviewInvite_Body", propertyName, checkInDate, checkOutDate)
            .Paragraph("ReviewInvite_How")
            .Button("ReviewInvite_Cta", reviewUrl)
            .LinkFallback("ReviewInvite_LinkFallback", reviewUrl)
            .Muted("ReviewInvite_Validity", builder.FormatInstant(expiresAtUtc))
            .Muted("ReviewInvite_Once")
            .Build("ReviewInvite_Subject", propertyName);
    }

    /// <summary>
    /// The culture of <see cref="ReviewInvite"/> for the language of the guest (<c>GuestLanguages</c>: the one chosen on the check-in page, else the one of the country of the guest):
    /// Italian for <c>it</c> and when no language is known, English for every other language, because the invitation is written in Italian and English only (German, French and
    /// Spanish hold the guest check-in emails and nothing else). The same choice as the access code email (<see cref="GuestAccessCodeCulture"/>).
    /// </summary>
    public static CultureInfo ReviewInviteCulture(string? language) => GuestAccessCodeCulture(language);
}
