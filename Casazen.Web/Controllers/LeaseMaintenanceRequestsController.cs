using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Maintenance the tenant of a lease asks for. The tenant (a verified email equal to a tenant party of the lease)
/// opens a request and reads the decision. The landlord or the agency of the same org, with <c>lease.create</c> on
/// the lease's property, approves or rejects it and pays it. Payment starts the euro bank-link debit and does not
/// mark the request paid: Stripe does, when the PaymentIntent succeeds. Another org's lease is 404.
/// </summary>
[ApiController]
[Route("api/leases/{leaseId:guid}/maintenance-requests")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class LeaseMaintenanceRequestsController(
    ILeaseMaintenanceService maintenance,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IAccountEmailResolver emails) : ControllerBase
{
    /// <summary>Opens a request. Status is <see cref="LeaseMaintenanceStatus.InAttesa"/>. Photos are optional.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(LeaseMaintenanceRequestView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseMaintenanceRequestView>> Create(
        Guid leaseId, [FromBody] CreateLeaseMaintenanceRequestDto body, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (await CallerAsync(leaseId, cancellationToken) is not { } caller)
            return MissingCaller();
        if (caller.Missing is { } missing)
            return missing;

        var photos = new List<LeaseMaintenancePhotoContent>();
        foreach (var photo in body.Photos ?? [])
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(photo.ContentBase64);
            }
            catch (FormatException)
            {
                return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "MaintenancePhotoInvalid");
            }

            photos.Add(new LeaseMaintenancePhotoContent(photo.FileName, bytes));
        }

        var created = await maintenance.CreateAsync(
            caller.Caller!,
            leaseId,
            new CreateLeaseMaintenanceCommand(body.Title, body.Description, body.Urgency!.Value, photos),
            cancellationToken);
        return Created($"/api/leases/{leaseId}/maintenance-requests/{created.Id}", created);
    }

    /// <summary>The requests of this lease the caller may see: the tenant, or anyone who may read the lease.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(LeaseMaintenanceListView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseMaintenanceListView>> List(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(leaseId, cancellationToken) is not { } caller)
            return MissingCaller();
        if (caller.Missing is { } missing)
            return missing;
        return Ok(await maintenance.ListAsync(caller.Caller!, leaseId, cancellationToken));
    }

    /// <summary>
    /// Approves or rejects. <c>decision</c> is <c>Approved</c> or <c>Rejected</c>. A rejection needs <c>reason</c>.
    /// Approval does not mark the request paid.
    /// </summary>
    [HttpPost("{id:guid}/decision")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [ProducesResponseType(typeof(LeaseMaintenanceRequestView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseMaintenanceRequestView>> Decide(
        Guid leaseId, Guid id, [FromBody] LeaseMaintenanceDecisionDto body, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (await CallerAsync(leaseId, cancellationToken) is not { } caller)
            return MissingCaller();
        if (caller.Missing is { } missing)
            return missing;
        return Ok(await maintenance.DecideAsync(
            caller.Caller!, leaseId, id, new LeaseMaintenanceDecisionCommand(body.Decision!.Value, body.Reason), cancellationToken));
    }

    /// <summary>
    /// Starts the euro debit of an approved request from the org's linked account. A request still
    /// <see cref="LeaseMaintenanceStatus.InAttesa"/> or <see cref="LeaseMaintenanceStatus.Rifiutata"/> cannot be paid.
    /// A second call returns the debit already started and does not create another.
    /// </summary>
    [HttpPost("{id:guid}/payment")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    [ProducesResponseType(typeof(LeaseMaintenancePaymentView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseMaintenancePaymentView>> Pay(
        Guid leaseId, Guid id, [FromBody] LeaseMaintenancePaymentDto body, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (await CallerAsync(leaseId, cancellationToken) is not { } caller)
            return MissingCaller();
        if (caller.Missing is { } missing)
            return missing;
        var paid = await maintenance.PayAsync(
            caller.Caller!,
            leaseId,
            id,
            new LeaseMaintenancePaymentCommand(body.AmountCents),
            new BankLinkCaller(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString()),
            cancellationToken);
        return Ok(paid);
    }

    private ActionResult MissingCaller() => Unauthorized();

    private async Task<CallerResolution?> CallerAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var userId = User.GetUserId();
        if (orgId is null || userId is null)
            return null;

        var access = await maintenance.FindLeaseAsync(orgId.Value, leaseId, cancellationToken);
        if (access is null)
            return new CallerResolution(null, this.ApiProblem(StatusCodes.Status404NotFound, "lease_not_found", "LeaseNotFound"));

        var resource = new HostResource(access.OrgId, access.PropertyOwnerId, access.PropertyId);
        var canRead = await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Read);
        var canManage = await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create);
        var (email, verified) = await emails.ResolveAsync(User, userId, cancellationToken);
        return new CallerResolution(
            new LeaseMaintenanceCaller(orgId.Value, userId, email, verified, canRead, canManage),
            null);
    }

    private sealed record CallerResolution(LeaseMaintenanceCaller? Caller, ActionResult? Missing);
}

/// <summary>What the tenant sends. <c>urgency</c> is <c>Normal</c>, <c>High</c> or <c>Emergency</c>.</summary>
public sealed class CreateLeaseMaintenanceRequestDto
{
    [Required(ErrorMessage = "MaintenanceTitleInvalid")]
    [MaxLength(LeaseMaintenanceRequestLimits.Title, ErrorMessage = "MaintenanceTitleInvalid")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "MaintenanceDescriptionInvalid")]
    [MaxLength(LeaseMaintenanceRequestLimits.Description, ErrorMessage = "MaintenanceDescriptionInvalid")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "MaintenanceUrgencyInvalid")]
    public LeaseMaintenanceUrgency? Urgency { get; set; }

    public List<LeaseMaintenancePhotoDto>? Photos { get; set; }
}

/// <summary>One photo, as base64 of a JPEG, PNG or WebP. Omitted when the tenant attaches none.</summary>
public sealed class LeaseMaintenancePhotoDto
{
    [Required(ErrorMessage = "MaintenancePhotoInvalid")]
    [MaxLength(120, ErrorMessage = "MaintenancePhotoInvalid")]
    public string FileName { get; set; } = string.Empty;

    [Required(ErrorMessage = "MaintenancePhotoInvalid")]
    public string ContentBase64 { get; set; } = string.Empty;
}

/// <summary><c>decision</c> is <c>Approved</c> or <c>Rejected</c>. <c>reason</c> is required when rejecting.</summary>
public sealed class LeaseMaintenanceDecisionDto
{
    [Required(ErrorMessage = "MaintenanceDecisionInvalid")]
    public LeaseMaintenanceDecision? Decision { get; set; }

    [MaxLength(LeaseMaintenanceRequestLimits.Reason, ErrorMessage = "MaintenanceReasonInvalid")]
    public string? Reason { get; set; }
}

/// <summary>The euros to debit. A retry of a debit already started does not charge this amount again.</summary>
public sealed class LeaseMaintenancePaymentDto
{
    [Range(1, 100_000_000, ErrorMessage = "BankLinkAmountInvalid")]
    public long AmountCents { get; set; }
}

/// <summary>Lengths shared with the entity, so the DTO does not take a dependency on the column type.</summary>
public static class LeaseMaintenanceRequestLimits
{
    public const int Title = 120;
    public const int Description = 4000;
    public const int Reason = 500;
}
