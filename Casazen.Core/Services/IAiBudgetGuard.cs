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

    /// <summary>
    /// Replaces the reservation with <paramref name="usedTokens"/> (prompt + completion; <c>0</c> for a cache hit or a
    /// call that did not reach the provider).
    /// </summary>
    Task SettleAsync(AiBudgetReservation reservation, long usedTokens, CancellationToken cancellationToken = default);
}

/// <summary>Tokens held on the budget row <paramref name="BudgetId"/> until the call is settled.</summary>
public sealed record AiBudgetReservation(Guid BudgetId, long ReservedTokens);

/// <summary>
/// Token estimates for the budget when the real figure is not known yet (before the call) or not reported by the
/// provider (A8-07: the DeepSeek integration used to report 0 tokens, so the budget never moved).
/// </summary>
public static class AiTokenEstimator
{
    /// <summary>
    /// One token every 3 characters: a deliberately high estimate for Italian/English text, so the budget errs on the
    /// side of stopping early.
    /// </summary>
    public const int CharactersPerToken = 3;

    /// <summary>Estimated tokens of <paramref name="text"/>.</summary>
    public static long Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharactersPerToken - 1) / CharactersPerToken;

    /// <summary>Worst case of a call: the prompt plus the completion limit sent to the provider.</summary>
    public static long EstimateCall(string prompt, int maxCompletionTokens) =>
        Estimate(prompt) + Math.Max(0, maxCompletionTokens);

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
