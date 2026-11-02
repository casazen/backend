using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestAssistService"/>
/// <remarks>
/// <para><b>What goes to the provider.</b> The host's own words, cleaned (<see cref="ServiceRequestAssistPrompt.CleanText"/>), and the fixed rules of
/// the prompt: the list of the categories and the urgencies. Not the property, its address or comune, a stay, a guest, a supplier or the notes of
/// a request that exists (FD-21, A8-15). The answer is a suggestion the host reads and changes.</para>
/// <para><b>What comes back.</b> One JSON object, checked by <see cref="ServiceRequestAssistRules"/> (a category of the platform, an urgency of the
/// enum, short plain texts), and its texts scrubbed again. A provider that is missing, a budget that is gone, a failure, a reply that is not an
/// object of this kind and a job the model cannot tell are never errors for the host: the result says why there is no suggestion.</para>
/// <para><b>Who to ask.</b> Not the AI's decision: CasaZen suppliers come first whenever an active one covers the comune of the property (the one
/// chosen from the official list) for the category, with the same lookup the proposals of the wizard make; otherwise the partners are proposed. The
/// answer is cached per org (only an answer that passed the checks); the channel is read live each time.</para>
/// <para><b>Cost.</b> Economy tier, behind the platform budget (<see cref="IAiProvider"/>) and the per-user and per-org rate limit of the endpoint.</para>
/// </remarks>
public sealed class ServiceRequestAssistService(
    IAiProvider ai,
    IAiRuntimeSettings settings,
    IAiResponseCache cache,
    ISupplierService suppliers,
    ILogger<ServiceRequestAssistService> logger) : IServiceRequestAssistService
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        (await settings.GetEffectiveAsync(cancellationToken)).IsExternalProviderActive();

    public async Task<ServiceRequestAssist> AssistAsync(
        Property property,
        string text,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(text);

        var cleaned = ServiceRequestAssistPrompt.CleanText(text);
        if (cleaned.Length < ServiceRequestAssistLimits.MinTextLength)
        {
            // What was typed is contact data or an address and little else: nothing to ask the AI.
            return ServiceRequestAssist.Without(ServiceRequestAssistReasons.NotUnderstood);
        }

        var (answer, reason) = await AskAsync(property.OrgId, cleaned, language, cancellationToken);
        if (answer is null)
        {
            logger.LogInformation("Request assist for property {PropertyId}: no suggestion ({Reason})", property.Id, reason);
            return ServiceRequestAssist.Without(reason!);
        }

        var (channel, covering) = await ChannelAsync(property, answer.Category, cancellationToken);
        logger.LogInformation(
            "Request assist for property {PropertyId}: {Category}, {Urgency}, channel {Channel} ({Covering} CasaZen suppliers)",
            property.Id, answer.Category, answer.Urgency, channel, covering);
        return new ServiceRequestAssist(true, null, answer.Category, answer.Urgency, answer.Summary, answer.Explanation, channel, covering);
    }

    /// <summary>
    /// What the AI says, once checked; or why there is none. Only an answer that passed the checks is kept for the next time: a bad one would
    /// otherwise be served again. The answer itself never reaches a log (it may repeat what the host wrote); the reason and the sizes do.
    /// </summary>
    private async Task<(AcceptedAssistAnswer? Answer, string? Reason)> AskAsync(
        Guid orgId,
        string text,
        string language,
        CancellationToken cancellationToken)
    {
        var request = ServiceRequestAssistPrompt.Build(text, language);
        var cacheKey = ServiceRequestAssistPrompt.CacheKey(request);
        try
        {
            if (cache.TryGet(orgId, cacheKey, out AcceptedAssistAnswer? kept) && kept is not null)
                return (kept, null);

            var result = await ai.GenerateAsync(request, AiModelTier.Economy, cacheKey, cancellationToken);
            if (!result.ProviderConfigured)
                return (null, AiSuggestionFallbackReasons.AiNotConfigured);

            var raw = ServiceRequestAssistAnswerParser.Parse(result.Content);
            if (raw is null)
            {
                logger.LogWarning("The AI answer to the description of a job is not an object with a category ({Length} characters)", result.Content?.Length ?? 0);
                return (null, AiSuggestionFallbackReasons.AiInvalidAnswer);
            }

            var (checkedAnswer, why) = ServiceRequestAssistRules.Check(raw);
            if (checkedAnswer is null)
            {
                if (why == AiSuggestionFallbackReasons.AiInvalidAnswer)
                    logger.LogWarning("The AI answer to the description of a job names a category that does not exist");
                return (null, why);
            }

            // The texts are the model's, written from the host's words: scrubbed once more, then kept as they are.
            var accepted = checkedAnswer with
            {
                Summary = Clean(checkedAnswer.Summary, ServiceRequestAssistLimits.MaxSummaryLength),
                Explanation = Clean(checkedAnswer.Explanation, ServiceRequestAssistLimits.MaxExplanationLength),
            };
            cache.Set(orgId, cacheKey, accepted);
            return (accepted, null);
        }
        catch (AiBudgetExceededException)
        {
            return (null, AiSuggestionFallbackReasons.AiBudgetExhausted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout of the provider is a TaskCanceledException too: it is the provider that did not answer, not the caller who left.
            logger.LogWarning(ex, "The AI provider failed to read the description of a job");
            return (null, AiSuggestionFallbackReasons.AiUnavailable);
        }
    }

    /// <summary>
    /// CasaZen when at least one active supplier covers the comune of the property for the category, the same lookup the proposals of the wizard
    /// make; the partners otherwise. A property with no comune chosen from the official list has no CasaZen coverage for the wizard, so it is
    /// the partners here too (the screen says to add the comune).
    /// </summary>
    private async Task<(string Channel, int Covering)> ChannelAsync(Property property, string category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(property.ComuneIstatCode))
            return (ServiceRequestAssistChannels.External, 0);

        var covering = await suppliers.GetActiveByComuneAsync(ComuneTarget.ForProperty(property), category, cancellationToken);
        return covering.Count > 0 ? (ServiceRequestAssistChannels.CasaZen, covering.Count) : (ServiceRequestAssistChannels.External, 0);
    }

    /// <summary>A text of the answer without contact data and the placeholders the scrubber leaves; <c>null</c> when nothing is left.</summary>
    private static string? Clean(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var scrubbed = PiiScrubber.Scrub(text)
            .Replace("[tel]", string.Empty, StringComparison.Ordinal)
            .Replace("[email]", string.Empty, StringComparison.Ordinal)
            .Replace("[cf]", string.Empty, StringComparison.Ordinal);
        return ServiceRequestAssistRules.OneLine(scrubbed, limit);
    }
}
