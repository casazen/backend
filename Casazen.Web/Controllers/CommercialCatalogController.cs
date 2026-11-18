using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController, Route("api/billing/commercial-catalog"), Authorize]
public sealed class CommercialCatalogController(IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(CommercialAddon.All.Select(a => new
    {
        a.Code,
        a.Context,
        PriceMonthly = configuration.GetValue<decimal?>($"Billing:Addons:{a.Code}:PriceMonthly"),
        OwnerOffer = new
        {
            PriceMonthly = CommercialPricing.Offer(configuration, a.Code, PlanTier.Pro).GetValue<decimal?>("PriceMonthly"),
            Purchasable = CommercialPricing.IsEnabled(configuration, a.Code) && CommercialPricing.Price(configuration, a.Code, PlanTier.Pro) is not null,
        },
        IncludedInPlans = PlanCatalog.All.Where(plan => CommercialPricing.IsIncluded(plan.Tier, a.Code) && CommercialPricing.IsEnabled(configuration, a.Code))
            .Select(plan => plan.Tier.ToString()),
        Currency = "EUR",
        Purchasable = CommercialPricing.IsAvailable(configuration, a.Code),
    }));
}
