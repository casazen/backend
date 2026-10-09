using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The supplier's Stripe Connect account (SP-14). The Express account, its status and the onboarding link are those of
/// <see cref="IConnectOnboardingService"/>, which works on any <c>Org</c> (advisory lock <c>OrgConnectAccount</c>, idempotency
/// key <c>connect-account:{orgId}</c>, an account replaced only when Stripe says it is gone): nothing of the host's flow is
/// changed. This class adds what is the supplier's own: only an org with a supplier profile gets an account, the dashboard
/// link needs an existing account, and the "Verificato" state of <see cref="SupplierVerification"/> is part of the answer.
/// No PaymentIntent, fee or payment record is created here (SP-15).
/// </summary>
public sealed class SupplierPaymentsAccountService(
    AppDbContext db,
    IConnectOnboardingService connectOnboarding,
    IStripeConnectGateway stripeConnectGateway,
    ILogger<SupplierPaymentsAccountService> logger) : ISupplierPaymentsAccountService
{
    public async Task<SupplierPaymentsAccount> GetAccountAsync(
        Guid supplierOrgId,
        bool refreshFromStripe,
        CancellationToken cancellationToken = default)
    {
        var profile = await RequireProfileAsync(supplierOrgId, cancellationToken);
        var status = await connectOnboarding.GetStatusAsync(supplierOrgId, refreshFromStripe, cancellationToken);
        return Map(profile, status);
    }

    public async Task<SupplierPaymentsAccount> EnsureAccountAsync(
        Guid supplierOrgId,
        CancellationToken cancellationToken = default)
    {
        var profile = await RequireProfileAsync(supplierOrgId, cancellationToken);
        var status = await connectOnboarding.EnsureExpressAccountAsync(supplierOrgId, cancellationToken);
        return Map(profile, status);
    }

    public async Task<string> CreateOnboardingLinkAsync(
        Guid supplierOrgId,
        string returnUrl,
        string refreshUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshUrl);
        await RequireProfileAsync(supplierOrgId, cancellationToken);

        var url = await connectOnboarding.CreateOnboardingLinkAsync(supplierOrgId, returnUrl, refreshUrl, cancellationToken);
        logger.LogInformation("Onboarding link of the Stripe Connect account created for supplier org {OrgId}", supplierOrgId);
        return url;
    }

    public async Task<string> CreateDashboardLinkAsync(Guid supplierOrgId, CancellationToken cancellationToken = default)
    {
        await RequireProfileAsync(supplierOrgId, cancellationToken);

        // The database is enough to know whether there is an account to open: a status read never goes to Stripe here.
        var status = await connectOnboarding.GetStatusAsync(supplierOrgId, refreshFromStripe: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(status.ConnectedAccountId))
            throw NotReady();

        try
        {
            var url = await stripeConnectGateway.CreateDashboardLoginLinkAsync(status.ConnectedAccountId, cancellationToken);
            logger.LogInformation("Express dashboard login link created for supplier org {OrgId}", supplierOrgId);
            return url;
        }
        catch (StripeConnectException ex) when (ex.Failure == StripeConnectFailure.Rejected)
        {
            // Stripe only gives a login link to an account that finished the onboarding: the supplier has to complete it
            // first (onboarding link). Not a server fault, so not a 502; the gateway already logged the refusal.
            logger.LogWarning(
                "Stripe refused the dashboard login link of supplier org {OrgId} (Stripe code {StripeErrorCode}): onboarding not complete",
                supplierOrgId,
                ex.StripeErrorCode ?? "none");
            throw NotReady();
        }
    }

    /// <summary>
    /// The facts of the profile the state depends on. A supplier org always has a profile; an org without one (a host org, a
    /// deleted profile) never gets a Connect account through the supplier's routes.
    /// </summary>
    private async Task<ProfileFacts> RequireProfileAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        await db.SupplierProfiles.AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => new ProfileFacts(sp.Status, sp.VatNumber))
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException($"Supplier profile of org {supplierOrgId} not found") { MessageKey = "SupplierProfileNotFound" };

    private static SupplierPaymentsAccount Map(ProfileFacts profile, ConnectStatus status)
    {
        var canReceivePayments = SupplierVerification.CanReceivePayments(
            status.ConnectedAccountId, status.ChargesEnabled, status.PayoutsEnabled);
        var missing = SupplierVerification.Missing(profile.Status, canReceivePayments, profile.VatNumber);

        return new SupplierPaymentsAccount(
            HasAccount: !string.IsNullOrWhiteSpace(status.ConnectedAccountId),
            ChargesEnabled: status.ChargesEnabled,
            PayoutsEnabled: status.PayoutsEnabled,
            DetailsSubmitted: status.DetailsSubmitted,
            RequirementsDue: status.RequirementsDue,
            CanReceivePayments: canReceivePayments,
            Verified: missing.Count == 0,
            VerificationMissing: missing);
    }

    private static DomainRuleException NotReady() =>
        new(SupplierPaymentsErrors.NotReady, SupplierPaymentsErrors.NotReadyMessageKey);

    private sealed record ProfileFacts(SupplierStatus Status, string? VatNumber);
}
