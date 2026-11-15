using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The self-billed invoices (TD17) of the commissions that foreign portals charge an Italian account holder (E17/F01/US03/T01, NF-116): the list and the detail of the month
/// (the drafts and the issued ones), the drafts of a month made from the owner ledger, the issue of a draft and the discard of a draft. Every action needs
/// <c>agency.invoice.manage</c> (create and issue invoices, agency plan) and <c>Features:FiscalDocumentIssuance</c> (404 while it is off). The PDF and the XML of a TD17 are the ones of
/// every fiscal document (<c>GET api/fiscal/documents/{id}/pdf</c> and <c>/xml</c>, a document without a booking: the whole org only); once issued its state towards the SDI is
/// <c>to_send</c> like every Italian electronic invoice, and the SDI port sends it.
/// </summary>
/// <remarks>
/// A TD17 spans the properties of a holder, like the owner accounting it comes from: a caller restricted to some properties is refused (403) even with the permission, on every
/// action, reads included. A document of another org, and any document that is not a TD17, is 404. The endpoints of the guest documents (<c>api/fiscal/documents</c>) refuse to
/// change a TD17 (422 <c>document_self_billing</c>).
/// </remarks>
[ApiController]
[Route("api/fiscal/self-billing")]
[Authorize(Policy = CasazenPolicies.AgencyInvoiceManage)]
[FeatureGate(FeatureFlags.FiscalDocumentIssuance)]
public class SelfBillingController(
    ISelfBillingService selfBilling,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The TD17 of the org (drafts and issued ones), the newest month first: <c>period</c> (<c>yyyy-MM</c>) narrows it to a month. Each with its holder (<c>Agency</c> or
    /// <c>Owner</c>), portal, month, deadline, amounts, status and state towards the SDI. 422 <c>self_billing_period_invalid</c> for a period that is not one.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<SelfBillingListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<List<SelfBillingListItemDto>>> List([FromQuery] string? period = null)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        int? year = null, month = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!DateOnly.TryParseExact(period.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, SelfBillingErrors.PeriodInvalid, "SelfBillingPeriodInvalid");
            (year, month) = (first.Year, first.Month);
        }

        var rows = await selfBilling.ListAsync(orgId, new SelfBillingCriteria(year, month), HttpContext.RequestAborted);
        return Ok(rows.Select(SelfBillingListItemDto.From).ToList());
    }

    /// <summary>One TD17 with its lines (one for each property). 404 <c>fiscal_document_not_found</c> for a document of another org or one that is not a TD17.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SelfBillingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SelfBillingDto>> Get(Guid id)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        return Ok(SelfBillingDto.From(await selfBilling.GetAsync(orgId, id, HttpContext.RequestAborted)));
    }

    /// <summary>
    /// The drafts of a month (<c>{ period: "yyyy-MM" }</c>) for every holder and portal that needs a TD17: made, or made again, with the counts, one row for each TD17 (with the
    /// part of its amount that is an estimate), the holders that need one and cannot get one yet (<c>failed</c>) and the commissions that are not self-billed (<c>notDue</c>) with the
    /// reason, and <c>ledgerPosted</c> (false: the owner ledger of the month has no line yet, post the month first). 422 <c>self_billing_period_invalid</c>,
    /// <c>self_billing_period_not_ended</c> (the month is not over), <c>self_billing_not_configured</c>.
    /// </summary>
    [HttpPost("drafts")]
    [ProducesResponseType(typeof(SelfBillingGenerationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SelfBillingGenerationDto>> CreateDrafts([FromBody] GenerateSelfBillingRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.Period)
            || !DateOnly.TryParseExact(request.Period.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, SelfBillingErrors.PeriodInvalid, "SelfBillingPeriodInvalid");
        }

        var generation = await selfBilling.CreateDraftsForPeriodAsync(orgId, first.Year, first.Month, User.GetUserId(), HttpContext.RequestAborted);
        return Ok(SelfBillingGenerationDto.From(generation));
    }

    /// <summary>
    /// The draft issued after the confirmation (<c>{ confirm: true }</c>, else 422 <c>document_confirmation_required</c>): numbered in the series of its holder, with its XML, to be
    /// sent to the SDI. 404 for a document of another org or one that is not a TD17; 422 <c>document_not_draft</c>, <c>self_billing_period_not_ended</c>, <c>self_billing_not_configured</c>,
    /// <c>fatturapa_xml_invalid</c>, <c>fatturapa_schema_unavailable</c>, <c>series_too_long</c>; 409 <c>document_draft_outdated</c> (make the drafts of the month again first). When part of the
    /// amount is an estimate (a rule of the agency or the default of the channel: <c>estimatedAmount</c> of <c>GET {id}</c>) the issue needs <c>acceptEstimate: true</c> too, else 422
    /// <c>self_billing_estimate_unconfirmed</c> (with <c>estimatedAmount</c>) and nothing is numbered.
    /// </summary>
    [HttpPost("{id:guid}/issue")]
    [ProducesResponseType(typeof(SelfBillingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SelfBillingDto>> Issue(Guid id, [FromBody] IssueSelfBillingRequest request)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        var issued = await selfBilling.IssueAsync(orgId, id, request.Confirm, User.GetUserId(), HttpContext.RequestAborted, request.AcceptEstimate);
        return Ok(SelfBillingDto.From(issued));
    }

    /// <summary>
    /// The draft of a TD17 discarded (no number is consumed): its holder, portal and month are free for a new draft. 404 for a document of another org or one that is not a TD17;
    /// 422 <c>document_not_draft</c>.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Discard(Guid id)
    {
        if (await OrgAsync() is not { } orgId)
            return Forbid();

        await selfBilling.DiscardDraftAsync(orgId, id, HttpContext.RequestAborted);
        return NoContent();
    }

    /// <summary>The caller's org, only when its reach is the whole org.</summary>
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
