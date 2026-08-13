using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/service-requests")]
[Authorize]
public class ServiceRequestsController(
    IServiceRequestService serviceRequestService,
    ISupplierMatchService supplierMatchService,
    IOrgContextResolver orgContextResolver,
    ISupplierOrgContextResolver supplierOrgContextResolver) : ControllerBase
{
    [HttpPost("match-supplier")]
    [ProducesResponseType(typeof(SupplierMatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupplierMatchResponse>> MatchSupplier(
        [FromBody] MatchSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var userId = GetUserId();
        if (orgId is null || userId is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Category))
            return BadRequest(new { error = "Categoria obbligatoria." });

        try
        {
            var result = await supplierMatchService.MatchAsync(
                orgId.Value,
                userId,
                request.PropertyId,
                request.Category.Trim(),
                request.Urgency,
                request.Notes,
                cancellationToken);
            return Ok(MapMatchResult(result));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Create(
        [FromBody] CreateServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null) return Unauthorized();

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        try
        {
            var created = await serviceRequestService.CreateAsync(
                new CreateServiceRequestCommand(
                    orgId.Value,
                    userId,
                    request.PropertyId,
                    request.BookingId,
                    request.SupplierOrgId,
                    request.Category,
                    request.Urgency,
                    request.Notes,
                    request.ChargeToGuest),
                cancellationToken);

            return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapDto(created));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpGet]
    [ProducesResponseType(typeof(ServiceRequestListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ServiceRequestListResponse>> List(
        [FromQuery] string? status,
        [FromQuery] Guid? propertyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? view = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        ServiceRequestStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ServiceRequestStatus>(status, true, out var parsed))
            statusFilter = parsed;

        if (string.Equals(view, "supplier", StringComparison.OrdinalIgnoreCase) && User.IsInRole("Supplier"))
        {
            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is null) return NotFound();

            var openOnly = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
            var (items, total) = await serviceRequestService.ListForSupplierAsync(
                supplierOrgId.Value, openOnly, page, pageSize, cancellationToken);

            return Ok(new ServiceRequestListResponse
            {
                Items = items.Select(MapDto),
                Total = total,
                Page = page,
                PageSize = pageSize,
            });
        }

        var hostOrgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (hostOrgId is null) return Unauthorized();

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var (hostItems, hostTotal) = await serviceRequestService.ListForHostAsync(
            hostOrgId.Value, userId, GetUserRoles(), statusFilter, propertyId, page, pageSize, cancellationToken);

        return Ok(new ServiceRequestListResponse
        {
            Items = hostItems.Select(MapDto),
            Total = hostTotal,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ServiceRequestDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Supplier"))
        {
            var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
            if (supplierOrgId is not null)
            {
                var supplierRequest = await serviceRequestService.GetByIdForSupplierAsync(id, supplierOrgId.Value, cancellationToken);
                if (supplierRequest is not null)
                    return Ok(MapDto(supplierRequest));
            }
        }

        var hostOrgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (hostOrgId is null) return Unauthorized();

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var hostRequest = await serviceRequestService.GetByIdForHostAsync(
            id, hostOrgId.Value, userId, GetUserRoles(), cancellationToken);
        if (hostRequest is null) return NotFound();

        return Ok(MapDto(hostRequest));
    }

    [HttpPost("{id:guid}/take")]
    [Authorize(Policy = "RequireSupplier")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Take(Guid id, CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        var userId = GetUserId();
        if (supplierOrgId is null || userId is null) return NotFound();

        try
        {
            var updated = await serviceRequestService.TakeAsync(id, supplierOrgId.Value, userId, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = "RequireSupplier")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Complete(
        Guid id,
        [FromBody] CompleteServiceRequestRequest? request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        try
        {
            var updated = await serviceRequestService.CompleteAsync(
                id, supplierOrgId.Value, request?.Notes, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "RequireSupplier")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> Reject(
        Guid id,
        [FromBody] RejectServiceRequestRequest request,
        CancellationToken cancellationToken)
    {
        var supplierOrgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (supplierOrgId is null) return NotFound();

        try
        {
            var updated = await serviceRequestService.RejectAsync(
                id, supplierOrgId.Value, request.Reason, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("{id:guid}/mark-paid")]
    [ProducesResponseType(typeof(ServiceRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ServiceRequestDto>> MarkPaid(Guid id, CancellationToken cancellationToken)
    {
        var hostOrgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var userId = GetUserId();
        if (hostOrgId is null || userId is null) return Unauthorized();

        try
        {
            var updated = await serviceRequestService.MarkPaidAsync(id, hostOrgId.Value, userId, cancellationToken);
            return Ok(MapDto(updated));
        }
        catch (ServiceRequestStateException ex)
        {
            return Conflict(new ProblemDetails { Title = "Conflitto", Detail = ex.Message, Status = 409 });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private string? GetUserId() =>
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");

    private IReadOnlyList<string> GetUserRoles() =>
        User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

    internal static ServiceRequestDto MapDto(ServiceRequest r) => new()
    {
        Id = r.Id,
        OrgId = r.OrgId,
        BookingId = r.BookingId,
        PropertyId = r.PropertyId,
        PropertyName = r.Property?.Name,
        SupplierOrgId = r.SupplierOrgId,
        SupplierName = r.SupplierOrg?.DisplayName ?? r.SupplierOrg?.Name,
        Category = r.Category,
        Urgency = r.Urgency.ToString(),
        Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
        Status = r.Status.ToString(),
        TakenAt = r.TakenAt,
        TakenByUserId = r.TakenByUserId,
        CompletedAt = r.CompletedAt,
        PaidAt = r.PaidAt,
        ChargeToGuest = r.ChargeToGuest,
        RejectionReason = r.RejectionReason,
        CreatedAt = r.CreatedAt,
        UpdatedAt = r.UpdatedAt,
    };

    internal static ServiceRequestSummaryDto MapSummary(ServiceRequest r) => new()
    {
        Id = r.Id,
        PropertyId = r.PropertyId,
        PropertyName = r.Property?.Name ?? string.Empty,
        Category = r.Category,
        Urgency = r.Urgency.ToString(),
        Status = r.Status.ToString(),
        Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
        CreatedAt = r.CreatedAt,
    };

    private static SupplierMatchResponse MapMatchResult(SupplierMatchResult result) => new()
    {
        Recommended = result.Recommended is null ? null : MapCandidate(result.Recommended),
        Alternatives = result.Alternatives.Select(MapCandidate),
        ExternalSuggestions = result.ExternalSuggestions.Select(e => new ExternalSupplierSuggestionDto
        {
            Name = e.Name,
            Address = e.Address,
            Phone = e.Phone,
            Email = e.Email,
            Rating = e.Rating,
            ReviewCount = e.ReviewCount,
            GoogleMapsUrl = e.GoogleMapsUrl,
            WebsiteUrl = e.WebsiteUrl,
            Source = e.Source,
        }),
        UsedExternalFallback = result.UsedExternalFallback,
    };

    private static SupplierMatchCandidateDto MapCandidate(SupplierMatchCandidate c) => new()
    {
        OrgId = c.OrgId,
        LegalName = c.LegalName,
        Phone = c.Phone,
        Email = c.Email,
        Bio = c.Bio,
        MatchScore = c.MatchScore,
        MatchReason = c.MatchReason,
        Source = c.Source,
    };
}
