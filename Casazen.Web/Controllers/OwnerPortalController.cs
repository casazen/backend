using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.OwnerPortal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// Owner portal API endpoints (ADR-011).
/// All endpoints enforce owner_portal policy and filter by active PropertyOwnership period (ADR-011-R2, ADR-011-R8).
/// Guest data is masked per ADR-011-R3: names NULL, emails NULL, phones NULL.
/// Returns 404 for other owner's data (ADR-011-R2).
/// </summary>
[ApiController]
[Route("api/owner-portal")]
[Authorize(Policy = CasazenPolicies.OwnerPortal)]
public class OwnerPortalController(
    IOwnerPortalService ownerPortalService,
    IOptions<Auth0Options> auth0Options,
    ILogger<OwnerPortalController> logger) : ControllerBase
{
    private readonly string? _customClaimNamespace = auth0Options.Value.CustomClaimNamespace;

    /// <summary>
    /// Gets properties owned by the authenticated owner with occupancy and revenue summaries (ADR-011-R8).
    /// Filters by PropertyOwnership.OwnerUserId and active ownership period.
    /// Guest data is masked in responses (ADR-011-R3).
    /// </summary>
    [HttpGet("properties")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalPropertyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalPropertyDto>>> GetProperties(
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var properties = await ownerPortalService.GetOwnerPropertiesAsync(
            ownerUserId.Value,
            cancellationToken);

        var dtos = properties.Select(p => new OwnerPortalPropertyDto
        {
            PropertyId = p.PropertyId,
            PropertyName = p.PropertyName,
            Address = p.Address,
            TotalBookings = p.TotalBookings,
            TotalRevenue = p.TotalRevenue,
            OccupancyRate = p.OccupancyRate,
            RecentBookings = p.RecentBookings.Select(b => new OwnerPortalBookingSummaryDto
            {
                BookingId = b.BookingId,
                CheckInDate = b.CheckInDate,
                CheckOutDate = b.CheckOutDate,
                Channel = b.Channel,
                TotalAmount = b.TotalAmount,
                Status = b.Status
            }).ToList()
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets published monthly statements for the owner's properties (ADR-011-R2).
    /// Returns empty list for now (statements feature not yet implemented).
    /// </summary>
    [HttpGet("statements")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalStatementDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalStatementDto>>> GetStatements(
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var statements = await ownerPortalService.GetOwnerStatementsAsync(
            ownerUserId.Value,
            cancellationToken);

        var dtos = statements.Select(s => new OwnerPortalStatementDto
        {
            StatementId = s.StatementId,
            PropertyId = s.PropertyId,
            PropertyName = s.PropertyName,
            Month = s.Month,
            Year = s.Year,
            TotalRevenue = s.TotalRevenue,
            AgencyCommission = s.AgencyCommission,
            NetAmount = s.NetAmount,
            PublishedAt = s.PublishedAt
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets invoices received by the owner (ADR-011-R2).
    /// Returns empty list for now (invoices feature not yet implemented).
    /// </summary>
    [HttpGet("invoices")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerPortalInvoiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<OwnerPortalInvoiceDto>>> GetInvoices(
        CancellationToken cancellationToken)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var invoices = await ownerPortalService.GetOwnerInvoicesAsync(
            ownerUserId.Value,
            cancellationToken);

        var dtos = invoices.Select(i => new OwnerPortalInvoiceDto
        {
            InvoiceId = i.InvoiceId,
            InvoiceNumber = i.InvoiceNumber,
            InvoiceDate = i.InvoiceDate,
            Amount = i.Amount,
            Description = i.Description,
            IsPaid = i.IsPaid
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets withholding tax summary for the owner (ADR-011-R2).
    /// Returns empty summary for now (withholding feature not yet implemented).
    /// </summary>
    [HttpGet("withholding")]
    [ProducesResponseType(typeof(OwnerPortalWithholdingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OwnerPortalWithholdingDto>> GetWithholding(
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = User.GetOwnerUserId(_customClaimNamespace);
        if (ownerUserId == null)
        {
            logger.LogWarning("Owner portal request without valid owner_id claim");
            return Unauthorized();
        }

        var withholding = await ownerPortalService.GetOwnerWithholdingAsync(
            ownerUserId.Value,
            year,
            cancellationToken);

        var dto = new OwnerPortalWithholdingDto
        {
            Year = withholding.Year,
            TotalWithheld = withholding.TotalWithheld,
            Details = withholding.Details.Select(d => new OwnerPortalWithholdingDetailDto
            {
                PropertyId = d.PropertyId,
                PropertyName = d.PropertyName,
                Month = d.Month,
                AmountWithheld = d.AmountWithheld
            }).ToList()
        };

        return Ok(dto);
    }
}
