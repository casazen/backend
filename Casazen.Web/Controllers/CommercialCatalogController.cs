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
        AgencyIncluded = AgencyUsagePricing.Included.Contains(a.Code),
        AgencyPurchasable = AgencyUsagePricing.Available(configuration, a.Code),
        AgencyPriceMonthly = AgencyUsagePricing.AddonAmount(configuration, a.Code),
        PriceMonthly = configuration.GetValue<decimal?>($"Billing:Addons:{a.Code}:PriceMonthly"),
        Currency = "EUR",
        Purchasable = CommercialPricing.IsAvailable(configuration, a.Code),
    }));
}
