using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// Property owners for agency tier (ADR-012). Every action is scoped to the caller's agency org: an owner
/// of another org answers 404. Responses are DTOs, never the entity. IBAN is masked in the list;
/// full IBAN requires <c>agency.owner.financial.read</c> permission and is audit-logged.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerView)]
public class OwnersController(
    AppDbContext db,
    IOrgContextResolver orgContextResolver,
    ILogger<OwnersController> logger) : ControllerBase
{
    /// <summary>404: the owner IBAN is missing (<c>GET /api/owners/{id}/iban</c>).</summary>
    public const string IbanMissingCode = "owner_iban_missing";

    /// <summary>
    /// List of property owners for the agency. IBAN is masked (last 4 chars only, ADR-012-R3).
    /// Requires <c>agency.owner.view</c> policy.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<PropertyOwnerSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<PropertyOwnerSummaryDto>>> GetAll()
    {
        logger.LogInformation("Retrieving property owners list");

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var owners = await db.PropertyOwners
            .Where(o => o.OrgId == orgId.Value)
            .OrderBy(o => o.LastName)
            .ThenBy(o => o.FirstName)
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted);

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

        // Audit log: user viewed the full IBAN (ADR-012-R3). NEVER log the IBAN value itself.
        logger.LogInformation(
            "User {UserId} viewed full IBAN of property owner {OwnerId}",
            User.GetUserId(), id);

        Response.Headers.CacheControl = "private, no-store";
        return Ok(PropertyOwnerDtoMapper.ToIbanDto(owner));
    }
}
