using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
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
    PublicSiteLinks publicSiteLinks,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>503: the public URL of the web app, base of the Stripe return pages, is not configured (PL-11).</summary>
    private const string ReturnUrlNotConfiguredCode = "billing_return_url_not_configured";

    /// <summary>
    /// Plans of the catalogue. <c>purchasable</c> is false for a plan whose Stripe Price id is not configured in this
    /// environment (<c>Billing__Prices__&lt;Tier&gt;</c>, PL-11): its checkout answers 422 <c>billing_plan_unavailable</c>.
    /// </summary>
    [HttpGet("plans")]
    [Authorize]
    public ActionResult<IEnumerable<PlanCatalogDto>> GetPlans() =>
        Ok(PlanCatalog.All.Select(e =>
        {
            var priceId = BillingPrices.Resolve(configuration, e.Tier);
            return new PlanCatalogDto
            {
                Tier = e.Tier.ToString(),
                DisplayName = configuration[$"Billing:Display:{e.Tier}:Name"] ?? e.DisplayName,
                PriceMonthly = configuration.GetValue<decimal>($"Billing:Display:{e.Tier}:PriceMonthly", 0m),
                Currency = "EUR",
                UnitAllowance = e.MaxProperties == int.MaxValue ? -1 : e.MaxProperties,
                Features = configuration.GetSection($"Billing:Display:{e.Tier}:Features").Get<string[]>() ?? [e.Description],
                StripePriceId = priceId ?? string.Empty,
                Purchasable = priceId is not null,
            };
        }));

    /// <summary>
    /// Starts the Stripe Checkout of a paid plan. An org that already has a subscription (active, trialing, past due,
    /// unpaid or waiting for its first payment, stored or already on Stripe) gets 409 <c>already_subscribed</c>: it
    /// changes plan or pays from the billing portal (<c>POST /api/billing/portal-session</c>), never through a second
    /// subscription (A1-10). A repeated request (double click) returns the same open session.
    /// <para>
    /// Return pages (PL-11, A1-31): by default the plan page of the web app on <c>App:PublicSiteBaseUrl</c>
    /// (<c>?checkout=success</c> / <c>?checkout=cancel</c>). <c>successUrl</c> and <c>cancelUrl</c> sent by the client must
    /// be absolute URLs of that same site, otherwise 400 <c>validation_error</c>: Stripe never sends the browser to
    /// another site. A plan without a Stripe Price id in this environment answers 422 <c>billing_plan_unavailable</c>.
    /// </para>
    /// </summary>
    [HttpPost("checkout-session")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(CheckoutSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CheckoutSessionResponse>> CreateCheckoutSession(
        [FromBody] CreateCheckoutSessionRequest request,
        CancellationToken ct)
    {
        if (!PlanCatalog.TryParseTier(request.PlanTier, out var planTier))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingPlanTierUnknown");

        if (string.IsNullOrWhiteSpace(request.BillingCountry) || request.BillingCountry.Trim().Length != 2)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingCountryInvalid");

        // Checked before any change (billing profile, Stripe customer): nothing happens for a request that cannot pay.
        if (!publicSiteLinks.IsConfigured)
        {
            return this.ApiProblem(
                StatusCodes.Status503ServiceUnavailable,
                ReturnUrlNotConfiguredCode,
                "BillingReturnUrlNotConfigured");
        }

        if (!IsAllowedReturnUrl(request.SuccessUrl) || !IsAllowedReturnUrl(request.CancelUrl))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "BillingReturnUrlNotAllowed");

        if (BillingPrices.Resolve(configuration, planTier) is null)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                BillingPrices.PlanUnavailableCode,
                BillingPrices.PlanUnavailableMessageKey);
        }

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

        var successUrl = string.IsNullOrWhiteSpace(request.SuccessUrl)
            ? publicSiteLinks.BillingCheckoutSuccess()
            : request.SuccessUrl.Trim();
        var cancelUrl = string.IsNullOrWhiteSpace(request.CancelUrl)
            ? publicSiteLinks.BillingCheckoutCancel()
            : request.CancelUrl.Trim();
        var url = await billingCheckoutService.StartCheckoutAsync(org.Id, planTier, successUrl, cancelUrl, ct);
        return Ok(new CheckoutSessionResponse { CheckoutUrl = url });
    }

    /// <summary>
    /// Stripe billing portal of the org. It links back to the plan page of the web app on <c>App:PublicSiteBaseUrl</c>
    /// (PL-11, A1-31); 503 <c>billing_return_url_not_configured</c> when that URL is not set (Development/Testing only).
    /// </summary>
    [HttpPost("portal-session")]
    [Authorize(Policy = "RequireOrgBillingAdmin")]
    public async Task<ActionResult<PortalSessionResponse>> CreatePortalSession(CancellationToken ct)
    {
        if (!publicSiteLinks.IsConfigured)
        {
            return this.ApiProblem(
                StatusCodes.Status503ServiceUnavailable,
                ReturnUrlNotConfiguredCode,
                "BillingReturnUrlNotConfigured");
        }

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
            PortalUrl = await stripeBillingService.CreatePortalSessionAsync(org, publicSiteLinks.BillingPortalReturn(), ct),
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

    /// <summary>A return URL of the client: absent (the default page is used) or a page of the public web app.</summary>
    private bool IsAllowedReturnUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) || publicSiteLinks.IsOnPublicSite(url);

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
