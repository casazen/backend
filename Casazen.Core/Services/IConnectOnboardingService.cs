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

    /// <summary>
    /// Single-use login link to the Express Dashboard of the connected account (SP-14): where the owner of the account sees
    /// balance, payouts and bank details on Stripe. Stripe refuses it (<see cref="Casazen.Core.Exceptions.StripeConnectFailure.Rejected"/>)
    /// for an account that is not eligible, e.g. one that has not completed the onboarding. The URL is a credential: never
    /// log it nor store it.
    /// </summary>
    Task<string> CreateDashboardLoginLinkAsync(string connectedAccountId, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Applies an <c>account.updated</c> to the org that owns the account and says whether a supplier has just become able to
    /// receive payments (SP-15b): the caller then queues the payment requests that waited for it. An unknown account changes
    /// nothing and is <see cref="ConnectAccountUpdate.None"/>.
    /// </summary>
    Task<ConnectAccountUpdate> ApplyAccountUpdatedAsync(ConnectAccountSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>What an <c>account.updated</c> changed for the org that owns the account.</summary>
/// <param name="OrgId">The org the account belongs to; null when no org owns it.</param>
/// <param name="SupplierBecameReady">
/// The org is a supplier whose account could <b>not</b> take charges and payouts before this event and can now
/// (<c>SupplierVerification.CanReceivePayments</c>): the payment requests that were pending for it can go out.
/// </param>
public sealed record ConnectAccountUpdate(Guid? OrgId, bool SupplierBecameReady)
{
    /// <summary>No org owns the account: nothing changed.</summary>
    public static ConnectAccountUpdate None { get; } = new(null, false);
}
