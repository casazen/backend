using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IMessageTranslationService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Nothing is written.</b> The service reads the message, translates it and returns the text: no row of the conversation is created or changed
/// (the stored message keeps the original, ADR-032), and the translation is not kept anywhere but the short memory of the AI answers
/// (<see cref="IAiResponseCache"/>, per organization, which holds the translation of the scrubbed text).</item>
/// <item><b>The AI first, but only behind its own flag, and behind the platform budget.</b> With <see cref="FeatureFlags.MessageTranslationAi"/> <b>off (the
/// default)</b> the AI provider is never asked and its memory of answers is not read: what a guest wrote does not leave CasaZen for a sub-processor until
/// the product owner has decided that (<c>docs/runbooks/ai.md</c>). With it on: one call on the cheapest tier (<see cref="AiModelTier.Economy"/>) of
/// <see cref="IAiProvider"/>, which is wrapped by the monthly budget when an external provider is active: a call over the cap never leaves and throws
/// <see cref="AiBudgetExceededException"/>. What the provider gets is the text after <see cref="MessageTranslationPrompt.Scrub"/>.</item>
/// <item><b>The channel manager second (the only one with the flag off).</b> When the AI is off or cannot be used (no provider is configured, the budget is
/// used up, the provider fails or answers nothing usable) and the text is a message of a conversation of a channel that the channel manager holds, its own
/// translation of that message is asked for (<see cref="IOctorateApiClient.TranslateChatMessageAsync"/>, by the id of its message: no word of CasaZen's
/// leaves). A text the host is still writing has no message there, so only the AI can translate it. With neither: 422 <c>translation_unavailable</c>.</item>
/// <item><b>Logs</b> carry ids, the language and which translator answered: never the text, the translation or a name.</item>
/// </list>
/// </remarks>
public sealed class MessageTranslationService(
    AppDbContext db,
    IAiProvider ai,
    IAiResponseCache cache,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    ILogger<MessageTranslationService> logger) : IMessageTranslationService
{
    /// <summary>Stable code of the 422 of a text no translator could translate now.</summary>
    public const string UnavailableCode = "translation_unavailable";

    /// <summary>Stable code of the 422 of a language that is not one of the 14.</summary>
    public const string LanguageInvalidCode = "translation_language_invalid";

    /// <summary>Stable code of the 422 of a request that names a message and a text, or neither.</summary>
    public const string InputInvalidCode = "translation_input_invalid";

    /// <summary>The longest translation kept: a message is at most <see cref="GuestMessageRules.TextMaxLength"/> characters, and a translation is not twice as long.</summary>
    public const int MaxTranslationLength = 2 * GuestMessageRules.TextMaxLength;

    public async Task<MessageTranslation> TranslateAsync(
        HostScope scope,
        MessageThreadKey threadKey,
        Guid? messageId,
        string? text,
        string? targetLanguage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var language = ListingLanguages.Normalize(targetLanguage)
            ?? throw new DomainRuleException(LanguageInvalidCode, "MessageTranslationLanguageInvalid");
        if ((messageId is null) == string.IsNullOrWhiteSpace(text))
            throw new DomainRuleException(InputInvalidCode, "MessageTranslationInputInvalid");

        // The conversation in the reach of the caller, then the message in that conversation: another org's, or another conversation's, is a 404.
        var conversation = await MessageConversationLookup.FindAsync(db, scope, threadKey, ct);
        var source = messageId is { } id
            ? await MessageOfAsync(scope, conversation, id, ct)
            : new SourceText(GuestMessageRules.NormalizeText(text), null);
        if (source.Text is not { Length: > 0 and <= GuestMessageRules.TextMaxLength } original)
            throw new DomainRuleException(MessageChannelRules.TextInvalidCode, "GuestMessageTextInvalid");

        // The AI only behind its own flag (FD-21, AI-06): what a guest wrote is personal data and the provider is an external sub-processor. With the flag
        // off nothing of the text reaches the provider and the memory of its answers is not even read.
        if (featureFlags.IsEnabled(FeatureFlags.MessageTranslationAi)
            && await TryAiAsync(scope.OrgId, conversation, original, language, ct) is { } byAi)
        {
            logger.LogInformation("Message translation for org {OrgId} into {Language}: the AI answered", scope.OrgId, language);
            return new MessageTranslation(byAi, language, MessageTranslationProvider.Ai);
        }

        if (await TryChannelManagerAsync(conversation, source.OctorateMessageId, language, ct) is { } byChannelManager)
        {
            logger.LogInformation("Message translation for org {OrgId} into {Language}: the channel manager answered", scope.OrgId, language);
            return new MessageTranslation(byChannelManager, language, MessageTranslationProvider.ChannelManager);
        }

        logger.LogInformation("Message translation for org {OrgId} into {Language}: no translator could answer", scope.OrgId, language);
        throw new DomainRuleException(UnavailableCode, "MessageTranslationUnavailable");
    }

    /// <summary>
    /// The text of a message of the conversation, and its id on the channel manager when it has one. The words of a message of a channel are encrypted at
    /// rest and are read through the entity; a message of another conversation or another org is not found.
    /// </summary>
    private async Task<SourceText> MessageOfAsync(HostScope scope, ConversationFacts conversation, Guid messageId, CancellationToken ct)
    {
        if (conversation.OtaThreadId is { } threadId)
        {
            var ota = await db.OtaMessages
                .AsNoTracking()
                .Where(m => m.OrgId == scope.OrgId && m.ThreadId == threadId && m.Id == messageId)
                .Select(m => new { m.Text, m.OctorateMessageId })
                .FirstOrDefaultAsync(ct)
                ?? throw MessageNotFound();
            return new SourceText(GuestMessageRules.NormalizeText(ota.Text), ota.OctorateMessageId);
        }

        var bookingId = conversation.Key.Id;
        var direct = await db.GuestMessages
            .AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.Id == messageId && m.Thread.BookingId == bookingId)
            .Select(m => m.Text)
            .FirstOrDefaultAsync(ct)
            ?? throw MessageNotFound();
        return new SourceText(GuestMessageRules.NormalizeText(direct), null);
    }

    /// <summary>The translation by CasaZen's AI, or <c>null</c> when it cannot be used (see the remarks of the class).</summary>
    private async Task<string?> TryAiAsync(Guid orgId, ConversationFacts conversation, string text, string language, CancellationToken ct)
    {
        // The names the conversation knows: the guest (unless it was erased: its name is gone) and the person who hosts. The name of the organization is
        // a business name, often made of common words, and is left alone.
        var names = conversation.GuestErased
            ? new List<string?> { conversation.HostName }
            : [conversation.GuestFirstName, conversation.GuestLastName, conversation.ThreadGuestName, conversation.HostName];
        var scrubbed = MessageTranslationPrompt.Scrub(text, names);
        var request = MessageTranslationPrompt.Build(scrubbed.Text, language);
        var cacheKey = MessageTranslationPrompt.CacheKey(request);

        try
        {
            string translatedScrubbed;
            if (cache.TryGet(orgId, cacheKey, out TranslatedText? kept) && kept is not null)
            {
                translatedScrubbed = kept.Text;
            }
            else
            {
                var result = await ai.GenerateAsync(request, AiModelTier.Economy, cacheKey, ct);
                if (!result.ProviderConfigured)
                    return null;

                translatedScrubbed = Clean(result.Content) ?? string.Empty;
                if (translatedScrubbed.Length == 0 || translatedScrubbed.Length > MaxTranslationLength)
                {
                    // The sizes, never the text: it is the model's, and it may repeat what it should not.
                    logger.LogWarning("The AI answer to a message translation is not usable ({Length} characters)", result.Content?.Length ?? 0);
                    return null;
                }

                cache.Set(orgId, cacheKey, new TranslatedText(translatedScrubbed));
            }

            return GuestMessageRules.NormalizeText(MessageTranslationPrompt.Restore(translatedScrubbed, scrubbed));
        }
        catch (AiBudgetExceededException)
        {
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout of the provider is a TaskCanceledException too: it is the provider that did not answer, not the caller who left.
            logger.LogWarning(ex, "The AI provider failed to translate a message");
            return null;
        }
    }

    /// <summary>
    /// The translation by the channel manager, for a message that it holds, or <c>null</c> when it cannot be asked or does not answer. A refusal, an
    /// outage and a license without the chat module are all "not available": the answer of the endpoint is the same 422.
    /// </summary>
    private async Task<string?> TryChannelManagerAsync(ConversationFacts conversation, string? octorateMessageId, string language, CancellationToken ct)
    {
        if (octorateMessageId is null
            || conversation.OctorateThreadId is null
            || conversation.OctorateAccommodationId is null
            || !featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            return null;
        }

        try
        {
            string translated;
            using (orgScope.Use(conversation.OrgId))
            {
                translated = await client.TranslateChatMessageAsync(
                    conversation.OctorateAccommodationId, conversation.OctorateThreadId, octorateMessageId, language, ct);
            }

            var clean = Clean(translated);
            return clean is { Length: > 0 and <= MaxTranslationLength } ? GuestMessageRules.NormalizeText(clean) : null;
        }
        catch (OctorateException ex)
        {
            logger.LogWarning("The channel manager could not translate a message of org {OrgId} ({Code})", conversation.OrgId, ex.Code);
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The answer of a translator as text: trimmed, without the quotation marks a model sometimes puts around it. <c>null</c> when nothing is left.</summary>
    private static string? Clean(string? answer)
    {
        var text = answer?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '«' && text[^1] == '»')))
            text = text[1..^1].Trim();

        return text.Length == 0 ? null : text;
    }

    private static NotFoundException MessageNotFound() =>
        new("The message was not found or is not within reach") { Code = MessageHubService.MessageNotFoundCode, MessageKey = "MessageNotFound" };

    private sealed record SourceText(string? Text, string? OctorateMessageId);

    /// <summary>The translation of a scrubbed text, as the memory of the AI answers keeps it (names still as tokens).</summary>
    private sealed record TranslatedText(string Text);
}
