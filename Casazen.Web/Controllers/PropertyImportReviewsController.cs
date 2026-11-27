using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/import-review")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public sealed class PropertyImportReviewsController(
    IPropertyImportReviewService reviews,
    IOrgContextResolver orgContext,
    IHostScopeResolver scopeResolver) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PropertyImportReviewView>> Get(Guid propertyId, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope)
            return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        var result = await reviews.GetAsync(scope, propertyId, ct);
        return result is null ? NoContent() : Ok(result);
    }

    [HttpPost("recheck")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<ActionResult<PropertyImportReviewView>> Recheck(Guid propertyId, ReviewImportedProperty request, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope)
            return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await reviews.RecheckAsync(scope, propertyId, request.ConfirmedField, ct));
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken ct) =>
        await orgContext.GetOrProvisionOrgIdAsync(ct) is { } orgId
            ? await scopeResolver.ResolveHostScopeAsync(User, orgId, ct)
            : null;
}

public sealed record ReviewImportedProperty([MaxLength(40)] string? ConfirmedField);

[ApiController]
[Route("api/admin/property-import-reviews")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminPropertyImportReviewsController(IPropertyImportReviewService reviews) : ControllerBase
{
    [HttpPut("{orgId:guid}/{propertyId:guid}")]
    [RequestSizeLimit(2_000_000)]
    public async Task<ActionResult<PropertyImportReviewView>> Record(
        Guid orgId, Guid propertyId, PropertyImportSnapshot snapshot, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await reviews.RecordAsync(orgId, propertyId, snapshot, ct));
    }
}
