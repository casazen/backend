using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public supplier registration + host-facing supplier discovery endpoints (US-022 / #292).
/// </summary>
[ApiController]
[Route("api/suppliers")]
public class SuppliersController(
    ISupplierService supplierService,
    ILogger<SuppliersController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Public self-serve supplier registration. Optionally validates an admin invite token.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SupplierRegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupplierRegisterResponse>> Register(
        [FromBody] SupplierRegisterRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (org, _) = await supplierService.RegisterAsync(
                request.Email,
                request.LegalName,
                request.Phone,
                request.ComuneCode,
                request.InviteToken,
                cancellationToken);

            logger.LogInformation("Supplier registered: {OrgId} for {Email}", org.Id, request.Email);

            return CreatedAtAction(nameof(Register), new SupplierRegisterResponse
            {
                OrgId = org.Id,
                AuthRedirectUrl = "/supplier/activation",
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns <c>Active</c> suppliers for a comune. Available to hosts (PropertyOwner role).
    /// </summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(PagedResultDto<SupplierPickerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResultDto<SupplierPickerDto>>> GetSuppliers(
        [FromQuery] string comune,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var suppliers = await supplierService.GetActiveByComune(comune, category, cancellationToken);

        var items = suppliers.Select(sp => new SupplierPickerDto
        {
            OrgId = sp.OrgId,
            LegalName = sp.LegalName,
            Phone = sp.Phone,
            Email = sp.Email,
            Categories = JsonSerializer.Deserialize<IEnumerable<string>>(sp.CategoriesJson, JsonOpts) ?? [],
            Comuni = JsonSerializer.Deserialize<IEnumerable<string>>(sp.ComuniJson, JsonOpts) ?? [],
            Bio = sp.Bio,
            PhotoUrls = JsonSerializer.Deserialize<IEnumerable<string>>(sp.PhotoUrlsJson, JsonOpts) ?? [],
        });

        return Ok(new PagedResultDto<SupplierPickerDto>
        {
            Items = items,
            TotalCount = suppliers.Count,
            Page = 1,
            PageSize = suppliers.Count,
        });
    }
}
