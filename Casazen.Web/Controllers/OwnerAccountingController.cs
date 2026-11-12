using System.Globalization;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The owner ledger and the month-end close of an agency (E06/F05/US02/T02, NF-25, NF-110): posting a month, its readiness, the statement versions with
/// their lines, the approval (<c>agency.statement.manage</c>) and the publication (<c>agency.statement.publish</c>), the close reconciled to the paid wires
/// (a waiver needs <c>agency.owner.financial.manage</c>), the legal hold, the evidence, the charges, the exchange rates and the manual corrections.
/// </summary>
/// <remarks>
/// The owner accounting is a whole-org function: a caller restricted to some properties (a collaborator «Solo alcuni», an account outside the org team) is
/// refused (403) even with the permission, since an owner's ledger spans properties it does not reach.
/// </remarks>
[ApiController]
[Route("api/owner-accounting")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
public class OwnerAccountingController(
    IOwnerAccountingService accounting,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>Posts the ledger lines of the month from its sources (idempotent). 422 <c>owner_period_invalid</c>.</summary>
    [HttpPost("periods/{year:int}/{month:int}/post")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerPostingResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerPostingResult>> Post(int year, int month)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(await accounting.PostPeriodAsync(orgId, year, month, User.GetUserId(), HttpContext.RequestAborted));
    }

    /// <summary>The month-end readiness of the org and of each owner.</summary>
    [HttpGet("periods/{year:int}/{month:int}/readiness")]
    [ProducesResponseType(typeof(OwnerCloseReadiness), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerCloseReadiness>> Readiness(int year, int month)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(await accounting.GetReadinessAsync(orgId, year, month, HttpContext.RequestAborted));
    }

    /// <summary>A new version of the owner's statement from the ledger now. 409 <c>owner_period_closed</c>, <c>owner_statement_unchanged</c>.</summary>
    [HttpPost("periods/{year:int}/{month:int}/owners/{ownerId:guid}/versions")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerStatementVersionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerStatementVersionDto>> Generate(int year, int month, Guid ownerId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var version = await accounting.GenerateVersionAsync(orgId, ownerId, year, month, User.GetUserId(), HttpContext.RequestAborted);
        return CreatedAtAction(nameof(Version), new { versionId = version.Id }, OwnerStatementVersionDto.From(version));
    }

    /// <summary>The versions of the owner's month, the newest first.</summary>
    [HttpGet("periods/{year:int}/{month:int}/owners/{ownerId:guid}/versions")]
    [ProducesResponseType(typeof(List<OwnerStatementVersionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<OwnerStatementVersionDto>>> Versions(int year, int month, Guid ownerId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var versions = await accounting.ListVersionsAsync(orgId, ownerId, year, month, HttpContext.RequestAborted);
        return Ok(versions.Select(OwnerStatementVersionDto.From).ToList());
    }

    /// <summary>A version with the ledger lines it is made of, each with its source.</summary>
    [HttpGet("versions/{versionId:guid}")]
    [ProducesResponseType(typeof(OwnerStatementVersionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerStatementVersionDetailDto>> Version(Guid versionId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var detail = await accounting.GetVersionAsync(orgId, versionId, HttpContext.RequestAborted);
        return Ok(new OwnerStatementVersionDetailDto(OwnerStatementVersionDto.From(detail.Version), detail.Entries.Select(OwnerLedgerEntryDto.From).ToList()));
    }

    /// <summary>Approves a draft version. 422 <c>owner_statement_flagged</c>; 409 <c>owner_statement_not_draft</c>, <c>owner_period_dirty</c>.</summary>
    [HttpPost("versions/{versionId:guid}/approve")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerStatementVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerStatementVersionDto>> Approve(Guid versionId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(OwnerStatementVersionDto.From(await accounting.ApproveVersionAsync(orgId, versionId, User.GetUserId(), HttpContext.RequestAborted)));
    }

    /// <summary>Publishes an approved version: its file and hash are stored for good. 422 <c>owner_statement_not_approved</c>; 409 <c>owner_period_dirty</c>.</summary>
    [HttpPost("versions/{versionId:guid}/publish")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementPublish)]
    [ProducesResponseType(typeof(OwnerStatementVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerStatementVersionDto>> Publish(Guid versionId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(OwnerStatementVersionDto.From(await accounting.PublishVersionAsync(orgId, versionId, User.GetUserId(), HttpContext.RequestAborted)));
    }

    /// <summary>The published file of a version (JSON), as the owner received it, also after it was superseded. 404 <c>owner_statement_not_published</c>.</summary>
    [HttpGet("versions/{versionId:guid}/published")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Published(Guid versionId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var version = (await accounting.GetVersionAsync(orgId, versionId, HttpContext.RequestAborted)).Version;
        if (version.PublishedSnapshot is null)
        {
            throw new NotFoundException($"Owner statement version {versionId} is not published")
            {
                Code = OwnerAccountingErrors.VersionNotPublished,
                MessageKey = "OwnerStatementNotPublished",
            };
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"rendiconto-{version.PeriodYear:D4}-{version.PeriodMonth:D2}-v{version.VersionNumber}-{version.Id:N}.json");
        return File(Encoding.UTF8.GetBytes(version.PublishedSnapshot), "application/json", name);
    }

    /// <summary>
    /// Closes the owner's month on its approved version, reconciled to the paid wires; a difference or a negative balance needs <c>waiverReason</c> and
    /// <c>agency.owner.financial.manage</c>. 422 <c>owner_close_unreconciled</c> (with <c>due</c>, <c>paid</c>, <c>difference</c>),
    /// <c>owner_statement_not_approved</c>; 403 <c>owner_close_waiver_forbidden</c>; 409 <c>owner_period_dirty</c>, <c>owner_period_closed</c>.
    /// </summary>
    [HttpPost("periods/{year:int}/{month:int}/owners/{ownerId:guid}/close")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerClosePeriodDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerClosePeriodDto>> Close(int year, int month, Guid ownerId, [FromBody] OwnerCloseCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var mayWaive = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.AgencyOwnerFinancialManage)).Succeeded;
        var row = await accounting.CloseAsync(orgId, ownerId, year, month, command, mayWaive, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(OwnerClosePeriodDto.From(row));
    }

    /// <summary>Sets or lifts the legal hold of the owner's month (<c>{ hold, reason }</c>). 422 <c>owner_legal_hold_reason_required</c>.</summary>
    [HttpPost("periods/{year:int}/{month:int}/owners/{ownerId:guid}/legal-hold")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerClosePeriodDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerClosePeriodDto>> LegalHold(int year, int month, Guid ownerId, [FromBody] OwnerLegalHoldRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var row = await accounting.SetLegalHoldAsync(orgId, ownerId, year, month, request.Hold, request.Reason, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(OwnerClosePeriodDto.From(row));
    }

    /// <summary>The evidence of the owner's month: the close, every version with its published file, the lines and the charges.</summary>
    [HttpGet("periods/{year:int}/{month:int}/owners/{ownerId:guid}/evidence")]
    [ProducesResponseType(typeof(OwnerAccountingEvidenceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerAccountingEvidenceDto>> Evidence(int year, int month, Guid ownerId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var evidence = await accounting.ExportEvidenceAsync(orgId, ownerId, year, month, HttpContext.RequestAborted);
        return Ok(OwnerAccountingEvidenceDto.From(evidence));
    }

    /// <summary>The charges of the org, or of one property.</summary>
    [HttpGet("charges")]
    [ProducesResponseType(typeof(List<OwnerChargeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OwnerChargeDto>>> Charges([FromQuery] Guid? propertyId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok((await accounting.ListChargesAsync(orgId, propertyId, HttpContext.RequestAborted)).Select(OwnerChargeDto.From).ToList());
    }

    /// <summary>A charge or an expense, pending approval. 422 <c>owner_charge_invalid</c> (with the <c>field</c>); 409 <c>owner_charge_duplicate</c>.</summary>
    [HttpPost("charges")]
    [Authorize(Policy = CasazenPolicies.AgencyStatementManage)]
    [ProducesResponseType(typeof(OwnerChargeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerChargeDto>> CreateCharge([FromBody] OwnerChargeCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var charge = await accounting.CreateChargeAsync(orgId, command, User.GetUserId(), HttpContext.RequestAborted);
        return CreatedAtAction(nameof(Charges), new { propertyId = charge.PropertyId }, OwnerChargeDto.From(charge));
    }

    /// <summary>Approves a pending charge. 409 <c>owner_charge_not_pending</c>.</summary>
    [HttpPost("charges/{chargeId:guid}/approve")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerChargeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerChargeDto>> ApproveCharge(Guid chargeId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(OwnerChargeDto.From(await accounting.ApproveChargeAsync(orgId, chargeId, User.GetUserId(), HttpContext.RequestAborted)));
    }

    /// <summary>Cancels a charge and reverses what it posted in the open months. 409 <c>owner_charge_not_pending</c> when already cancelled.</summary>
    [HttpPost("charges/{chargeId:guid}/cancel")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerChargeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerChargeDto>> CancelCharge(Guid chargeId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(OwnerChargeDto.From(await accounting.CancelChargeAsync(orgId, chargeId, User.GetUserId(), HttpContext.RequestAborted)));
    }

    /// <summary>The exchange rates of the owner accounting.</summary>
    [HttpGet("fx-rates")]
    [ProducesResponseType(typeof(List<OwnerFxRateDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<OwnerFxRateDto>>> FxRates()
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok((await accounting.ListFxRatesAsync(orgId, HttpContext.RequestAborted)).Select(OwnerFxRateDto.From).ToList());
    }

    /// <summary>An exchange rate with its source. 422 <c>owner_fx_rate_invalid</c> (with the <c>field</c>); 409 <c>owner_fx_rate_exists</c>.</summary>
    [HttpPost("fx-rates")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerFxRateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerFxRateDto>> AddFxRate([FromBody] OwnerFxRateCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var rate = await accounting.AddFxRateAsync(orgId, command, User.GetUserId(), HttpContext.RequestAborted);
        return CreatedAtAction(nameof(FxRates), new { }, OwnerFxRateDto.From(rate));
    }

    /// <summary>
    /// A manual correction with its reason (<c>description</c>); <c>replacesEntryId</c> reverses an earlier manual correction first. 422
    /// <c>owner_correction_invalid</c> (with the <c>field</c>); 409 <c>owner_ledger_entry_already_reversed</c>, <c>owner_period_closed</c>.
    /// </summary>
    [HttpPost("corrections")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerLedgerEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerLedgerEntryDto>> Correct([FromBody] OwnerCorrectionCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var entry = await accounting.PostCorrectionAsync(orgId, command, User.GetUserId(), HttpContext.RequestAborted);
        return StatusCode(StatusCodes.Status201Created, OwnerLedgerEntryDto.From(entry));
    }

    /// <summary>The links of a version to the invoices of the agency's external fiscal system, the removed ones included (E06/F01/US02/T05).</summary>
    [HttpGet("versions/{versionId:guid}/external-documents")]
    [ProducesResponseType(typeof(List<OwnerExternalDocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<OwnerExternalDocumentDto>>> ExternalDocuments(Guid versionId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var references = await accounting.ListExternalDocumentsAsync(orgId, versionId, HttpContext.RequestAborted);
        return Ok(references.Select(OwnerExternalDocumentDto.From).ToList());
    }

    /// <summary>
    /// Links an approved or published version to the invoice the agency's external system issued: CasaZen keeps the link, never an amount, a number or a
    /// submission state of its own. 422 <c>owner_external_document_invalid</c> (with the <c>field</c>), <c>owner_statement_not_approved</c>; 409
    /// <c>owner_external_document_duplicate</c>.
    /// </summary>
    [HttpPost("versions/{versionId:guid}/external-documents")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerExternalDocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerExternalDocumentDto>> AddExternalDocument(Guid versionId, [FromBody] OwnerExternalDocumentCommand command)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var reference = await accounting.AddExternalDocumentAsync(orgId, versionId, command, User.GetUserId(), HttpContext.RequestAborted);
        return CreatedAtAction(nameof(ExternalDocuments), new { versionId }, OwnerExternalDocumentDto.From(reference));
    }

    /// <summary>Records that the link was checked against the external system. 409 <c>owner_external_document_removed</c>.</summary>
    [HttpPost("external-documents/{referenceId:guid}/verify")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerExternalDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerExternalDocumentDto>> VerifyExternalDocument(Guid referenceId)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        return Ok(OwnerExternalDocumentDto.From(await accounting.VerifyExternalDocumentAsync(orgId, referenceId, User.GetUserId(), HttpContext.RequestAborted)));
    }

    /// <summary>Removes a mistaken link with its reason (<c>{ reason }</c>): the link stays as evidence. 409 <c>owner_external_document_removed</c>.</summary>
    [HttpPost("external-documents/{referenceId:guid}/remove")]
    [Authorize(Policy = CasazenPolicies.AgencyOwnerFinancialManage)]
    [ProducesResponseType(typeof(OwnerExternalDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerExternalDocumentDto>> RemoveExternalDocument(Guid referenceId, [FromBody] OwnerExternalDocumentRemoval request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();
        var reference = await accounting.RemoveExternalDocumentAsync(orgId, referenceId, request.Reason, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(OwnerExternalDocumentDto.From(reference));
    }

    /// <summary>The caller's org, only when its reach is the whole org (the owner accounting spans the properties of the owners).</summary>
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

/// <summary>The removal of a mistaken link to an external invoice, with its reason.</summary>
public sealed record OwnerExternalDocumentRemoval(string? Reason);

/// <summary>A link of a version to an invoice issued by the agency's external fiscal system, with its audit facts.</summary>
public sealed record OwnerExternalDocumentDto(
    Guid Id,
    Guid StatementVersionId,
    Guid OwnerId,
    string Provider,
    string ExternalId,
    string ExternalNumber,
    DateOnly ExternalDate,
    string? DocumentUrl,
    string? DocumentSha256,
    OwnerExternalDocumentStatus Status,
    DateTime? VerifiedAt,
    string? VerifiedByUserId,
    DateTime? RemovedAt,
    string? RemovedByUserId,
    string? RemovalReason,
    DateTime CreatedAt,
    string? CreatedByUserId)
{
    public static OwnerExternalDocumentDto From(OwnerExternalDocumentReference r) => new(
        r.Id, r.OwnerStatementVersionId, r.PropertyOwnerId, r.Provider, r.ExternalId, r.ExternalNumber, r.ExternalDate, r.DocumentUrl, r.DocumentSha256,
        r.Status, r.VerifiedAt, r.VerifiedByUserId, r.RemovedAt, r.RemovedByUserId, r.RemovalReason, r.CreatedAt, r.CreatedByUserId);
}

/// <summary>The legal hold of a month: set with its reason, or lifted.</summary>
public sealed record OwnerLegalHoldRequest(bool Hold, string? Reason);

/// <summary>A line of the owner ledger with its source and the facts of its amount.</summary>
public sealed record OwnerLedgerEntryDto(
    Guid Id,
    Guid OwnerId,
    Guid? PropertyId,
    OwnerLedgerEntryType Type,
    OwnerLedgerSourceType Source,
    Guid? SourceId,
    string? SourceReference,
    Guid? SourceDocumentId,
    string? AttachmentReference,
    DateOnly ServiceDate,
    DateOnly RecognitionDate,
    DateOnly? PaymentDate,
    int Year,
    int Month,
    Guid? OwnershipId,
    decimal SharePercent,
    string OriginalCurrency,
    decimal SourceAmount,
    decimal OriginalAmount,
    string StatementCurrency,
    decimal FxRate,
    string? FxSource,
    DateOnly? FxDate,
    string? TaxCode,
    string? RuleVersion,
    decimal Amount,
    Guid? ReversesEntryId,
    string? Description,
    DateTime PostedAt)
{
    public static OwnerLedgerEntryDto From(OwnerLedgerEntry e) => new(
        e.Id, e.PropertyOwnerId, e.PropertyId, e.EntryType, e.SourceType, e.SourceId, e.SourceReference, e.SourceDocumentId, e.AttachmentReference,
        e.ServiceDate, e.RecognitionDate, e.PaymentDate, e.PeriodYear, e.PeriodMonth, e.PropertyOwnershipId, e.SharePercent, e.OriginalCurrency,
        e.SourceAmount, e.OriginalAmount, e.StatementCurrency, e.FxRate, e.FxSource, e.FxDate, e.TaxCode, e.RuleVersion, e.Amount, e.ReversesEntryId,
        e.Description, e.PostedAt);
}

/// <summary>A version of an owner statement, without its published file (<c>…/published</c>).</summary>
public sealed record OwnerStatementVersionDto(
    Guid Id,
    Guid OwnerId,
    int Year,
    int Month,
    int Version,
    OwnerStatementVersionStatus Status,
    string Currency,
    DateTime CutoffAt,
    decimal OpeningBalance,
    decimal Revenue,
    decimal ChannelFees,
    decimal Commission,
    decimal Charges,
    decimal Payments,
    decimal Corrections,
    decimal ClosingBalance,
    int EntryCount,
    string EntriesSha256,
    string? IssuesJson,
    DateTime? ApprovedAt,
    string? ApprovedByUserId,
    DateTime? PublishedAt,
    string? PublishedByUserId,
    string? PublishedSha256,
    Guid? SupersedesVersionId,
    Guid? SupersededByVersionId,
    DateTime CreatedAt)
{
    public static OwnerStatementVersionDto From(OwnerStatementVersion v) => new(
        v.Id, v.PropertyOwnerId, v.PeriodYear, v.PeriodMonth, v.VersionNumber, v.Status, v.Currency, v.CutoffAt, v.OpeningBalance, v.Revenue, v.ChannelFees,
        v.Commission, v.Charges, v.Payments, v.Corrections, v.ClosingBalance, v.EntryCount, v.EntriesSha256, v.IssuesJson, v.ApprovedAt, v.ApprovedByUserId,
        v.PublishedAt, v.PublishedByUserId, v.PublishedSha256, v.SupersedesVersionId, v.SupersededByVersionId, v.CreatedAt);
}

/// <summary>A version with its lines.</summary>
public sealed record OwnerStatementVersionDetailDto(OwnerStatementVersionDto Version, IReadOnlyList<OwnerLedgerEntryDto> Entries);

/// <summary>The month of an owner: the close with its reconciliation or waiver, the retention and the legal hold.</summary>
public sealed record OwnerClosePeriodDto(
    Guid OwnerId,
    int Year,
    int Month,
    OwnerClosePeriodStatus Status,
    bool IsDirty,
    DateTime? ClosedAt,
    string? ClosedByUserId,
    Guid? ClosedVersionId,
    decimal? DueAmount,
    decimal? PaidAmount,
    decimal? Difference,
    string? WaiverReason,
    string? WaivedByUserId,
    DateTime? WaivedAt,
    string? RetentionJurisdiction,
    int? RetentionYears,
    DateOnly? RetainUntil,
    bool LegalHold,
    string? LegalHoldReason,
    DateTime? LegalHoldAt)
{
    public static OwnerClosePeriodDto From(OwnerClosePeriod p) => new(
        p.PropertyOwnerId, p.PeriodYear, p.PeriodMonth, p.Status, p.IsDirty, p.ClosedAt, p.ClosedByUserId, p.ClosedVersionId, p.DueAmount, p.PaidAmount,
        p.Difference, p.WaiverReason, p.WaivedByUserId, p.WaivedAt, p.RetentionJurisdiction, p.RetentionYears, p.RetainUntil, p.LegalHold, p.LegalHoldReason,
        p.LegalHoldAt);
}

/// <summary>A charge or an expense of the owners of a property.</summary>
public sealed record OwnerChargeDto(
    Guid Id,
    Guid PropertyId,
    Guid? OwnerId,
    OwnerChargeKind Kind,
    OwnerChargeRecurrence Recurrence,
    string Description,
    decimal Amount,
    string Currency,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? AttachmentReference,
    OwnerChargeStatus Status,
    DateTime? ApprovedAt,
    string? ApprovedByUserId,
    DateTime? CancelledAt,
    DateTime CreatedAt)
{
    public static OwnerChargeDto From(OwnerCharge c) => new(
        c.Id, c.PropertyId, c.PropertyOwnerId, c.Kind, c.Recurrence, c.Description, c.Amount, c.Currency, c.EffectiveFrom, c.EffectiveTo,
        c.AttachmentReference, c.Status, c.ApprovedAt, c.ApprovedByUserId, c.CancelledAt, c.CreatedAt);
}

/// <summary>An exchange rate of the owner accounting.</summary>
public sealed record OwnerFxRateDto(Guid Id, string BaseCurrency, string QuoteCurrency, DateOnly RateDate, decimal Rate, string Source, DateTime CreatedAt)
{
    public static OwnerFxRateDto From(OwnerFxRate r) => new(r.Id, r.BaseCurrency, r.QuoteCurrency, r.RateDate, r.Rate, r.Source, r.CreatedAt);
}

/// <summary>A published file of a version, with its hash.</summary>
public sealed record OwnerPublishedFileDto(Guid VersionId, int Version, string Sha256, string Content);

/// <summary>The evidence of an owner's month, exportable as it is.</summary>
public sealed record OwnerAccountingEvidenceDto(
    Guid OwnerId,
    int Year,
    int Month,
    OwnerClosePeriodDto? Period,
    IReadOnlyList<OwnerStatementVersionDto> Versions,
    IReadOnlyList<OwnerPublishedFileDto> PublishedFiles,
    IReadOnlyList<OwnerLedgerEntryDto> Entries,
    IReadOnlyList<OwnerChargeDto> Charges)
{
    public static OwnerAccountingEvidenceDto From(OwnerAccountingEvidence e) => new(
        e.OwnerId,
        e.Year,
        e.Month,
        e.Period is null ? null : OwnerClosePeriodDto.From(e.Period),
        e.Versions.Select(OwnerStatementVersionDto.From).ToList(),
        e.Versions.Where(v => v.PublishedSnapshot is not null && v.PublishedSha256 is not null)
            .Select(v => new OwnerPublishedFileDto(v.Id, v.VersionNumber, v.PublishedSha256!, v.PublishedSnapshot!))
            .ToList(),
        e.Entries.Select(OwnerLedgerEntryDto.From).ToList(),
        e.Charges.Select(OwnerChargeDto.From).ToList());
}
