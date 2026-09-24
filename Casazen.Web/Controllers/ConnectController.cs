using System.Globalization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Connect;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Stripe Connect onboarding of the caller's org (Express account). Starting or restarting the onboarding is for the
/// org's billing administrator (<see cref="CasazenPolicies.OrgBillingAdmin"/>, A3-42); the status is readable with
/// <see cref="CasazenPolicies.PaymentRead"/>.
/// </summary>
/// <remarks>
/// Stripe failures (BK-09, A3-19) never unlink the account: 503 <c>stripe_connect_unavailable</c> with <c>Retry-After</c>
/// (rate limit, Stripe outage, network), 503 <c>stripe_connect_not_configured</c> (platform key), 502
/// <c>stripe_connect_failed</c> (request refused), 409 <c>stripe_connect_account_unavailable</c> (account gone while
/// creating the link: the next attempt replaces it).
/// </remarks>
[ApiController]
[Route("api/connect")]
public class ConnectController(
    IOrgContextResolver orgContextResolver,
    IConnectOnboardingService connectOnboardingService,
    PublicSiteLinks publicSiteLinks) : ControllerBase
{
    /// <summary>503: Stripe is temporarily unavailable (429, 5xx, network); retry after <see cref="RetryAfterSeconds"/>.</summary>
    public const string UnavailableCode = "stripe_connect_unavailable";

    /// <summary>503: the platform's Stripe key is missing, invalid, expired or without permission.</summary>
    public const string NotConfiguredCode = "stripe_connect_not_configured";

    /// <summary>502: Stripe refused the request for another reason.</summary>
    public const string FailedCode = "stripe_connect_failed";

    /// <summary>409: the linked account disappeared while the link was being created.</summary>
    public const string AccountUnavailableCode = "stripe_connect_account_unavailable";

    /// <summary>503: <c>App:PublicSiteBaseUrl</c>, base of the return pages, is not configured (Development/Testing only).</summary>
    public const string ReturnUrlNotConfiguredCode = "connect_return_url_not_configured";

    /// <summary>Seconds of the <c>Retry-After</c> header of <see cref="UnavailableCode"/>.</summary>
    public const int RetryAfterSeconds = 10;

    /// <summary>Creates the org's Express account when missing (idempotent: parallel clicks create one account).</summary>
    [HttpPost("account")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(ConnectStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ConnectStatusDto>> CreateAccount(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        try
        {
            var status = await connectOnboardingService.EnsureExpressAccountAsync(orgId.Value, cancellationToken);
            return Ok(Map(status));
        }
        catch (StripeConnectException ex)
        {
            return StripeConnectProblem(ex);
        }
    }

    /// <summary>
    /// Account Link of the onboarding (creates the account when missing). <c>return_url</c> and <c>refresh_url</c> are the
    /// payments page of the web app on <c>App:PublicSiteBaseUrl</c> (A3-42): a request body is ignored, a client never
    /// chooses where Stripe sends the host.
    /// </summary>
    [HttpPost("onboarding-link")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OnboardingLinkResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<OnboardingLinkResponseDto>> CreateOnboardingLink(CancellationToken cancellationToken)
    {
        // Checked first: nothing is created on Stripe for a link that could not send the host back.
        if (!publicSiteLinks.IsConfigured)
        {
            return this.ApiProblem(
                StatusCodes.Status503ServiceUnavailable,
                ReturnUrlNotConfiguredCode,
                "ConnectReturnUrlNotConfigured");
        }

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        try
        {
            var url = await connectOnboardingService.CreateOnboardingLinkAsync(
                orgId.Value,
                publicSiteLinks.ConnectOnboardingReturn(),
                publicSiteLinks.ConnectOnboardingRefresh(),
                cancellationToken);

            return Ok(new OnboardingLinkResponseDto { Url = url });
        }
        catch (StripeConnectException ex)
        {
            return StripeConnectProblem(ex);
        }
    }

    [HttpGet("status")]
    [Authorize(Policy = CasazenPolicies.PaymentRead)]
    [ProducesResponseType(typeof(ConnectStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ConnectStatusDto>> GetStatus(
        [FromQuery] bool refresh = true,
        CancellationToken cancellationToken = default)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned");

        try
        {
            var status = await connectOnboardingService.GetStatusAsync(orgId.Value, refresh, cancellationToken);
            return Ok(Map(status));
        }
        catch (StripeConnectException ex)
        {
            return StripeConnectProblem(ex);
        }
    }

    /// <summary>The response of a failed Stripe call; the gateway already logged it with its cause.</summary>
    private ObjectResult StripeConnectProblem(StripeConnectException exception)
    {
        switch (exception.Failure)
        {
            case StripeConnectFailure.Transient:
                Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return this.ApiProblem(StatusCodes.Status503ServiceUnavailable, UnavailableCode, "StripeConnectUnavailable");
            case StripeConnectFailure.Configuration:
                return this.ApiProblem(StatusCodes.Status503ServiceUnavailable, NotConfiguredCode, "StripeConnectNotConfigured");
            case StripeConnectFailure.AccountUnavailable:
                return this.ApiProblem(StatusCodes.Status409Conflict, AccountUnavailableCode, "StripeConnectAccountUnavailable");
            default:
                return this.ApiProblem(StatusCodes.Status502BadGateway, FailedCode, "StripeConnectFailed");
        }
    }

    private static ConnectStatusDto Map(ConnectStatus status) => new()
    {
        ConnectedAccountId = status.ConnectedAccountId,
        ChargesEnabled = status.ChargesEnabled,
        PayoutsEnabled = status.PayoutsEnabled,
        DetailsSubmitted = status.DetailsSubmitted,
        RequirementsDue = status.RequirementsDue.ToList(),
    };
}
