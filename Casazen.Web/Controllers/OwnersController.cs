using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
public class OwnersController(
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    ILogger<OwnersController> logger) : ControllerBase
{
    public const string IbanMissingCode = "owner_iban_missing";

    [HttpGet]
    [ProducesResponseType(typeof(List<PropertyOwnerSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<PropertyOwnerSummaryDto>>> GetAll(
        [FromServices] IOwnerDirectoryService directory,
        [FromServices] IHostScopeResolver hostScopeResolver)
    {
        logger.LogInformation("Retrieving property owners list");

        if (await ScopeAsync(hostScopeResolver) is not { } scope)
            return Forbid();

        var owners = await directory.ListAsync(scope, HttpContext.RequestAborted);
        return Ok(owners.Select(PropertyOwnerDtoMapper.ToSummary).ToList());
    }

    /// <summary>
    /// Full IBAN of a property owner (ADR-012-R3). Requires <c>agency.owner.financial.read</c> permission
    /// and logs access (user, timestamp, owner ID) for audit. 404 when the owner has no IBAN or does not exist.
    /// Response is never cached.
    /// </summary>
    [HttpGet("{id:guid}/iban")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialRead)]
    [ProducesResponseType(typeof(PropertyOwnerIbanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyOwnerIbanDto>> GetIban(Guid id)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var owner = await db.PropertyOwners
            .Where(o => o.Id == id && o.OrgId == orgId.Value)
            .AsNoTracking()
            .FirstOrDefaultAsync(HttpContext.RequestAborted);

        if (owner is null)
        {
            logger.LogWarning("Property owner {OwnerId} not found or not in caller's org", id);
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(owner.Iban))
        {
            logger.LogInformation("Property owner {OwnerId} has no IBAN", id);
            return this.ApiProblem(StatusCodes.Status404NotFound, IbanMissingCode, "OwnerIbanMissing");
        }

        logger.LogInformation(
            "User {UserId} viewed full IBAN of property owner {OwnerId}",
            User.GetUserId(), id);

        Response.Headers.CacheControl = "private, no-store";
        return Ok(PropertyOwnerDtoMapper.ToIbanDto(owner));
    }

    [HttpGet("{id:guid}/address")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialRead)]
    [ProducesResponseType(typeof(OwnerAddressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerAddressDto>> GetAddress(
        Guid id,
        [FromServices] IOwnerMandateService mandates,
        [FromServices] IHostScopeResolver hostScopeResolver)
    {
        if (await ScopeAsync(hostScopeResolver) is not { } scope)
            return Forbid();

        var owner = await mandates.GetAddressAsync(scope, id, HttpContext.RequestAborted);
        logger.LogInformation("User {UserId} read the billing address of property owner {OwnerId}", User.GetUserId(), id);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(OwnerAddressDto.From(owner));
    }

    /// <summary>
    /// Replaces the structured billing address of the owner (<c>{ street, buildingNumber, postalCode, town, province, country }</c>), stored encrypted: the
    /// recipient of the owner invoice, the QR-bill and the pain.001.09 transfer (E13/F01/US01/T01). Requires <c>agency.owner.financial.manage</c> and an owner
    /// in the caller's reach. 422 <c>owner_address_invalid</c> (with the <c>field</c>); 404 <c>owner_not_found</c>.
    /// </summary>
    [HttpPut("{id:guid}/address")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerAddressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerAddressDto>> SaveAddress(
        Guid id,
        [FromBody] OwnerAddressCommand command,
        [FromServices] IOwnerMandateService mandates,
        [FromServices] IHostScopeResolver hostScopeResolver)
    {
        if (await ScopeAsync(hostScopeResolver) is not { } scope)
            return Forbid();

        var owner = await mandates.SaveAddressAsync(scope, id, command, User.GetUserId(), HttpContext.RequestAborted);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(OwnerAddressDto.From(owner));
    }

    private async Task<HostScope?> ScopeAsync(IHostScopeResolver hostScopeResolver)
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
    }
}
