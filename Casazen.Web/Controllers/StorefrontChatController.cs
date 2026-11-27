using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[AllowAnonymous]
[PrivateAnswer]
[Route("api/public/orgs/{slug}/properties/{propertyId:guid}/chat")]
public sealed class StorefrontChatController(IOrgService orgs, StorefrontChatService chat) : ControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.StorefrontChatRead)]
    public async Task<ActionResult<IReadOnlyList<StorefrontChatLine>>> Get(
        string slug, Guid propertyId, [FromHeader(Name = StorefrontChatService.AccessHeader)] string? access, CancellationToken ct)
    {
        var org = await orgs.GetPublicBySlugAsync(slug, ct);
        if (org is null)
            return NotFound();
        return Ok(await chat.GetAsync(org.Id, propertyId, access, ct));
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.StorefrontChatWrite)]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Send(
        string slug, Guid propertyId, ChatRequest request,
        [FromHeader(Name = StorefrontChatService.AccessHeader)] string? access,
        [FromHeader(Name = "Idempotency-Key")] string? messageKey, CancellationToken ct)
    {
        var org = await orgs.GetPublicBySlugAsync(slug, ct);
        if (org is null)
            return NotFound();
        await chat.SendAsync(org.Id, propertyId, access, messageKey, request.Text, request.Name, request.Email, request.Consent, ct);
        return NoContent();
    }

    public sealed record ChatRequest(string? Text, string? Name, string? Email, bool Consent);
}
