using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Billing;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/billing")]
public class BillingController(
    IOrgContextResolver orgContextResolver,
    IOrgService orgService,
    IStripeBillingService stripeBillingService,
    IBillingCheckoutService billingCheckoutService,
    IBillingEntryGate billingEntryGate,
    IViesService viesService,
    IConfiguration configuration) : ControllerBase
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

    /// <summary>
    /// Starts the Stripe Checkout of a paid plan. An org that already has a subscription (active, trialing, past due,
    /// unpaid or waiting for its first payment, stored or already on Stripe) gets 409 <c>already_subscribed</c>: it
    /// changes plan or pays from the billing portal (<c>POST /api/billing/portal-session</c>), never through a second
    /// subscription (A1-10). A repeated request (double click) returns the same open session.
    /// </summary>
    [HttpPost("checkout-session")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CheckoutSessionResponse>> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionRequest request,
        CancellationToken ct)
    {
        if (!PlanCatalog.TryParseTier(request.PlanTier, out var planTier))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingPlanTierUnknown");

        if (string.IsNullOrWhiteSpace(request.BillingCountry) || request.BillingCountry.Trim().Length != 2)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingCountryInvalid");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        try
        {
            await billingEntryGate.AssertCanChargeAsync(ct);
        }
        catch (BillingGateClosedException)
        {
            return this.ApiProblem(StatusCodes.Status409Conflict, "billing_gate_closed", "BillingNotAvailable");
        }

        var org = await orgService.GetByIdAsync(orgId.Value, ct);
        if (org is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "OrganizationNotFound");

        // Refused before the billing profile changes; BillingCheckoutService checks again under the org lock and on Stripe.
        if (BillingSubscriptionPolicy.BlocksNewCheckout(org))
        {
            return this.ApiProblem(
                StatusCodes.Status409Conflict,
                BillingSubscriptionPolicy.AlreadySubscribedCode,
                BillingSubscriptionPolicy.AlreadySubscribedMessageKey);
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
                return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingVatIdInvalid");
            }

            vatValidatedAt = DateTime.UtcNow;
        }

        await orgService.UpdateBillingProfileAsync(org.Id, request.BillingCountry, request.VatId, vatValidatedAt, ct);

        var successUrl = request.SuccessUrl ?? "https://app.casazen.app/settings/billing?checkout=success";
        var cancelUrl = request.CancelUrl ?? "https://app.casazen.app/settings/billing/plans?checkout=cancel";
        var url = await billingCheckoutService.StartCheckoutAsync(org.Id, planTier, successUrl, cancelUrl, ct);
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
            SubscriptionStatus.Incomplete => "incomplete",
            SubscriptionStatus.Unpaid => "unpaid",
            _ => "none",
        },
        CurrentPeriodEnd = org.CurrentPeriodEnd,
        BillingCountry = org.BillingCountry,
        VatId = org.VatId,
    };
}
