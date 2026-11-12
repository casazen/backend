using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// One-way copy of a CondoLedger <c>condo-export-v1</c> snapshot. Staging does not change balances.
/// A house is posted only after a property in this organization is verified and the year matches to the cent.
/// This is not an administrator PDF reader.
/// </summary>
[ApiController]
[Route("api/long-rent/condominium-imports")]
[Authorize(Policy = CasazenPolicies.LongRentPropertyRead)]
public class CondominiumImportsController(
    ICondominiumHistoricalImportService imports,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumImportBatchView>> Stage(CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(cancellationToken);
        return Ok(await imports.StageAsync(scope, json, userId, cancellationToken));
    }

    [HttpPost("{batchId:guid}/houses/{houseId:guid}/property")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumImportBatchView>> VerifyProperty(
        Guid batchId, Guid houseId, [FromBody] VerifyImportPropertyDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await imports.VerifyPropertyAsync(scope, batchId, houseId, dto.PropertyId, userId, cancellationToken));
    }

    [HttpPost("{batchId:guid}/confirm")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumImportBatchView>> Confirm(Guid batchId, CancellationToken cancellationToken)
    {
        var (scope, userId) = await ScopeAsync(cancellationToken);
        if (scope is null || userId is null)
            return Unauthorized();
        return Ok(await imports.ConfirmAsync(scope, batchId, userId, cancellationToken));
    }

    private async Task<(HostScope? Scope, string? UserId)> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return (null, null);
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return (scope, User.GetUserId());
    }
}

public sealed class VerifyImportPropertyDto
{
    public Guid PropertyId { get; set; }
}
