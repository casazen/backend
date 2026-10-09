namespace Casazen.Core.Services;

/// <summary>
/// State of a supplier's Stripe Connect account as the supplier sees it (SP-14). It carries what Stripe reports about the
/// account's capabilities and the names of the requirements Stripe still needs, never bank details, document data or the
/// Stripe account id.
/// </summary>
/// <param name="HasAccount">An Express account is linked to the supplier org (the onboarding was started).</param>
/// <param name="ChargesEnabled">Stripe enabled charges on the account (cached from <c>account.updated</c>, or read when a refresh was asked).</param>
/// <param name="PayoutsEnabled">Stripe enabled payouts on the account.</param>
/// <param name="DetailsSubmitted">The supplier submitted the onboarding form (Stripe may still be verifying it).</param>
/// <param name="RequirementsDue">Names of the fields Stripe currently needs (e.g. <c>external_account</c>); empty when none.</param>
/// <param name="CanReceivePayments">Account linked, charges <b>and</b> payouts enabled: the supplier can be paid through CasaZen.</param>
/// <param name="Verified">The "Verificato" badge (decision D11): Active profile, <paramref name="CanReceivePayments"/> and a VAT number.</param>
/// <param name="VerificationMissing">What is missing for <paramref name="Verified"/> (<c>profile_not_active</c>, <c>payments_not_enabled</c>, <c>vat_number_missing</c>); empty when verified.</param>
public sealed record SupplierPaymentsAccount(
    bool HasAccount,
    bool ChargesEnabled,
    bool PayoutsEnabled,
    bool DetailsSubmitted,
    IReadOnlyList<string> RequirementsDue,
    bool CanReceivePayments,
    bool Verified,
    IReadOnlyList<string> VerificationMissing);

/// <summary>
/// The Stripe Connect account (Express) a supplier uses to receive the payment of its jobs inside CasaZen (SP-14, decision D2:
/// direct charge on the supplier's own account). It reuses <see cref="IConnectOnboardingService"/>, which works on any
/// <c>Org</c>, for the account, the onboarding link and the status, and adds the supplier's own rules: the org must have a
/// supplier profile, the dashboard link needs an existing account and the "Verificato" state (<c>SupplierVerification</c>).
/// No payment is created here. Every method takes the supplier org resolved from the caller's own supplier link.
/// </summary>
/// <remarks>
/// An org without a supplier profile is a <see cref="Casazen.Core.Exceptions.NotFoundException"/>. A failed Stripe call is a
/// <see cref="Casazen.Core.Exceptions.StripeConnectException"/> (<see cref="Casazen.Core.Exceptions.StripeConnectFailure"/> says
/// how): the linked account is left untouched unless Stripe says it does not exist or was revoked, exactly as in the host's
/// onboarding (BK-09).
/// </remarks>
public interface ISupplierPaymentsAccountService
{
    /// <summary>
    /// The account state read from the database; with <paramref name="refreshFromStripe"/> read again from Stripe first (one
    /// call, and only when an account is linked).
    /// </summary>
    Task<SupplierPaymentsAccount> GetAccountAsync(Guid supplierOrgId, bool refreshFromStripe, CancellationToken cancellationToken = default);

    /// <summary>
    /// The supplier's Express account, created when missing: one at a time per org (advisory lock) with the idempotency key
    /// <c>connect-account:{orgId}</c>, so parallel requests create one account.
    /// </summary>
    Task<SupplierPaymentsAccount> EnsureAccountAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Account Link of the onboarding (creates the account when missing). The URLs are built by the server, never taken from
    /// the client. The link is single-use: never log it.
    /// </summary>
    Task<string> CreateOnboardingLinkAsync(
        Guid supplierOrgId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Single-use login link to the supplier's Express Dashboard. Needs an existing account (no account is created here): without
    /// one, or when Stripe refuses the link because the onboarding is not complete, it is a
    /// <see cref="Casazen.Core.Exceptions.DomainRuleException"/> with the code <c>supplier_payments_not_ready</c> (422). The link
    /// is a credential: never log it.
    /// </summary>
    Task<string> CreateDashboardLinkAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);
}
