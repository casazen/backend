using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The package the accountant got a link to (E15/F01/US02/T02, NF-101): <c>GET api/public/accountant-package/{orgId}/{period}?expiresAt=&amp;signature=</c>, anonymous (the accountant has no CasaZen account), streams the
/// ZIP the monthly e-mail announced.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>One answer.</b> A link that is wrong, changed, expired, made for another org or month, a malformed one, one whose package is gone and one that was made while the secret was not configured are all the same
/// 404 <c>accountant_package_link_invalid</c>, with the same body: nothing says which part was wrong, and nothing of the org is in it.</item>
/// <item><b>The proof is the signature</b> (HMAC-SHA256 of the org, the month and the expiry, with the secret of the signed links, compared in constant time, <see cref="IAccountantPackageDeliveryService.OpenAsync"/>).
/// Nothing is read from the database: the package is in the private storage under a key made of the org and the month.</item>
/// <item><b>Never stored by a cache, never indexed</b> (<see cref="PrivateAnswerAttribute"/>, and <c>Cache-Control: private, no-store</c> on the file). The log carries the org and the month, never the link, the
/// signature or an address.</item>
/// <item><b>Rate limited per client IP</b> (<see cref="RateLimitPolicies.PublicAccountantPackage"/>) and behind <c>Features:AccountantPackage</c> (404 while it is off, before anything is read).</item>
/// </list>
/// </remarks>
[ApiController]
[Route("api/public/accountant-package")]
[AllowAnonymous]
[FeatureGate(FeatureFlags.AccountantPackage)]
[EnableRateLimiting(RateLimitPolicies.PublicAccountantPackage)]
[PrivateAnswer]
public class PublicAccountantPackageController(IAccountantPackageDeliveryService delivery) : ControllerBase
{
    /// <summary>
    /// The ZIP of the package, <c>application/zip</c>, <c>casazen-commercialista-{period}.zip</c>. 404 <c>accountant_package_link_invalid</c> for every link that does not open it. <paramref name="orgId"/> is
    /// read here and not by the router, so a malformed one is the same answer as any other link that does not open.
    /// </summary>
    [HttpGet("{orgId}/{period}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string orgId, string period, [FromQuery] string? expiresAt, [FromQuery] string? signature, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(orgId, "D", out var id))
            return LinkInvalid();

        var file = await delivery.OpenAsync(id, period, expiresAt, signature, cancellationToken);
        if (file is null)
            return LinkInvalid();

        Response.Headers.CacheControl = "private, no-store";
        return File(file.Content, "application/zip", file.FileName);
    }

    private ObjectResult LinkInvalid() =>
        this.ApiProblem(StatusCodes.Status404NotFound, AccountantPackageErrors.LinkInvalid, "AccountantPackageLinkInvalid");
}
