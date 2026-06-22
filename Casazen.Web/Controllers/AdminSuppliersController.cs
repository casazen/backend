using Casazen.Core.Services;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Platform-admin endpoints for supplier management (US-022 / #292, AC3).
/// </summary>
[ApiController]
[Route("api/admin/suppliers")]
[Authorize(Policy = "AdminOnly")]
public class AdminSuppliersController(
    ISupplierService supplierService,
    ILogger<AdminSuppliersController> logger) : ControllerBase
{
    /// <summary>Sends an invite email to a prospective supplier for a given comune.</summary>
    [HttpPost("invite")]
    [ProducesResponseType(typeof(AdminInviteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminInviteResponse>> InviteSupplier(
        [FromBody] AdminInviteSupplierRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var invite = await supplierService.CreateInviteAsync(
                request.Email,
                request.ComuneCode,
                request.Categories,
                request.Message,
                cancellationToken);

            logger.LogInformation(
                "Admin supplier invite created: {InviteId} for {Email} in {Comune}",
                invite.InviteId, request.Email, request.ComuneCode);

            return CreatedAtAction(nameof(InviteSupplier), new AdminInviteResponse
            {
                InviteId = invite.InviteId,
                ExpiresAt = invite.ExpiresAt,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message, code = "duplicate_invite" });
        }
    }
}
