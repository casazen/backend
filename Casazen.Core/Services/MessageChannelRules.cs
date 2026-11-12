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
    /// <summary>The text has a link, an e-mail address or a phone number that the channel does not take yet (422), or the channel takes no answer at all.</summary>
    public const string ContactDetailsCode = "channel_rule_violation";

    /// <summary>The text is empty or longer than <see cref="GuestMessageRules.TextMaxLength"/> (422).</summary>
    public const string TextInvalidCode = "message_text_invalid";

    /// <summary>
    /// The code of the first rule that <paramref name="text"/> breaks for a conversation of <paramref name="channel"/> of <paramref name="kind"/>, else
    /// <c>null</c>: first the text itself (<see cref="TextInvalidCode"/>), then what the channel allows (<see cref="ContactDetailsCode"/>). A channel or a
    /// kind the table does not know allows nothing (<see cref="OtaChannelCapabilities"/> is closed by default).
    /// </summary>
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

    /// <summary>True when <paramref name="text"/> holds an e-mail address, a link or a phone number.</summary>
    public static bool ContainsContactDetails(string? text) =>
        !string.IsNullOrEmpty(text) && (EmailAddress().IsMatch(text) || Link().IsMatch(text) || PhoneNumber().IsMatch(text));

    // something@something.tld, with a space allowed around the sign.
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+[ ]?@[ ]?[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailAddress();

    // A scheme or www. in any case; otherwise a domain (labels, then a common ending in lower case, so a sentence that runs on without a space after the
    // full stop, «pronto.It», is not taken for an address).
    [GeneratedRegex(
        @"(?i:(?:https?|ftp)://|www\.)\S+"
        + @"|\b[A-Za-z0-9][A-Za-z0-9\-]*(?:\.[A-Za-z0-9][A-Za-z0-9\-]*)*\.(?:com|net|org|info|biz|eu|it|de|fr|es|uk|us|ch|at|nl|be|pt|ru|io|me|co|app|dev|link|ly|xyz|online|site|shop|club|tv|gl|page)\b(?:/\S*)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    // Nine digits or more, with the usual separators between them (a space, a dash, a dot, brackets), after a + or 00 or not; not inside a longer word.
    // A date (2026-11-06) has eight digits and a time has a colon, so neither is a number.
    [GeneratedRegex(@"(?<![\d\w])(?:\+|00)?\d(?:[ \-.()]{0,2}\d){8,}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneNumber();
}
