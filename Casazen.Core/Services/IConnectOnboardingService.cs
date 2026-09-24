namespace Casazen.Core.Services;

public record ConnectAccountSnapshot(
    string AccountId,
    bool ChargesEnabled,
    bool PayoutsEnabled,
    bool DetailsSubmitted,
    IReadOnlyList<string> RequirementsDue);

/// <summary>
/// Stripe Connect calls of the onboarding. Every failure is a <see cref="Casazen.Core.Exceptions.StripeConnectException"/>
/// whose <see cref="Casazen.Core.Exceptions.StripeConnectFailure"/> tells a missing or revoked account apart from a
/// temporary or configuration problem (BK-09, A3-19).
/// </summary>
public interface IStripeConnectGateway
{
    /// <summary>
    /// Creates an Express account. <paramref name="idempotencyKey"/> is sent as Stripe's <c>Idempotency-Key</c>: a repeated
    /// call with the same key (lost answer, retry) returns the same account instead of a second one.
    /// </summary>
    Task<string> CreateExpressAccountAsync(string email, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<ConnectAccountSnapshot> GetAccountAsync(string connectedAccountId, CancellationToken cancellationToken = default);

    Task<string> CreateAccountOnboardingLinkAsync(
        string connectedAccountId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default);
}

public record ConnectStatus(
    string? ConnectedAccountId,
    bool ChargesEnabled,
    bool PayoutsEnabled,
    bool DetailsSubmitted,
    IReadOnlyList<string> RequirementsDue);

public interface IConnectOnboardingService
{
    /// <summary>
    /// Connect status of the org, read again from Stripe when <paramref name="refreshFromStripe"/>. A missing or revoked
    /// account closes the checkout gate (capabilities false) but stays linked until the onboarding replaces it; any other
    /// Stripe failure throws <see cref="Casazen.Core.Exceptions.StripeConnectException"/> and changes nothing.
    /// </summary>
    Task<ConnectStatus> GetStatusAsync(Guid orgId, bool refreshFromStripe, CancellationToken cancellationToken = default);

    /// <summary>
    /// The org's Express account, created when missing (BK-09, A3-19): one org at a time (advisory lock) and with an
    /// idempotency key bound to the org, so parallel clicks create one account. The linked account is replaced only when
    /// Stripe says it does not exist or was revoked; a temporary or configuration failure throws
    /// <see cref="Casazen.Core.Exceptions.StripeConnectException"/> and leaves it untouched.
    /// </summary>
    Task<ConnectStatus> EnsureExpressAccountAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Account Link of the onboarding. <paramref name="returnUrl"/> and <paramref name="refreshUrl"/> are built by the
    /// server on the public web app (<c>App:PublicSiteBaseUrl</c>), never taken from the client (A3-42).
    /// </summary>
    Task<string> CreateOnboardingLinkAsync(
        Guid orgId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default);
    Task ApplyAccountUpdatedAsync(ConnectAccountSnapshot snapshot, CancellationToken cancellationToken = default);
}
