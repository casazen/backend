using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IAutopilotMessageWriter"/>
/// <remarks>
/// <para><b>What goes to the provider.</b> A language and the number of the reminder for each text, and the fixed rules of
/// <see cref="AutopilotMessagePrompt"/>. No guest, no stay, no property, no host.</para>
/// <para><b>What comes back.</b> One JSON object, every text of it checked by <see cref="AutopilotChatText.Check"/>; a text that does not
/// pass is replaced by the plain text of CasaZen, one by one. A provider that is missing, a budget that is gone, a limit that is used
/// up, a failure and an answer that is not of this kind are never errors: the result says why the AI was not used and the plain texts
/// are there. The answer is cached per org (only an answer that passed the checks): a second run with the same variants costs nothing.</para>
/// <para><b>Cost.</b> One call for all the texts of a run, Economy tier, behind the platform budget (<see cref="IAiProvider"/>) and the
/// per-user and per-org rate limit the caller takes a permit of.</para>
/// </remarks>
public sealed class AutopilotMessageWriter(
    IAiProvider ai,
    IAiRuntimeSettings settings,
    IAiResponseCache cache,
    ILogger<AutopilotMessageWriter> logger) : IAutopilotMessageWriter
{
    public async Task<AutopilotWording> WriteAsync(
        Guid orgId,
        IReadOnlyCollection<AutopilotChatVariant> variants,
        Func<bool>? acquirePermit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variants);

        var plain = variants
            .DistinctBy(v => v.Key)
            .ToDictionary(v => v.Key, v => new AutopilotWrittenText(AutopilotChatText.Fallback(v.Language, v.Reminder), false), StringComparer.Ordinal);
        if (plain.Count == 0)
            return new AutopilotWording(plain, false, null);

        if (!(await settings.GetEffectiveAsync(cancellationToken)).IsExternalProviderActive())
            return new AutopilotWording(plain, false, AiSuggestionFallbackReasons.AiNotConfigured);

        var request = AutopilotMessagePrompt.Build(variants);
        var cacheKey = AutopilotMessagePrompt.CacheKey(request);
        try
        {
            if (!cache.TryGet(orgId, cacheKey, out AutopilotAcceptedTexts? accepted) || accepted is null)
            {
                // A permit only for a call that is really made: a cached answer costs nothing and counts for nothing.
                if (acquirePermit is not null && !acquirePermit())
                    return new AutopilotWording(plain, false, AiSuggestionFallbackReasons.AiUnavailable);

                var result = await ai.GenerateAsync(request, AiModelTier.Economy, cacheKey, cancellationToken);
                if (!result.ProviderConfigured)
                    return new AutopilotWording(plain, false, AiSuggestionFallbackReasons.AiNotConfigured);

                var texts = AutopilotMessagePrompt.Parse(result.Content, variants);
                if (texts.Count == 0)
                {
                    // The sizes, never the text: it is the model's, and it may repeat what it should not.
                    logger.LogWarning("The AI answer for the chat messages of the assistant holds no usable text ({Length} characters)", result.Content?.Length ?? 0);
                    return new AutopilotWording(plain, false, AiSuggestionFallbackReasons.AiInvalidAnswer);
                }

                accepted = new AutopilotAcceptedTexts(texts);
                cache.Set(orgId, cacheKey, accepted);
            }

            var merged = new Dictionary<string, AutopilotWrittenText>(plain, StringComparer.Ordinal);
            foreach (var (key, template) in accepted.Texts)
            {
                if (merged.ContainsKey(key))
                    merged[key] = new AutopilotWrittenText(template, true);
            }

            return new AutopilotWording(merged, merged.Values.Any(t => t.AiWritten), null);
        }
        catch (AiBudgetExceededException)
        {
            return new AutopilotWording(plain, false, AiSuggestionFallbackReasons.AiBudgetExhausted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout of the provider is a TaskCanceledException too: it is the provider that did not answer, not the caller who left.
            logger.LogWarning(ex, "The AI provider failed to write the chat messages of the assistant");
            return new AutopilotWording(plain, false, AiSuggestionFallbackReasons.AiUnavailable);
        }
    }
}

/// <summary>The texts of an answer that passed the checks, by variant key: what the cache keeps.</summary>
public sealed record AutopilotAcceptedTexts(IReadOnlyDictionary<string, string> Texts);
