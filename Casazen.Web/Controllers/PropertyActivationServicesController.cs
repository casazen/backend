using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/activation-services")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public sealed class PropertyActivationServicesController(
    PropertyActivationServices services,
    IOrgContextResolver orgContext,
    IHostScopeResolver scopeResolver) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PropertyActivationServicesView>> Get(Guid propertyId, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope)
            return Forbid();
        return Ok(await services.GetAsync(scope, propertyId, ct));
    }

    [HttpPut("portals")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    public async Task<ActionResult<PropertyActivationServicesView>> Select(Guid propertyId, RequiredActivationPortals input, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope)
            return Forbid();
        return Ok(await services.SetPortalsAsync(scope, propertyId, input.Portals, ct));
    }

    [HttpPost("compliance")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<ActionResult<AssistRequestView>> RequestHelp(Guid propertyId, ActivationComplianceRequest input, CancellationToken ct)
    {
        if (User.GetUserId() is not { Length: > 0 } userId || await ScopeAsync(ct) is not { } scope)
            return Forbid();
        return Ok(await services.RequestAsync(scope, userId, propertyId, input.Service, ct));
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken ct) =>
        await orgContext.GetOrProvisionOrgIdAsync(ct) is { } orgId
            ? await scopeResolver.ResolveHostScopeAsync(User, orgId, ct)
            : null;
}

public sealed record RequiredActivationPortals([Required, MaxLength(40)] string[] Portals);
public sealed record ActivationComplianceRequest([Required, MaxLength(20)] string Service);
