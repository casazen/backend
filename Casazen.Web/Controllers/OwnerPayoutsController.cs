using System.Text.Json;
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
/// org-wide reach (403 for a caller restricted to some properties). The facts of the withholding (the gross, the collection, the qualifying stays) are read
/// from the ledger and never sent (E18/F01/US01/T01): the dialog previews them (<c>GET …/preview</c>) and the evidence carries them.
/// </summary>
[ApiController]
[Route("api/owner-accounting/payouts")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
public class OwnerPayoutsController(
    IOwnerPayoutService payouts,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// What a payout of the version paid on <c>paidOn</c> would be, read only: the facts of the ledger (each stay, whether it is in the base and why), the balance
    /// still unpaid, the decision of the approved rule in force that day with the withholding in euro and in the currency of the payout, or the code that would refuse
    /// the confirmation (<c>blockingCode</c>, <c>blockingField</c>). 404 <c>owner_statement_version_not_found</c>; 422 <c>owner_payout_invalid</c>,
    /// <c>owner_statement_not_approved</c>.
    /// </summary>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(OwnerPayoutPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerPayoutPreviewDto>> Preview([FromQuery] Guid statementVersionId, [FromQuery] DateOnly paidOn)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var preview = await payouts.PreviewPayoutAsync(orgId, statementVersionId, paidOn, HttpContext.RequestAborted);
        Response.Headers.CacheControl = "no-store";
        return Ok(OwnerPayoutPreviewDto.From(preview));
    }

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
    /// Confirms a payout (idempotent on <c>idempotencyKey</c>: 200 with the same payout on a retry, 201 the first time) of <c>statementVersionId</c> on <c>paidOn</c>,
    /// with its <c>reference</c>; <c>grossAmount</c> is optional (the balance of the version still unpaid by default, never more: 422
    /// <c>owner_payout_exceeds_balance</c> with the <c>balance</c>). The facts are the ledger's: a body that sends them has them ignored. 422
    /// <c>tax_applicability_unresolved</c> (with the <c>field</c>), <c>owner_fiscal_*</c> (a rule set that is not approved decides nothing),
    /// <c>owner_payout_invalid</c>; 409 <c>owner_payout_key_reused</c>, <c>owner_period_closed</c>.
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

/// <summary>
/// A withholding decision as the API gives it: the base and the amount in euro (the currency of the F24), and the facts of the ledger it was taken on
/// (<see cref="Facts"/>: each stay and why it is or is not in the base; <c>null</c> for a decision of before E18/F01/US01, whose facts were typed).
/// </summary>
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
    DateTime CreatedAt,
    PayoutFacts? Facts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TaxObligationEvidenceDto From(TaxObligationEvidence e) => new(
        e.Id, e.Decision, e.ReasonCode, e.Jurisdiction, e.RuleCode, e.RuleVersion, e.GrossBase, e.RatePercent, e.Rounding, e.Amount, e.TaxCode, e.PaidOn,
        e.PeriodYear, e.PeriodMonth, e.ReversesEvidenceId, e.CreatedAt, FactsOf(e.FactsJson));

    /// <summary>The facts of the ledger a decision was taken on; <c>null</c> for the facts typed before E18/F01/US01 (no <c>source</c>) or a JSON that cannot be read.</summary>
    public static PayoutFacts? FactsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || source.GetString() != PayoutFactRules.FromLedger)
            {
                return null;
            }

            return document.RootElement.Deserialize<PayoutFacts>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a payout of a version would be (<c>GET api/owner-accounting/payouts/preview</c>): the facts of the ledger, the balance still unpaid (the gross of a payout that
/// names none) and the currency of the version; the decision of the approved rule (its base, rate, rounding and amount in euro, its tax code), the withholding kept
/// in the currency of the payout and the net of a payout of the whole balance; or <see cref="BlockingCode"/> (and <see cref="BlockingField"/>), the code the
/// confirmation would answer, and no decision.
/// </summary>
public sealed record OwnerPayoutPreviewDto(
    PayoutFacts Facts,
    decimal Balance,
    string Currency,
    TaxDecision? Decision,
    string? ReasonCode,
    decimal? GrossBase,
    decimal? RatePercent,
    string? Rounding,
    decimal? WithholdingInEuro,
    string? TaxCode,
    decimal? WithholdingAmount,
    decimal? NetAmount,
    string? BlockingCode,
    string? BlockingField)
{
    public static OwnerPayoutPreviewDto From(OwnerPayoutPreview p) => new(
        p.Facts,
        p.Balance,
        p.Currency,
        p.Decision?.Decision,
        p.Decision?.Reason,
        p.Decision?.GrossBase,
        p.Decision?.RatePercent,
        p.Decision?.Rounding,
        p.Decision?.Amount,
        p.Decision?.TaxCode,
        p.WithholdingAmount,
        p.WithholdingAmount is { } withheld && p.BlockingCode is null ? p.Balance - withheld : null,
        p.BlockingCode,
        p.BlockingField);
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
