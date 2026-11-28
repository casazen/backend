using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Wraps the external (paid) <see cref="IAiProvider"/> with the platform budget (A8-01, A8-07): the worst case of the
/// call (prompt estimate + <c>max_tokens</c>) is reserved <b>before</b> the provider is called, so a call over the cap
/// never leaves (<see cref="Core.Exceptions.AiBudgetExceededException"/>); afterwards the reservation is replaced by the
/// tokens reported by the provider, or by an estimate when it reports none.
/// </summary>
public sealed class BudgetGuardedAiProvider(
    IAiProvider inner,
    IAiBudgetGuard budget,
    int maxCompletionTokens,
    ILogger<BudgetGuardedAiProvider> logger) : IAiProvider
{
    public async Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        var reservation = await budget.ReserveAsync(
            AiTokenEstimator.EstimateCall(prompt, maxCompletionTokens),
            cancellationToken);

        AiGenerationResult result;
        try
        {
            result = await inner.GenerateAsync(prompt, tier, cacheKey, cancellationToken);
        }
        catch
        {
            await SettleSafelyAsync(reservation, AiTokenEstimator.Estimate(prompt));
            throw;
        }

        if (result.FromCache)
        {
            await SettleSafelyAsync(reservation, 0);
            return result;
        }

        var (promptTokens, completionTokens) = AiTokenEstimator.UsedOrEstimated(
            result.PromptTokens,
            result.CompletionTokens,
            prompt,
            result.Content);
        await SettleSafelyAsync(reservation, promptTokens + completionTokens);

        return result with
        {
            PromptTokens = (int)Math.Min(promptTokens, int.MaxValue),
            CompletionTokens = (int)Math.Min(completionTokens, int.MaxValue),
        };
    }

    public async Task<AiGenerationResult> GenerateAsync(
        AiRequest request,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.ToTextPrompt();
        var reservation = await budget.ReserveAsync(
            AiTokenEstimator.EstimateCall(request, maxCompletionTokens),
            cancellationToken);

        AiGenerationResult result;
        try
        {
            result = await inner.GenerateAsync(request, tier, cacheKey, cancellationToken);
        }
        catch
        {
            await SettleSafelyAsync(reservation, AiTokenEstimator.Estimate(text));
            throw;
        }

        if (result.FromCache)
        {
            await SettleSafelyAsync(reservation, 0);
            return result;
        }

        var reported = result.PromptTokens > 0 || result.CompletionTokens > 0;
        var (promptTokens, completionTokens) = AiTokenEstimator.UsedOrEstimated(
            result.PromptTokens,
            result.CompletionTokens,
            text,
            result.Content);
        if (!reported)
            promptTokens += (long)result.ImagesRead * AiTokenEstimator.TokensPerImage;
        await SettleSafelyAsync(reservation, promptTokens + completionTokens);

        return result with
        {
            PromptTokens = (int)Math.Min(promptTokens, int.MaxValue),
            CompletionTokens = (int)Math.Min(completionTokens, int.MaxValue),
        };
    }

    private async Task SettleSafelyAsync(AiBudgetReservation reservation, long usedTokens)
    {
        try
        {
            await budget.SettleAsync(reservation, usedTokens, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not settle an AI budget reservation of {ReservedTokens} tokens", reservation.ReservedTokens);
        }
    }
}
