using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Trusted supplier management for the authorised organisation.
/// </summary>
[ApiController]
[Route("api/trusted-suppliers")]
[Authorize(Policy = CasazenPolicies.TrustedSupplierManage)]
public class TrustedSuppliersController(
    ITrustedSupplierService trustedSupplierService,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Invites a supplier to become a trusted supplier of the agency (bug #977): the supplier gets the email of a supplier
    /// invite and, signed in with that email, registers (or, already registered, confirms) on the page of the link; the
    /// relation appears in the list then. The link token is only in the email. Errors: 422
    /// <c>trusted_supplier_invite_email_invalid</c>, <c>trusted_supplier_property_invalid</c>,
    /// <c>trusted_supplier_invite_comune_missing</c>; 409 <c>trusted_supplier_already_added</c>, <c>duplicate_invite</c>.
    /// </summary>
    /// <param name="request">Supplier email and property scope.</param>
    /// <returns>The invite and its expiry.</returns>
    [HttpPost("invite")]
    [ProducesResponseType(typeof(InviteSupplierResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InviteSupplierResponse>> InviteSupplier([FromBody] InviteSupplierRequestDto request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var actorUserId = User.GetUserId() ?? string.Empty;

        var result = await trustedSupplierService.InviteSupplierAsync(
            orgId.Value,
            new InviteSupplierRequest(request.Email, request.PropertyIds ?? [], request.VatNumber, request.FiscalCode, request.Phone),
            actorUserId);

        return Ok(new InviteSupplierResponse(result.InviteId, result.ExpiresAt));
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
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var suppliers = await trustedSupplierService.GetTrustedSuppliersAsync(orgId.Value);

        var dtos = suppliers.Select(s => new TrustedSupplierDto
        {
            Id = s.Id,
            SupplierOrgId = s.SupplierOrgId,
            SupplierName = s.SupplierName,
            HostOrigin = s.HostOrigin,
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
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var actorUserId = User.GetUserId() ?? string.Empty;

        await trustedSupplierService.UpdateMarketplaceVisibilityAsync(orgId.Value, id, request.Visible, actorUserId);

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
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Unauthorized();

        var actorUserId = User.GetUserId() ?? string.Empty;

        await trustedSupplierService.RemoveTrustedSupplierAsync(orgId.Value, id, actorUserId);

        return NoContent();
    }
}

// DTOs
public sealed record InviteSupplierRequestDto(string Email, List<Guid>? PropertyIds,
    [System.ComponentModel.DataAnnotations.MaxLength(32)] string? VatNumber = null,
    [System.ComponentModel.DataAnnotations.MaxLength(24)] string? FiscalCode = null,
    [System.ComponentModel.DataAnnotations.MaxLength(50)] string? Phone = null);
public sealed record InviteSupplierResponse(Guid InviteId, DateTime ExpiresAt);
public sealed record UpdateVisibilityRequest(bool Visible);

public sealed class TrustedSupplierDto
{
    public Guid Id { get; set; }
    public Guid SupplierOrgId { get; set; }
    public List<Guid> AssignedPropertyIds { get; set; } = new();
    public string? SupplierName { get; set; }
    public bool HostOrigin { get; set; }
    public int Priority { get; set; }
    public bool VisibleInMarketplace { get; set; }
    public DateTime CreatedAt { get; set; }
}
