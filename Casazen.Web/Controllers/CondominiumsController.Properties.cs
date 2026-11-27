using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

public partial class CondominiumsController
{
    [HttpGet("properties")]
    public async Task<ActionResult<IReadOnlyList<PropertyCondominiumView>>> Properties(CancellationToken ct)
    {
        var (scope, _) = await ScopeAsync(ct);
        return scope is null ? Unauthorized() : Ok(await condominiums.ListPropertiesAsync(scope, ct));
    }

    [HttpPost("properties/{propertyId:guid}")]
    [Authorize(Policy = CasazenPolicies.LongRentPropertyWrite)]
    public async Task<ActionResult<CondominiumView>> SetupProperty(Guid propertyId, [FromBody] CreateCondominiumDto dto, CancellationToken ct)
    {
        var (scope, user) = await ScopeAsync(ct);
        return scope is null || user is null ? Unauthorized() : Ok(await condominiums.SetupPropertyAsync(scope, propertyId,
            new(dto.Name, dto.AdministratorName, dto.AdministratorEmail, dto.AdministratorPhone, dto.FiscalStartMonth), user, ct));
    }
}
