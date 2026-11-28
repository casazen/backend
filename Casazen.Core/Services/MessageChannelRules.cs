using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The rules a message CasaZen is about to send to a guest through a channel has to pass (E05/F02/US02/T01, OCT-11, ADR-032-R7), pure: no clock, no
/// database, no call. <see cref="CheckOutgoing"/> answers the stable code of the first rule the text breaks, or <c>null</c> when it may go out; the
/// service of the reply turns the code into the 422 of the API, and the screens read the same rule from <see cref="OtaChannelCapabilities"/>, the one
/// table that says whether a conversation takes a link, an address or a number.
/// </summary>
/// <remarks>
/// <para><b>Contact details before the booking (OCT-11).</b> On an Airbnb conversation that is still a request, with no booking behind it, the host may
/// not write a link, an e-mail address or a phone number: the channel keeps the first contact on its own platform. The detection is a best effort on the
/// words (an address, a number of nine digits or more, a scheme or <c>www.</c>, a domain with a common ending): it is not a substitute for the channel's
/// own check, which answers with an error CasaZen shows as the state of the message.</para>
/// <para>The text length is <see cref="GuestMessageRules.TextMaxLength"/>, the one of the direct messages.</para>
/// </remarks>
public static partial class MessageChannelRules
{
    public const string ContactDetailsCode = "channel_rule_violation";

    public const string TextInvalidCode = "message_text_invalid";

    public static string? CheckOutgoing(OtaChannel channel, OtaThreadKind kind, string? text)
    {
        var clean = GuestMessageRules.NormalizeText(text);
        if (clean is null || clean.Length > GuestMessageRules.TextMaxLength)
            return TextInvalidCode;

        var capability = OtaChannelCapabilities.For(channel, kind);
        if (!capability.CanReply)
            return ContactDetailsCode;

        return !capability.ContactDetailsAllowed && ContainsContactDetails(clean) ? ContactDetailsCode : null;
    }

    public static bool ContainsContactDetails(string? text) =>
        !string.IsNullOrEmpty(text) && (EmailAddress().IsMatch(text) || Link().IsMatch(text) || PhoneNumber().IsMatch(text));

    public static IReadOnlyList<string> ContactDetailsIn(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return EmailAddress().Matches(text)
            .Concat(Link().Matches(text))
            .Concat(PhoneNumber().Matches(text))
            .Select(match => match.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+[ ]?@[ ]?[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    // A scheme or www. in any case; otherwise a domain (labels, then a common ending in lower case, so a sentence that runs on without a space after the
    // full stop, «pronto.It», is not taken for an address).
    [GeneratedRegex(
        @"(?i:(?:https?|ftp)://|www\.)\S+"
        + @"|\b[A-Za-z0-9][A-Za-z0-9\-]*(?:\.[A-Za-z0-9][A-Za-z0-9\-]*)*\.(?:com|net|org|info|biz|eu|it|de|fr|es|uk|us|ch|at|nl|be|pt|ru|io|me|co|app|dev|link|ly|xyz|online|site|shop|club|tv|gl|page)\b(?:/\S*)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"(?<![\d\w])(?:\+|00)?\d(?:[ \-.()]{0,2}\d){8,}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumber();
}
