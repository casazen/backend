using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The translation of a message of the hub in both directions, for the composer (E05/F02/US02/T02, NF-63, AI-06): what a guest wrote into the language of
/// the host, and what the host is about to write into the language of the guest. The translation is returned to the screen and is <b>never stored</b>:
/// the stored message keeps the original text, and the screen shows the translation as a machine translation next to it.
/// </summary>
/// <remarks>
/// <para><b>Who translates.</b> CasaZen's own AI first: <see cref="IAiProvider"/> on the cheapest tier, behind the platform budget (the provider is
/// wrapped by the guard of <see cref="IAiBudgetGuard"/>, so a call that would pass the monthly cap never leaves). The channel manager's translation of
/// the message (<see cref="IOctorateApiClient.TranslateChatMessageAsync"/>) is the fallback when the AI cannot be used (no provider is configured, the
/// budget is used up, the provider does not answer or answers something unusable), and it exists only for a message of a conversation of a channel that
/// the channel manager holds. With neither: 422 <c>translation_unavailable</c>.</para>
/// <para><b>Personal data (FD-21).</b> What reaches the AI provider is the text after <c>PiiScrubber</c> (e-mail addresses, phone numbers and fiscal
/// codes become placeholders) and after the names of the conversation (the guest, the host) have been replaced by placeholders, which are put back in the
/// translation. Other names typed in the text cannot be told from words and are not recognized: the host chooses to translate a message, and the
/// screen says it goes to an automatic translator. The words and the translation are never logged.</para>
/// </remarks>
public interface IMessageTranslationService
{
    /// <summary>
    /// Translates one message of a conversation (<paramref name="messageId"/>, the id of a row of the hub) or a text the host is writing
    /// (<paramref name="text"/>), exactly one of the two, into <paramref name="targetLanguage"/> (one of the 14 languages of the channel manager, in any
    /// case). The conversation is checked against <paramref name="scope"/> first: 404 <c>thread_not_found</c> or <c>message_not_found</c>. 422
    /// <c>translation_language_invalid</c>, <c>translation_input_invalid</c>, <c>message_text_invalid</c> (a text longer than a message can be) and
    /// <c>translation_unavailable</c>.
    /// </summary>
    Task<MessageTranslation> TranslateAsync(
        HostScope scope,
        MessageThreadKey threadKey,
        Guid? messageId,
        string? text,
        string? targetLanguage,
        CancellationToken ct = default);
}

/// <summary>Who produced a translation.</summary>
public enum MessageTranslationProvider
{
    /// <summary>CasaZen's own AI (<see cref="IAiProvider"/>).</summary>
    Ai = 1,

    /// <summary>The channel manager (Octorate), asked only when the AI could not be used.</summary>
    ChannelManager = 2,
}

/// <summary>
/// A translation, as the screen shows it. It is always a machine translation (<paramref name="MachineTranslated"/> is true) and is not a message:
/// nothing in the conversation changes.
/// </summary>
public sealed record MessageTranslation(string Text, string TargetLanguage, MessageTranslationProvider Provider, bool MachineTranslated = true);
