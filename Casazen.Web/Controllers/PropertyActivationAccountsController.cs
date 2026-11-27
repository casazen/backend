using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/property-activation-accounts")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
public sealed class PropertyActivationAccountsController(
    PropertyActivationAccounts accounts,
    IOrgContextResolver orgContext,
    IHostScopeResolver scopeResolver,
    IAuthorizationService authorization) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ActivationAccountView>>> List(CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope)
            return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await accounts.ListAsync(scope, ct));
    }

    [HttpPost]
    [RequestSizeLimit(64_000)]
    public async Task<ActionResult<ActivationAccountView>> Store(ActivationAccountInput input, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope || !await CanManageAsync(input.Service))
            return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await accounts.StoreAsync(scope, input, ct));
    }

    [HttpPost("{id:guid}/run")]
    public async Task<ActionResult<IReadOnlyList<ActivationAccountOutcome>>> Run(Guid id, CancellationToken ct)
    {
        if (User.GetUserId() is not { Length: > 0 } actor || await ScopeAsync(ct) is not { } scope
            || !await CanManageAsync(await accounts.ServiceAsync(scope, id, ct)))
            return Forbid();
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await accounts.RunAsync(scope, actor, id, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (await ScopeAsync(ct) is not { } scope || !await CanManageAsync(await accounts.ServiceAsync(scope, id, ct)))
            return Forbid();
        await accounts.DeleteAsync(scope, id, ct);
        return NoContent();
    }

    private async Task<bool> CanManageAsync(string service) => !service.StartsWith("ota:", StringComparison.Ordinal)
        || (await authorization.AuthorizeAsync(User, null, CasazenPolicies.OtaWrite)).Succeeded;

    private async Task<HostScope?> ScopeAsync(CancellationToken ct) =>
        await orgContext.GetOrProvisionOrgIdAsync(ct) is { } orgId
            ? await scopeResolver.ResolveHostScopeAsync(User, orgId, ct)
            : null;
}
