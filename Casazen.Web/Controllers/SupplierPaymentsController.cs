using System.Globalization;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's Stripe Connect account (SP-14, decision D2): the account where the payments of its jobs will land. The
/// supplier starts the Express onboarding, reads the state (charges, payouts, what Stripe still needs, "Verificato") and opens
/// its Express Dashboard. <b>No payment is created here</b> (SP-15). Behind <see cref="FeatureFlags.SupplierOnlinePayments"/>:
/// 404 while the flag is off, before authentication.
/// </summary>
/// <remarks>
/// The org is the caller's own supplier org (its supplier link, never a value of the request; a dual-role account uses the
/// supplier org here and its host org on <c>api/connect</c>) and is never provisioned. The host's <see cref="ConnectController"/>
/// is not changed: both use <see cref="IConnectOnboardingService"/>, so the account is created once per org (advisory lock,
/// idempotency key <c>connect-account:{orgId}</c>) and a Stripe failure never unlinks it (BK-09). The return pages are built
/// by the server (<see cref="PublicSiteLinks.SupplierConnectOnboardingReturn"/>), a request body is ignored. The responses
/// carry <c>Cache-Control: private, no-store</c>: the links are single-use credentials.
/// Errors: 404 <c>not_found</c> (no linked supplier org); 422 <c>supplier_payments_not_ready</c> (dashboard link without a
/// ready account); 409 <c>stripe_connect_account_unavailable</c> (the linked account is gone: the next onboarding link replaces
/// it); 503 <c>stripe_connect_unavailable</c> (with <c>Retry-After</c>), <c>stripe_connect_not_configured</c> and
/// <c>connect_return_url_not_configured</c>; 502 <c>stripe_connect_failed</c>.
/// </remarks>
[ApiController]
[Route("api/supplier/payments")]
[Authorize(Policy = CasazenPolicies.Supplier)]
[FeatureGate(FeatureFlags.SupplierOnlinePayments)]
public class SupplierPaymentsController(
    ISupplierPaymentsAccountService payments,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    PublicSiteLinks publicSiteLinks) : ControllerBase
{
    /// <summary>
    /// The state of the account, read from the database; with <c>refresh=true</c> read again from Stripe first (the page that
    /// Stripe sends the supplier back to asks for it). Without an account every flag is false and nothing reaches Stripe.
    /// </summary>
    [HttpGet("account")]
    [ProducesResponseType(typeof(SupplierPaymentsAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SupplierPaymentsAccountDto>> GetAccount(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            async orgId => Ok(Map(await payments.GetAccountAsync(orgId, refresh, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Creates the supplier's Express account when missing (idempotent: parallel requests create one account) and returns the
    /// state. The onboarding link creates it too: this is only for a client that wants the account before the link.
    /// </summary>
    [HttpPost("account")]
    [ProducesResponseType(typeof(SupplierPaymentsAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SupplierPaymentsAccountDto>> EnsureAccount(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(Map(await payments.EnsureAccountAsync(orgId, cancellationToken))),
            cancellationToken);

    /// <summary>
    /// Account Link of the onboarding (creates the account when missing). <c>return_url</c> and <c>refresh_url</c> are the
    /// supplier settings page of the web app on <c>App:PublicSiteBaseUrl</c> (<c>?stripe_return=1</c>, <c>?stripe_refresh=1</c>);
    /// a request body is ignored, a client never chooses where Stripe sends the supplier.
    /// </summary>
    [HttpPost("onboarding-link")]
    [ProducesResponseType(typeof(SupplierPaymentsLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SupplierPaymentsLinkDto>> CreateOnboardingLink(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId =>
            {
                // Checked before anything is created on Stripe: no account, no link that could not send the supplier back.
                if (!publicSiteLinks.IsConfigured)
                {
                    return this.ApiProblem(
                        StatusCodes.Status503ServiceUnavailable,
                        ConnectController.ReturnUrlNotConfiguredCode,
                        "ConnectReturnUrlNotConfigured");
                }

                var url = await payments.CreateOnboardingLinkAsync(
                    orgId,
                    publicSiteLinks.SupplierConnectOnboardingReturn(),
                    publicSiteLinks.SupplierConnectOnboardingRefresh(),
                    cancellationToken);
                return Ok(new SupplierPaymentsLinkDto { Url = url });
            },
            cancellationToken);

    /// <summary>
    /// Single-use login link to the supplier's Express Dashboard on Stripe. Needs an existing account: 422
    /// <c>supplier_payments_not_ready</c> without one, or when Stripe refuses it because the onboarding is incomplete (the way
    /// forward is the onboarding link). It never creates an account.
    /// </summary>
    [HttpPost("dashboard-link")]
    [ProducesResponseType(typeof(SupplierPaymentsLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SupplierPaymentsLinkDto>> CreateDashboardLink(CancellationToken cancellationToken) =>
        await RunAsync(
            async orgId => Ok(new SupplierPaymentsLinkDto { Url = await payments.CreateDashboardLinkAsync(orgId, cancellationToken) }),
            cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> for the caller's linked supplier org. The org comes only from the caller's own supplier
    /// link (<see cref="ISupplierOrgContextResolver.GetLinkedSupplierOrgIdAsync"/>): the payments account never provisions a
    /// supplier org. A Stripe failure becomes the response of the host's Connect onboarding (BK-09); the gateway already
    /// logged it with its cause.
    /// </summary>
    private async Task<ActionResult> RunAsync(Func<Guid, Task<ActionResult>> action, CancellationToken cancellationToken)
    {
        // The links are single-use credentials and the state is private: nothing is cached by the browser or a proxy.
        Response.Headers.CacheControl = "private, no-store";

        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        try
        {
            return await action(orgId.Value);
        }
        catch (StripeConnectException ex)
        {
            return StripeConnectProblem(ex);
        }
    }

    /// <summary>The response of a failed Stripe call: the same codes and texts as the host's Connect onboarding.</summary>
    private ObjectResult StripeConnectProblem(StripeConnectException exception)
    {
        switch (exception.Failure)
        {
            case StripeConnectFailure.Transient:
                Response.Headers.RetryAfter = ConnectController.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return this.ApiProblem(
                    StatusCodes.Status503ServiceUnavailable, ConnectController.UnavailableCode, "StripeConnectUnavailable");
            case StripeConnectFailure.Configuration:
                return this.ApiProblem(
                    StatusCodes.Status503ServiceUnavailable, ConnectController.NotConfiguredCode, "StripeConnectNotConfigured");
            case StripeConnectFailure.AccountUnavailable:
                return this.ApiProblem(
                    StatusCodes.Status409Conflict, ConnectController.AccountUnavailableCode, "StripeConnectAccountUnavailable");
            default:
                return this.ApiProblem(StatusCodes.Status502BadGateway, ConnectController.FailedCode, "StripeConnectFailed");
        }
    }

    private static SupplierPaymentsAccountDto Map(SupplierPaymentsAccount account) => new()
    {
        HasAccount = account.HasAccount,
        ChargesEnabled = account.ChargesEnabled,
        PayoutsEnabled = account.PayoutsEnabled,
        DetailsSubmitted = account.DetailsSubmitted,
        RequirementsDue = account.RequirementsDue.ToList(),
        CanReceivePayments = account.CanReceivePayments,
        Verified = account.Verified,
        VerificationMissing = account.VerificationMissing.ToList(),
    };
}
