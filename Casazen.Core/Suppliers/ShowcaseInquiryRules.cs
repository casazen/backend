using Casazen.Core.Regulatory;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Technical bounds of the request on measure an anonymous customer makes from a supplier's showcase (SP-10b) and of the thread it
/// opens: the words of the customer and of the supplier, the number of messages, the requests of one address that wait. The sizes of
/// the name, the address, the comune and the rest are those of the booking (<see cref="ShowcaseBookingLimits"/>); like them, these keep an
/// anonymous endpoint small and a typo harmless, they are not product rules.
/// </summary>
public static class ShowcaseInquiryLimits
{
    /// <summary>Fewest characters of the description of the request: a sentence, not a greeting.</summary>
    public const int MessageMinLength = 10;

    /// <summary>Most characters of a message, the first one (the description) included.</summary>
    public const int MessageMaxLength = 1000;

    /// <summary>Most messages of one request, both parties together: a conversation that long moves to a proper offer.</summary>
    public const int MaxMessagesPerRequest = 200;

    /// <summary>
    /// Requests on measure of one e-mail address, for one supplier, that may wait at the same time (the ones not checked yet and the
    /// ones the supplier did not answer): the fourth is refused until one is answered, checked or lapses. It keeps one address from
    /// filling a supplier's inbox.
    /// </summary>
    public const int MaxWaitingPerEmail = ShowcaseBookingLimits.MaxUnverifiedHoldsPerEmail;

    /// <summary>Shortest and longest time a supplier has to answer a request on measure, in minutes (5 minutes to a day).</summary>
    public const int MinResponseMinutes = 5;

    /// <inheritdoc cref="MinResponseMinutes"/>
    public const int MaxResponseMinutes = 24 * 60;

    /// <summary>
    /// The time a supplier has to answer a request on measure when nothing is configured (<c>Suppliers:Showcase:InquiryResponseMinutes</c>):
    /// one hour, the promise of the product (interview of 9 October, Q7). After it the request lapses and the customer is told.
    /// </summary>
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

/// <summary>
/// The rules on the values of a request on measure from a showcase (SP-10b): which are required, how long they can be, what shape they
/// have. Pure functions, no clock and no database. The values that are not valid are collected and refused together (422
/// <see cref="ShowcaseBookingErrors.Invalid"/> naming all the fields, as the booking does); a consent that is missing or not of the
/// current version has its own 422.
/// </summary>
public static class ShowcaseInquiryRules
{
    /// <summary>
    /// Checks and trims <paramref name="input"/>. <paramref name="currentPrivacyVersion"/> is the version the notice has now
    /// (<c>Suppliers:Showcase:PrivacyNoticeVersion</c>): the consent has to name exactly it.
    /// </summary>
    /// <exception cref="ShowcaseBookingRuleException">The fields that are not valid.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseBookingErrors.ConsentRequired"/> or <see cref="ShowcaseBookingErrors.ConsentOutdated"/>.
    /// </exception>
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

    /// <summary>
    /// A message of the thread, or the description that opens it: plain text with its lines, outer whitespace gone, between
    /// <see cref="ShowcaseInquiryLimits.MessageMinLength"/> and <see cref="ShowcaseInquiryLimits.MessageMaxLength"/> characters, with at
    /// least one letter or digit and no control character other than the line break and the tab. <c>null</c> (and <paramref name="field"/> in
    /// <paramref name="invalid"/>) when it is not.
    /// </summary>
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
