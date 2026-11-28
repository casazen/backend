using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The translation of a message of the hub in both directions, for the composer (E05/F02/US02/T02, NF-63, AI-06): what a guest wrote into the language of
/// the host, and what the host is about to write into the language of the guest. The translation is returned to the screen and is <b>never stored</b>:
/// the stored message keeps the original text, and the screen shows the translation as a machine translation next to it.
/// </summary>
/// <remarks>
/// <para><b>Who translates.</b> CasaZen's own AI first, <b>but only with <see cref="Casazen.Core.Features.FeatureFlags.MessageTranslationAi"/> on, which is off by
/// default</b>: what a guest wrote is personal data and the AI provider is an external sub-processor (<c>docs/runbooks/ai.md</c>), so with the flag off
/// the provider is never asked. With it on: <see cref="IAiProvider"/> on the cheapest tier, behind the platform budget (the provider is
/// wrapped by the guard of <see cref="IAiBudgetGuard"/>, so a call that would pass the monthly cap never leaves). The channel manager's translation of
/// the message (<see cref="IOctorateApiClient.TranslateChatMessageAsync"/>) is the fallback when the AI is off or cannot be used (no provider is
/// configured, the budget is used up, the provider does not answer or answers something unusable), and it exists only for a message of a conversation of
/// a channel that the channel manager holds. With neither: 422 <c>translation_unavailable</c>.</para>
/// <para><b>Personal data (FD-21).</b> What reaches the AI provider is the text after <c>PiiScrubber</c> (e-mail addresses, phone numbers and fiscal
/// codes become placeholders) and after the names of the conversation (the guest, the host) have been replaced by placeholders, which are put back in the
/// translation. Other names typed in the text cannot be told from words and are not recognized: the host chooses to translate a message, and the
/// screen says it goes to an automatic translator. The words and the translation are never logged.</para>
/// </remarks>
public interface IMessageTranslationService
{
    Task<MessageTranslation> TranslateAsync(
        HostScope scope,
        MessageThreadKey threadKey,
        Guid? messageId,
        string? text,
        string? targetLanguage,
        CancellationToken ct = default);
}

public enum MessageTranslationProvider
{
    Ai = 1,

    ChannelManager = 2,
}

public sealed record MessageTranslation(string Text, string TargetLanguage, MessageTranslationProvider Provider, bool MachineTranslated = true);
