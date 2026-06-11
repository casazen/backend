using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.Billing;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/billing")]
public class BillingController(
    IOrgContextResolver orgContextResolver,
    IOrgService orgService,
    IStripeBillingService stripeBillingService,
    IBillingEntryGate billingEntryGate,
    IViesService viesService,
    IConfiguration configuration,
    AppDbContext dbContext) : ControllerBase
{
    [HttpGet("plans")]
    [Authorize]
    public ActionResult<IEnumerable<PlanCatalogDto>> GetPlans() =>
        Ok(PlanCatalog.All.Select(e => new PlanCatalogDto
        {
            Tier = e.Tier.ToString(),
            DisplayName = configuration[$"Billing:Display:{e.Tier}:Name"] ?? e.DisplayName,
            PriceMonthly = configuration.GetValue<decimal>($"Billing:Display:{e.Tier}:PriceMonthly", 0m),
            Currency = "EUR",
            UnitAllowance = e.MaxProperties == int.MaxValue ? -1 : e.MaxProperties,
            Features = configuration.GetSection($"Billing:Display:{e.Tier}:Features").Get<string[]>() ?? [e.Description],
            StripePriceId = configuration[$"Billing:Prices:{e.Tier}"] ?? string.Empty,
        }));

    [HttpPost("checkout-session")]
    [Authorize(Policy = "RequireOrgBillingAdmin")]
    public async Task<ActionResult<CheckoutSessionResponse>> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionRequest request,
        CancellationToken ct)
    {
        if (!PlanCatalog.TryParseTier(request.PlanTier, out var planTier))
            return BadRequest(new { error = $"Unknown planTier: {request.PlanTier}", code = "validation_error" });

        if (string.IsNullOrWhiteSpace(request.BillingCountry) || request.BillingCountry.Trim().Length != 2)
            return BadRequest(new { error = "Invalid billing country", code = "validation_error" });

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned" });

        try
        {
            await billingEntryGate.AssertCanChargeAsync(ct);
        }
        catch (BillingGateClosedException)
        {
            return Conflict(new { error = "Fatturazione non ancora disponibile", code = "billing_gate_closed" });
        }

        DateTime? vatValidatedAt = null;
        if (!string.IsNullOrWhiteSpace(request.VatId) &&
            !string.Equals(request.BillingCountry.Trim(), "IT", StringComparison.OrdinalIgnoreCase))
        {
            if (!await viesService.ValidateVatIdAsync(
                    request.BillingCountry,
                    request.VatId.Replace(" ", string.Empty),
                    ct))
            {
                return BadRequest(new { error = "Invalid VAT id", code = "validation_error" });
            }

            vatValidatedAt = DateTime.UtcNow;
        }

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId.Value, ct);
        if (org is null)
            return NotFound(new { error = "No organization assigned" });

        await orgService.UpdateBillingProfileAsync(org.Id, request.BillingCountry, request.VatId, vatValidatedAt, ct);
        org = await dbContext.Orgs.FirstAsync(o => o.Id == orgId.Value, ct);
        org.StripeCustomerId = await stripeBillingService.EnsureCustomerAsync(org, ct);
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        var successUrl = request.SuccessUrl ?? "https://app.casazen.app/settings/billing?checkout=success";
        var cancelUrl = request.CancelUrl ?? "https://app.casazen.app/settings/billing/plans?checkout=cancel";
        var url = await stripeBillingService.CreateCheckoutSessionAsync(org, planTier, successUrl, cancelUrl, ct);
        return Ok(new CheckoutSessionResponse { CheckoutUrl = url });
    }

    [HttpPost("portal-session")]
    [Authorize(Policy = "RequireOrgBillingAdmin")]
    public async Task<ActionResult<PortalSessionResponse>> CreatePortalSession(CancellationToken ct)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned" });

        var org = await orgService.GetByIdAsync(orgId.Value, ct);
        if (org is null)
            return NotFound(new { error = "No organization assigned" });

        if (string.IsNullOrEmpty(org.StripeCustomerId))
            return BadRequest(new { error = "No Stripe customer" });

        return Ok(new PortalSessionResponse
        {
            PortalUrl = await stripeBillingService.CreatePortalSessionAsync(org, ct),
        });
    }

    [HttpGet("subscription")]
    [Authorize(Policy = "RequireOrgBillingAdmin")]
    public async Task<ActionResult<SubscriptionDto>> GetSubscription(CancellationToken ct)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned" });

        var org = await orgService.GetByIdAsync(orgId.Value, ct);
        return org is null
            ? NotFound(new { error = "No organization assigned" })
            : Ok(Map(org));
    }

    [HttpPut("profile")]
    [Authorize(Policy = "RequireOrgBillingAdmin")]
    public async Task<ActionResult<BillingProfileDto>> UpdateBillingProfile(
        [FromBody] UpdateBillingProfileRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BillingCountry) || request.BillingCountry.Trim().Length != 2)
            return BadRequest(new { error = "Invalid billing country", code = "validation_error" });

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return NotFound(new { error = "No organization assigned" });

        bool? viesValidated = null;
        DateTime? vatValidatedAt = null;
        if (!string.IsNullOrWhiteSpace(request.VatId) &&
            !string.Equals(request.BillingCountry.Trim(), "IT", StringComparison.OrdinalIgnoreCase))
        {
            viesValidated = await viesService.ValidateVatIdAsync(
                request.BillingCountry,
                request.VatId.Replace(" ", string.Empty),
                ct);
            if (viesValidated != true)
                return BadRequest(new { error = "Invalid VAT id", code = "validation_error" });

            vatValidatedAt = DateTime.UtcNow;
        }

        var org = await orgService.UpdateBillingProfileAsync(
            orgId.Value,
            request.BillingCountry,
            request.VatId,
            vatValidatedAt,
            ct);
        if (org is null)
            return NotFound(new { error = "No organization assigned" });

        return Ok(new BillingProfileDto
        {
            BillingCountry = org.BillingCountry ?? request.BillingCountry,
            VatId = org.VatId,
            ViesValidated = viesValidated,
        });
    }

    private static SubscriptionDto Map(Casazen.Core.Entities.Org org) => new()
    {
        PlanTier = org.PlanTier.ToString(),
        Status = org.SubscriptionStatus switch
        {
            SubscriptionStatus.Trialing => "trialing",
            SubscriptionStatus.Active => "active",
            SubscriptionStatus.PastDue => "past_due",
            SubscriptionStatus.Canceled => "canceled",
            _ => "none",
        },
        CurrentPeriodEnd = org.CurrentPeriodEnd,
        BillingCountry = org.BillingCountry,
        VatId = org.VatId,
        StripeCustomerId = org.StripeCustomerId,
    };
}
