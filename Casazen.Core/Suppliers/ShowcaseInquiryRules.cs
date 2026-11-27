using Casazen.Core.Regulatory;

namespace Casazen.Core.Suppliers;

public static class ShowcaseInquiryLimits
{
    public const int MessageMinLength = 10;

    public const int MessageMaxLength = 1000;

    public const int MaxMessagesPerRequest = 200;

    /// <summary>
    /// Requests on measure of one e-mail address, for one supplier, that may wait at the same time (the ones not checked yet and the
    /// ones the supplier did not answer): the fourth is refused until one is answered, checked or lapses. It keeps one address from
    /// filling a supplier's inbox.
    /// </summary>
    public const int MaxWaitingPerEmail = ShowcaseBookingLimits.MaxUnverifiedHoldsPerEmail;

    public const int MinResponseMinutes = 5;

    public const int MaxResponseMinutes = 24 * 60;

    public const int DefaultResponseMinutes = 60;
}

/// <summary>
/// What an anonymous customer sends to ask a supplier for something on measure, as read from the request: nothing is checked yet
/// (<see cref="ShowcaseInquiryRules.Normalize"/> checks it).
/// </summary>
/// <param name="ClientRequestId">The id the client made up for this attempt: the same id is the same inquiry.</param>
/// <param name="Service">The slug of a published service the request is about, when it is about one (a service on quote included).</param>
/// <param name="ComuneIstat">ISTAT code of the comune, when the customer chose it from the official list.</param>
/// <param name="City">The comune where the work is.</param>
/// <param name="Message">What the customer needs, in its own words.</param>
/// <param name="PrivacyAccepted">The customer accepted the privacy notice.</param>
/// <param name="PrivacyNoticeVersion">The version of the notice the page showed (it has to be the current one).</param>
/// <param name="ConsentIp">The client address, as the server saw it (not sent by the client): consent evidence.</param>
public sealed record ShowcaseInquiryInput(
    Guid ClientRequestId,
    string? Service,
    string? ComuneIstat,
    string? City,
    string? Message,
    string? FullName,
    string? Email,
    string? Phone,
    string? Locale,
    bool PrivacyAccepted,
    string? PrivacyNoticeVersion,
    string? ConsentIp);

/// <summary>An inquiry after <see cref="ShowcaseInquiryRules.Normalize"/>: every value trimmed and checked.</summary>
/// <param name="ServiceSlug">The slug the customer named, or <c>null</c> for a request about nothing in the price list.</param>
/// <param name="Message">The description: lines kept, outer whitespace gone, between the minimum and the maximum length.</param>
/// <param name="Email">The address as typed (trimmed): the e-mails go to it. The index is made of its lowercase form.</param>
/// <param name="Phone">Digits and an optional leading plus.</param>
/// <param name="Locale">One of <see cref="ServiceCustomerLocales"/>.</param>
public sealed record ShowcaseInquiryContent(
    Guid ClientRequestId,
    string? ServiceSlug,
    string? ComuneIstat,
    string City,
    string Message,
    string FullName,
    string Email,
    string Phone,
    string Locale,
    string PrivacyNoticeVersion,
    string ConsentIp);

public static class ShowcaseInquiryRules
{
    public static ShowcaseInquiryContent Normalize(ShowcaseInquiryInput input, string currentPrivacyVersion)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPrivacyVersion);

        var invalid = new List<string>();

        if (input.ClientRequestId == Guid.Empty)
            invalid.Add(ShowcaseBookingFields.ClientRequestId);

        var service = input.Service?.Trim();
        if (string.IsNullOrEmpty(service))
            service = null;

        var comuneIstat = input.ComuneIstat?.Trim();
        if (string.IsNullOrEmpty(comuneIstat))
            comuneIstat = null;
        else if (!ComuneRules.IsIstatCode(comuneIstat))
            invalid.Add(ShowcaseBookingFields.ComuneIstat);

        var city = ShowcaseBookingRules.Text(input.City, ShowcaseBookingLimits.CityMaxLength, required: true, ShowcaseBookingFields.City, invalid);
        var message = Message(input.Message, invalid);
        var fullName = ShowcaseBookingRules.FullName(input.FullName, invalid);
        var email = ShowcaseBookingRules.Email(input.Email, invalid);
        var phone = ShowcaseBookingRules.Phone(input.Phone, invalid);

        if (invalid.Count > 0)
            throw ShowcaseBookingErrors.InvalidFields(invalid);

        if (!input.PrivacyAccepted)
            throw ShowcaseBookingErrors.NoConsent();

        var version = input.PrivacyNoticeVersion?.Trim();
        if (!string.Equals(version, currentPrivacyVersion.Trim(), StringComparison.Ordinal))
            throw ShowcaseBookingErrors.OldConsent();

        return new ShowcaseInquiryContent(
            input.ClientRequestId,
            service,
            comuneIstat,
            city!,
            message!,
            fullName!,
            email!,
            phone!,
            ServiceCustomerLocales.Normalize(input.Locale),
            version!,
            ShowcaseBookingRules.Truncate(input.ConsentIp, ShowcaseBookingLimits.ConsentIpMaxLength));
    }

    public static string? Message(string? value, List<string> invalid, int minLength = ShowcaseInquiryLimits.MessageMinLength, string field = ShowcaseBookingFields.Message)
    {
        ArgumentNullException.ThrowIfNull(invalid);

        var text = value?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (string.IsNullOrEmpty(text)
            || text.Length < minLength
            || text.Length > ShowcaseInquiryLimits.MessageMaxLength
            || text.Any(c => char.IsControl(c) && c != '\n' && c != '\t')
            || !text.Any(char.IsLetterOrDigit))
        {
            invalid.Add(field);
            return null;
        }

        return text;
    }
}
