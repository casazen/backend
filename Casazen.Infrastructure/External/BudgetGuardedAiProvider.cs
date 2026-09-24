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
            // The prompt may have been processed before the failure (e.g. a timeout): count it, not the completion.
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

    /// <summary>
    /// Settles even when the caller's request was cancelled: the tokens were spent anyway. A failed settlement leaves
    /// the (higher) reservation in place, which errs on the side of the cap.
    /// </summary>
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
