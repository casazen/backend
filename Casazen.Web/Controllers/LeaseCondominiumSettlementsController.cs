using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The tenant's consuntivo settlement (CondoLedger phase 3). Calculating and posting do not email or message the tenant:
/// the landlord sends it by hand. Reads need <c>lease.read</c>; changes need <c>lease.create</c>.
/// </summary>
[ApiController]
[Route("api/leases/{leaseId:guid}/condominium-settlements")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LeaseCondominiumSettlementsController(
    ITenantCondominiumSettlementService settlements,
    ILeaseWorkflowService leaseService,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    private const string LeaseNotFoundCode = "lease_not_found";

    /// <summary>
    /// Drafts the settlement of a confirmed consuntivo. Positive is a debit, negative a credit, under one cent is even.
    /// The destination defaults to the next installment.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<TenantSettlementView>> Calculate(
        Guid leaseId, [FromBody] CalculateTenantSettlementDto dto, CancellationToken cancellationToken)
    {
        var (scope, userId, denied) = await AuthorizeAsync(leaseId, cancellationToken);
        if (denied is not null)
            return denied;
        return Ok(await settlements.CalculateAsync(
            scope!,
            leaseId,
            new CalculateTenantSettlementCommand(dto.PeriodId, dto.Disposition, dto.Rule, dto.Lines?.Select(line => new ExplicitSettlementLine(line.LineId, line.Amount)).ToList()),
            userId!,
            cancellationToken));
    }

    /// <summary>Confirms a draft. Nothing is posted and nothing is sent.</summary>
    [HttpPost("{settlementId:guid}/confirm")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<TenantSettlementView>> Confirm(
        Guid leaseId, Guid settlementId, CancellationToken cancellationToken)
    {
        var (scope, userId, denied) = await AuthorizeAsync(leaseId, cancellationToken);
        if (denied is not null)
            return denied;
        return Ok(await settlements.ConfirmAsync(scope!, leaseId, settlementId, userId!, cancellationToken));
    }

    /// <summary>
    /// Posts a confirmed settlement. Issued and paid rent installments keep their rent and advance. The same settlement
    /// posted again is the same component. The tenant is not notified.
    /// </summary>
    [HttpPost("{settlementId:guid}/post")]
    [Authorize(Policy = CasazenPolicies.LeaseCreate)]
    public async Task<ActionResult<TenantSettlementView>> Post(
        Guid leaseId, Guid settlementId, CancellationToken cancellationToken)
    {
        var (scope, userId, denied) = await AuthorizeAsync(leaseId, cancellationToken);
        if (denied is not null)
            return denied;
        return Ok(await settlements.PostAsync(scope!, leaseId, settlementId, userId!, cancellationToken));
    }

    private async Task<(HostScope? Scope, string? UserId, ActionResult? Denied)> AuthorizeAsync(
        Guid leaseId, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(leaseId) is { } denied)
            return (null, null, denied);
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || User.GetUserId() is not { } userId)
            return (null, null, Unauthorized());
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return scope is null ? (null, null, Unauthorized()) : (scope, userId, null);
    }

    private async Task<ActionResult?> AuthorizeLeaseAsync(Guid leaseId)
    {
        var lease = await leaseService.GetLeaseDetailAsync(leaseId);
        if (lease is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");
        if (lease.Property is null)
            return Forbid();

        var resource = HostResource.ForProperty(lease.Property) with { OrgId = lease.OrgId };
        return await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create) ? null : Forbid();
    }
}

public sealed class CalculateTenantSettlementDto
{
    public Guid PeriodId { get; set; }

    public TenantSettlementDisposition? Disposition { get; set; }

    public TenantAllocationRule? Rule { get; set; }

    public List<ExplicitSettlementLineDto>? Lines { get; set; }
}

public sealed class ExplicitSettlementLineDto
{
    public Guid LineId { get; set; }

    public decimal Amount { get; set; }
}
