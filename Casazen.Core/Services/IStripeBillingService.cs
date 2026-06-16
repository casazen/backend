using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IStripeBillingService
{
    Task<string> EnsureCustomerAsync(Org org, CancellationToken cancellationToken = default);
    Task<string> CreateCheckoutSessionAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CancellationToken cancellationToken = default);
    Task<string> CreatePortalSessionAsync(Org org, CancellationToken cancellationToken = default);
    PlanTier? MapPriceIdToTier(string? priceId);
}
