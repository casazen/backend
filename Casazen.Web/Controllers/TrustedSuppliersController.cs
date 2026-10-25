using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Trusted supplier management for agency tier orgs (NF-21, ADR-020).
/// All endpoints require Agency tier.
/// </summary>
[ApiController]
[Route("api/trusted-suppliers")]
[Authorize(Policy = CasazenPolicies.AgencySupplierManage)]
public class TrustedSuppliersController(
    ITrustedSupplierService trustedSupplierService,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Invites a supplier to become a trusted supplier of the agency.
    /// </summary>
    /// <param name="request">Supplier email and property scope.</param>
    /// <returns>Invitation token and expiration.</returns>
    [HttpPost("invite")]
    [ProducesResponseType(typeof(InviteSupplierResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<InviteSupplierResponse>> InviteSupplier([FromBody] InviteSupplierRequestDto request)
    {
        var orgId = orgContextResolver.GetOrgId(User);
        var actorUserId = User.GetUserId();

        var result = await trustedSupplierService.InviteSupplierAsync(
            orgId,
            new InviteSupplierRequest(request.Email, request.PropertyIds ?? new List<Guid>()),
            actorUserId);

        return Ok(new InviteSupplierResponse(result.InvitationToken, result.ExpiresAt));
    }

    /// <summary>
    /// Gets all trusted suppliers for the agency.
    /// </summary>
    /// <returns>List of trusted suppliers.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(List<TrustedSupplierDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<TrustedSupplierDto>>> GetTrustedSuppliers()
    {
        var orgId = orgContextResolver.GetOrgId(User);

        var suppliers = await trustedSupplierService.GetTrustedSuppliersAsync(orgId);

        var dtos = suppliers.Select(s => new TrustedSupplierDto
        {
            Id = s.Id,
            SupplierOrgId = s.SupplierOrgId,
            AssignedPropertyIds = s.AssignedPropertyIds,
            Priority = s.Priority,
            VisibleInMarketplace = s.VisibleInMarketplace,
            CreatedAt = s.CreatedAt
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Updates marketplace visibility for a trusted supplier.
    /// </summary>
    /// <param name="id">Trusted supplier ID.</param>
    /// <param name="request">New visibility state.</param>
    [HttpPut("{id}/visibility")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateVisibility(Guid id, [FromBody] UpdateVisibilityRequest request)
    {
        var orgId = orgContextResolver.GetOrgId(User);
        var actorUserId = User.GetUserId();

        await trustedSupplierService.UpdateMarketplaceVisibilityAsync(orgId, id, request.Visible, actorUserId);

        return NoContent();
    }

    /// <summary>
    /// Removes a trusted supplier relationship.
    /// </summary>
    /// <param name="id">Trusted supplier ID.</param>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveTrustedSupplier(Guid id)
    {
        var orgId = orgContextResolver.GetOrgId(User);
        var actorUserId = User.GetUserId();

        await trustedSupplierService.RemoveTrustedSupplierAsync(orgId, id, actorUserId);

        return NoContent();
    }
}

// DTOs
public sealed record InviteSupplierRequestDto(string Email, List<Guid>? PropertyIds);
public sealed record InviteSupplierResponse(string InvitationToken, DateTime ExpiresAt);
public sealed record UpdateVisibilityRequest(bool Visible);

public sealed class TrustedSupplierDto
{
    public Guid Id { get; set; }
    public Guid SupplierOrgId { get; set; }
    public List<Guid> AssignedPropertyIds { get; set; } = new();
    public int Priority { get; set; }
    public bool VisibleInMarketplace { get; set; }
    public DateTime CreatedAt { get; set; }
}
