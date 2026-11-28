using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed record StripeCheckoutSession(string Id, string Url, PlanTier? PlanTier, string? ConfigurationKey = null, string? ReturnUrl = null, string? AgencyBilling = null);

public sealed record StripeSubscriptionSummary(string Id, string Status);

public sealed record StripeTaxRateSummary(string Id, string? Country, decimal? EffectivePercentage, string? Jurisdiction);

public sealed record StripeCustomerTaxId(string Type, string Value, string? VerificationStatus);

public interface IStripeBillingService
{
    Task ReportAgencyUnitsAsync(string customerId, string eventName, int units, DateTime timestamp, CancellationToken ct) => throw new NotSupportedException();

    Task<string> EnsureCustomerAsync(Org org, CancellationToken cancellationToken = default);

    Task<StripeCheckoutSession> CreateCheckoutSessionAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CancellationToken cancellationToken = default);

    Task<StripeCheckoutSession> CreateCommercialCheckoutSessionAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CommercialSignup selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<StripeCheckoutSession>> ListOpenCheckoutSessionsAsync(string customerId, CancellationToken cancellationToken = default);

    Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StripeSubscriptionSummary>> ListSubscriptionsAsync(string customerId, CancellationToken cancellationToken = default);

    Task<string> CreatePortalSessionAsync(Org org, string returnUrl, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StripeTaxRateSummary>> GetTaxRatesAsync(IReadOnlyCollection<string> taxRateIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StripeCustomerTaxId>> ListCustomerTaxIdsAsync(string customerId, CancellationToken cancellationToken = default);

    PlanTier? MapPriceIdToTier(string? priceId);
}
