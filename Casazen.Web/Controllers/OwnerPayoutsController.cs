using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The payouts to the owners with their withholding evidence (E06/F05/US02/T03, NF-110, ADR-018 amended): confirming a payout is the only operation that
/// decides a withholding, on the approved rule in force on the payment day; a retry with the same key answers the same payout. Reading needs
/// <c>agency.statement.view</c>; confirming and reversing <c>agency.owner.financial.manage</c>. Like the rest of the owner accounting it needs an
/// org-wide reach (403 for a caller restricted to some properties).
/// </summary>
[ApiController]
[Route("api/owner-accounting/payouts")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
public class OwnerPayoutsController(
    IOwnerPayoutService payouts,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The payouts of the org, or of one owner, the newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<OwnerPayoutDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OwnerPayoutDto>>> List([FromQuery] Guid? ownerId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok((await payouts.ListPayoutsAsync(orgId, ownerId, HttpContext.RequestAborted)).Select(p => OwnerPayoutDto.From(p, [])).ToList());
    }

    /// <summary>A payout with its evidence. 404 <c>owner_payout_not_found</c>.</summary>
    [HttpGet("{payoutId:guid}")]
    [ProducesResponseType(typeof(OwnerPayoutDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerPayoutDto>> Get(Guid payoutId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var result = await payouts.GetPayoutAsync(orgId, payoutId, HttpContext.RequestAborted);
        return Ok(OwnerPayoutDto.From(result.Payout, result.Evidence));
    }

    /// <summary>
    /// Confirms a payout (idempotent on <c>idempotencyKey</c>: 200 with the same payout on a retry, 201 the first time). 422
    /// <c>tax_applicability_unresolved</c> (with the <c>field</c>), <c>owner_fiscal_*</c>, <c>owner_payout_invalid</c>; 409
    /// <c>owner_payout_key_reused</c>, <c>owner_period_closed</c>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerPayoutDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OwnerPayoutDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerPayoutDto>> Confirm([FromBody] OwnerPayoutCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var result = await payouts.ConfirmPayoutAsync(orgId, command, User.GetUserId(), HttpContext.RequestAborted);
        var dto = OwnerPayoutDto.From(result.Payout, result.Evidence);
        return result.Replayed ? Ok(dto) : CreatedAtAction(nameof(Get), new { payoutId = result.Payout.Id }, dto);
    }

    /// <summary>Reverses a payout with its reason (<c>{ reason }</c>): the evidence and the ledger lines get their reversals. 409 <c>owner_payout_reversed</c>.</summary>
    [HttpPost("{payoutId:guid}/reverse")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerPayoutDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerPayoutDto>> Reverse(Guid payoutId, [FromBody] OwnerPayoutReversal request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var result = await payouts.ReversePayoutAsync(orgId, payoutId, request.Reason, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(OwnerPayoutDto.From(result.Payout, result.Evidence));
    }

    private async Task<Guid?> OrgAsync()
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null)
            return null;
        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct);
        return scope is { IsOrgWide: true } ? orgId : null;
    }
}

/// <summary>The reversal of a payout, with its reason.</summary>
public sealed record OwnerPayoutReversal(string? Reason);

/// <summary>A withholding decision as the API gives it.</summary>
public sealed record TaxObligationEvidenceDto(
    Guid Id,
    TaxDecision Decision,
    string ReasonCode,
    string Jurisdiction,
    string RuleCode,
    string RuleVersion,
    decimal? GrossBase,
    decimal? RatePercent,
    string? Rounding,
    decimal? Amount,
    string? TaxCode,
    DateOnly PaidOn,
    int PeriodYear,
    int PeriodMonth,
    Guid? ReversesEvidenceId,
    DateTime CreatedAt)
{
    public static TaxObligationEvidenceDto From(TaxObligationEvidence e) => new(
        e.Id, e.Decision, e.ReasonCode, e.Jurisdiction, e.RuleCode, e.RuleVersion, e.GrossBase, e.RatePercent, e.Rounding, e.Amount, e.TaxCode, e.PaidOn,
        e.PeriodYear, e.PeriodMonth, e.ReversesEvidenceId, e.CreatedAt);
}

/// <summary>A payout with its evidence.</summary>
public sealed record OwnerPayoutDto(
    Guid Id,
    Guid OwnerId,
    Guid StatementVersionId,
    string IdempotencyKey,
    string? Reference,
    DateOnly PaidOn,
    string Currency,
    decimal GrossAmount,
    decimal WithholdingAmount,
    decimal NetAmount,
    bool? AgencyCollected,
    bool? QualifyingShortLet,
    OwnerPayoutStatus Status,
    DateTime ConfirmedAt,
    DateTime? ReversedAt,
    string? ReversalReason,
    IReadOnlyList<TaxObligationEvidenceDto> Evidence)
{
    public static OwnerPayoutDto From(OwnerPayout p, IReadOnlyList<TaxObligationEvidence> evidence) => new(
        p.Id, p.PropertyOwnerId, p.OwnerStatementVersionId, p.IdempotencyKey, p.Reference, p.PaidOn, p.Currency, p.GrossAmount, p.WithholdingAmount,
        p.NetAmount, p.AgencyCollected, p.QualifyingShortLet, p.Status, p.ConfirmedAt, p.ReversedAt, p.ReversalReason,
        evidence.Select(TaxObligationEvidenceDto.From).ToList());
}
