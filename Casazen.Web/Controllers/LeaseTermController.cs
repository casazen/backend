using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the landlord does about the end of a lease in force (LR-14): records the notice of non-renewal given by one of the parties and takes
/// it back, or ends the lease before the end of its period. A libero or concordato lease that nobody gave notice of renews by itself
/// (the hourly job <c>lease-term-renewal</c>, decision D5 of the interview); these are the only two things that stop it. Every endpoint needs
/// <c>lease.create</c> and authorizes the lease as a <see cref="HostResource"/> of its property (TN-3, AM-03): another org's lease is 404, one
/// the caller may not handle is 403. Each answers 200 with the lease detail (<see cref="LeaseDetailDto"/>), where
/// <c>currentTermEndDate</c>, <c>noticeOn</c>, <c>noticeFrom</c>, <c>terminatedOn</c> and <c>noticeWarning</c> say where the lease stands.
/// </summary>
/// <remarks>Nothing is written to a tenant by any of them: no e-mail, no message. The runbook <c>docs/runbooks/long-rent-aggregates.md</c> (§ 10) has the rules.</remarks>
[ApiController]
[Route("api/leases")]
[Authorize(Policy = CasazenPolicies.LeaseCreate)]
public class LeaseTermController(
    ILeaseTermService terms,
    ILeaseWorkflowService leaseService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private const string LeaseNotFoundCode = "lease_not_found";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Records the notice of non-renewal given by the landlord or the tenant on <c>on</c> (a day from the start of the lease up to today): the
    /// lease ends with its current period instead of renewing by itself. 200 with the detail, where <c>noticeWarning</c> is
    /// <c>notice_possibly_late</c> if it came after the last day to give it (six months before the end; CasaZen records it, it does not judge
    /// whether it is valid in law). 422 <c>lease_type_has_no_renewal</c> (a transitory lease ends by itself), <c>lease_notice_date_invalid</c>;
    /// 409 <c>lease_not_in_force</c>, <c>lease_notice_already_recorded</c>, <c>lease_already_terminated</c>, <c>lease_busy</c>.
    /// </summary>
    [HttpPost("{id:guid}/notice")]
    [ProducesResponseType(typeof(LeaseDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordNotice(Guid id, [FromBody] RecordLeaseNoticeDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        await terms.RecordNoticeAsync(id, dto.From!.Value, dto.On!.Value, cancellationToken);
        return await DetailAsync(id);
    }

    /// <summary>
    /// Takes back the notice, so the lease renews by itself again; only while the current period has not ended (409
    /// <c>lease_term_already_ended</c> after). Taking back a notice that is not there changes nothing (200).
    /// </summary>
    [HttpDelete("{id:guid}/notice")]
    [ProducesResponseType(typeof(LeaseDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveNotice(Guid id, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        await terms.RemoveNoticeAsync(id, cancellationToken);
        return await DetailAsync(id);
    }

    /// <summary>
    /// Ends the lease on <c>on</c>, its last day: after the start of the lease and no later than the end of the current period (a day in the
    /// past is allowed), and only once the lease has begun (LR-TERM): the end of the period becomes that day, the renewal stops for good and
    /// the installments of the rent plan that start after it and are not paid are cancelled (a payment made or in flight is never touched).
    /// <b>Irreversible</b>: there is no way back, so the interface asks to confirm first. 422 <c>lease_termination_date_invalid</c> with
    /// <c>min</c> and <c>max</c> (the first and last day that would do); 409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>,
    /// <c>lease_busy</c>.
    /// </summary>
    [HttpPost("{id:guid}/terminate")]
    [ProducesResponseType(typeof(LeaseDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Terminate(Guid id, [FromBody] TerminateLeaseDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        await terms.TerminateAsync(id, dto.On!.Value, cancellationToken);
        return await DetailAsync(id);
    }

    /// <summary>
    /// Records the withdrawal (recesso) the tenant sent and ends the lease on the day its notice runs out (LR-OPS-1): <c>receivedOn</c> (a day
    /// from the start of the lease up to today) plus the months of notice of the rules for serious reasons, or <c>noticeMonths</c> (1 to 12) when
    /// the contract gave the faculty. It is the early termination of <c>terminate</c> with the same rules and the same effects, and <b>irreversible</b>
    /// like it. 422 <c>lease_withdrawal_date_invalid</c>, <c>lease_withdrawal_notice_invalid</c>, <c>lease_termination_date_invalid</c> (the day the
    /// notice runs out is not inside the period, with <c>min</c> and <c>max</c>); 409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>,
    /// <c>lease_busy</c>. Nothing is written to the tenant.
    /// </summary>
    [HttpPost("{id:guid}/withdrawal")]
    [ProducesResponseType(typeof(LeaseDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordWithdrawal(Guid id, [FromBody] RecordTenantWithdrawalDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        await terms.RecordWithdrawalAsync(id, dto.ReceivedOn!.Value, dto.Reason!.Value, dto.NoticeMonths, cancellationToken);
        return await DetailAsync(id);
    }

    // ---- authorization and answer --------------------------------------------------------------------------------

    /// <summary>
    /// Authorizes <c>lease.create</c> on the property of the lease as a row of that property: <c>null</c> when allowed, otherwise the answer
    /// (404 <c>lease_not_found</c> for a lease of another org or none, 403 for a property the caller may not handle).
    /// </summary>
    private async Task<IActionResult?> AuthorizeLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await terms.FindPropertyIdAsync(leaseId, cancellationToken) is not { } propertyId)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        return await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create) ? null : Forbid();
    }

    private async Task<IActionResult> DetailAsync(Guid id)
    {
        var lease = await leaseService.GetLeaseDetailAsync(id);
        return lease is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound")
            : Ok(LeaseDtoMapper.ToDetail(lease, _clock.TodayInRome()));
    }
}

/// <summary>A notice of non-renewal: who gave it (<c>Landlord</c> or <c>Tenant</c>) and the day (<c>yyyy-MM-dd</c>, from the start of the lease up to today).</summary>
public sealed record RecordLeaseNoticeDto(
    [param: Required, EnumDataType(typeof(LeaseNoticeParty))] LeaseNoticeParty? From,
    [param: Required] DateOnly? On);

/// <summary>The last day of a lease ended early (<c>yyyy-MM-dd</c>, from the start of the lease up to the end of the period it is in).</summary>
public sealed record TerminateLeaseDto([param: Required] DateOnly? On);

/// <summary>
/// A withdrawal of the tenant (LR-OPS-1): the day it was received (<c>yyyy-MM-dd</c>, from the start of the lease up to today), why, and, only
/// when the contract gave the faculty, the months of notice it states (1 to 12; the rules' months otherwise, and always for serious reasons).
/// </summary>
public sealed record RecordTenantWithdrawalDto(
    [param: Required] DateOnly? ReceivedOn,
    [param: Required, EnumDataType(typeof(TenantWithdrawalReason))] TenantWithdrawalReason? Reason,
    int? NoticeMonths = null);
