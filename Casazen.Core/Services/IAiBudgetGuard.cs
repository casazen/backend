namespace Casazen.Core.Services;

/// <summary>
/// Monthly platform token budget of the paid AI calls (A8-01, A8-07): row <c>PlatformAiBudgets</c>, cap
/// <c>MonthlyTokenCap</c>. Every paid call reserves its worst case <b>before</b> reaching the provider and then settles
/// the reservation with the tokens actually used, so a call that would exceed the cap never leaves.
/// </summary>
/// <remarks>
/// Only external, paid providers go through the guard (<c>Ai:Provider=DeepSeek</c> with an API key): the stub provider
/// makes no call and costs nothing. Runbook: <c>docs/runbooks/ai.md</c>.
/// </remarks>
public interface IAiBudgetGuard
{
    /// <summary>
    /// Reserves <paramref name="estimatedTokens"/> of this month's budget (the month resets on the first call of a new
    /// UTC month). Throws <see cref="Exceptions.AiBudgetExceededException"/> when the reservation would exceed the cap:
    /// the caller must not call the provider.
    /// </summary>
    Task<AiBudgetReservation> ReserveAsync(long estimatedTokens, CancellationToken cancellationToken = default);

    Task SettleAsync(AiBudgetReservation reservation, long usedTokens, CancellationToken cancellationToken = default);
}

public sealed record AiBudgetReservation(Guid BudgetId, long ReservedTokens);

public static class AiTokenEstimator
{
    public const int CharactersPerToken = 3;

    public static long Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharactersPerToken - 1) / CharactersPerToken;

    /// <summary>
    /// Tokens counted for one image of an <see cref="AiRequest"/>: a 1024-pixel photo costs about 1,050 tokens on Claude
    /// (width × height / 750) and up to 1,105 on OpenAI; this rounds up so that the reservation errs on the side of the cap.
    /// </summary>
    public const int TokensPerImage = 1_400;

    public static long EstimateCall(string prompt, int maxCompletionTokens) =>
        Estimate(prompt) + Math.Max(0, maxCompletionTokens);

    public static long EstimateCall(AiRequest request, int maxCompletionTokens)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EstimateCall(request.ToTextPrompt(), maxCompletionTokens) + (long)request.Images.Count * TokensPerImage;
    }

    /// <summary>
    /// Tokens of a completed call: the provider's figures when it reports them, otherwise the estimate of the prompt
    /// and of the answer received.
    /// </summary>
    public static (long Prompt, long Completion) UsedOrEstimated(
        long promptTokens,
        long completionTokens,
        string prompt,
        string? completion) =>
        promptTokens > 0 || completionTokens > 0
            ? (Math.Max(0, promptTokens), Math.Max(0, completionTokens))
            : (Estimate(prompt), Estimate(completion));
}
