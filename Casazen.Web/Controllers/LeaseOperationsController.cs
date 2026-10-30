using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The operations of a lease in force (LR-OPS-1): what the landlord can do about the end of the lease, the letters CasaZen prepares for it as
/// drafts (PDF) and the record that a letter was sent. Every endpoint needs <c>lease.create</c> and authorizes the lease as a
/// <see cref="HostResource"/> of its property (TN-3, AM-03): another org's lease is 404, one the caller may not handle is 403. The letters carry
/// the residences of the parties, so even reading the state needs the permission to write the lease.
/// </summary>
/// <remarks>
/// Nothing is written to the tenant by any of them: no e-mail, no PEC, no registered mail. A letter is a draft the person signs and sends; it is
/// printed with the watermark «BOZZA — testo non ancora approvato» until the product owner approves its text on the host. The withdrawal of the
/// tenant, which ends the lease, is <c>POST api/leases/{id}/withdrawal</c> of <see cref="LeaseTermController"/>. The runbook
/// <c>docs/runbooks/lease-operations.md</c> has the rules.
/// </remarks>
[ApiController]
[Route("api/leases")]
[Authorize(Policy = CasazenPolicies.LeaseCreate)]
public class LeaseOperationsController(
    ILeaseOperationsService operations,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    private const string LeaseNotFoundCode = "lease_not_found";

    /// <summary>
    /// What the landlord can do about the end of the lease today (the notice of non-renewal: whether it is available, by when it is on time,
    /// the reasons of the law at the first expiry; the withdrawal of the tenant: whether it can be recorded and the one already recorded) and the
    /// state of each letter (approved or draft, and when it was marked as sent).
    /// </summary>
    [HttpGet("{id:guid}/operations")]
    [ProducesResponseType(typeof(LeaseOperationsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        Response.Headers.CacheControl = "private, no-store";
        return Ok(await operations.GetAsync(id, cancellationToken));
    }

    /// <summary>
    /// The draft of the landlord's notice of non-renewal, as a PDF. At the first expiry <c>reason</c> is required (422
    /// <c>lease_disdetta_reason_required</c>) and the letter states it; later it is ignored. 409 <c>lease_not_in_force</c>,
    /// <c>lease_already_terminated</c>; 422 <c>lease_type_has_no_renewal</c> (a transitory lease ends by itself).
    /// </summary>
    [HttpPost("{id:guid}/letters/disdetta")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Disdetta(Guid id, [FromBody] DisdettaLetterDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Pdf(await operations.BuildDisdettaAsync(id, dto.Reason, cancellationToken));
    }

    /// <summary>
    /// The draft of the landlord's acknowledgement of the tenant's withdrawal, as a PDF, for a withdrawal received on <c>receivedOn</c> (a day
    /// from the start of the lease up to today). It states the last day of the lease the way <c>POST withdrawal</c> would end it.
    /// </summary>
    [HttpPost("{id:guid}/letters/presa-atto-recesso")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> PresaAttoRecesso(Guid id, [FromBody] PresaAttoRecessoLetterDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Pdf(await operations.BuildPresaAttoRecessoAsync(id, dto.ReceivedOn!.Value, dto.Reason!.Value, dto.NoticeMonths, cancellationToken));
    }

    /// <summary>
    /// The draft of the agreement of the parties to renew or extend the lease, as a PDF, for the period from <c>startsOn</c> to <c>endsOn</c>
    /// (<c>agreed</c>: an extension to a day agreed on, otherwise the standard renewal). Both parties sign it. 422
    /// <c>lease_letter_not_applicable</c> (a type that does not renew), <c>lease_renewal_end_invalid</c>.
    /// </summary>
    [HttpPost("{id:guid}/letters/rinnovo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rinnovo(Guid id, [FromBody] RinnovoLetterDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Pdf(await operations.BuildRinnovoAsync(id, dto.StartsOn!.Value, dto.EndsOn!.Value, dto.Agreed, cancellationToken));
    }

    /// <summary>
    /// The draft of the confirmation that the need of a transitory lease goes on, from the landlord to the tenant, as a PDF. 422
    /// <c>lease_letter_not_applicable</c> when the lease is not a transitory one in force or the need is the tenant's.
    /// </summary>
    [HttpPost("{id:guid}/letters/conferma-esigenza")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfermaEsigenza(Guid id, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Pdf(await operations.BuildConfermaEsigenzaAsync(id, cancellationToken));
    }

    /// <summary>
    /// The ISTAT update of the rent worked out from the variation of the FOI index the landlord typed and the share of it the contract
    /// recognizes. Nothing is written and no external service is called. 409 <c>lease_not_in_force</c>, <c>lease_already_terminated</c>; 422
    /// <c>lease_istat_waived</c> (cedolare secca), <c>lease_istat_no_rent</c>, <c>lease_istat_percent_invalid</c>, <c>lease_istat_no_increase</c>.
    /// </summary>
    [HttpPost("{id:guid}/istat/calculate")]
    [ProducesResponseType(typeof(LeaseIstatCalculationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CalculateIstat(Guid id, [FromBody] IstatCalculationInputDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        Response.Headers.CacheControl = "private, no-store";
        return Ok(await operations.CalculateIstatAsync(id, dto.FoiPercent!.Value, dto.SharePercent!.Value, cancellationToken));
    }

    /// <summary>
    /// The draft of the landlord's request to update the rent, as a PDF, with the numbers <c>istat/calculate</c> gives for the same percentages,
    /// from <c>effectiveFrom</c> (never in the past, not after the end of the period: 422 <c>lease_letter_effect_date_invalid</c>).
    /// </summary>
    [HttpPost("{id:guid}/letters/aggiornamento-istat")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> AggiornamentoIstat(Guid id, [FromBody] AggiornamentoIstatLetterDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Pdf(await operations.BuildAggiornamentoIstatAsync(id, dto.FoiPercent!.Value, dto.SharePercent!.Value, dto.EffectiveFrom!.Value, cancellationToken));
    }

    /// <summary>
    /// The draft of the communication to the tenant that the landlord has chosen the cedolare secca, with the renunciation of the updates of the
    /// rent, as a PDF. 422 <c>lease_letter_not_applicable</c> when the lease is not under the cedolare secca.
    /// </summary>
    [HttpPost("{id:guid}/letters/opzione-cedolare")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpzioneCedolare(Guid id, [FromBody] OpzioneCedolareLetterDto dto, CancellationToken cancellationToken)
    {
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        return Pdf(await operations.BuildOpzioneCedolareAsync(id, dto.EffectiveFrom, cancellationToken));
    }

    /// <summary>
    /// Marks a letter (<c>disdetta</c>, <c>presa-atto-recesso</c>, <c>conferma-esigenza</c>, <c>aggiornamento-istat</c>, <c>opzione-cedolare</c>) as sent on <c>sentOn</c> (a day from the start of the lease up to today) by
    /// <c>means</c>: an event in the history of the lease and nothing else, the lease does not change. 200 with the operations of the lease; 404
    /// for a letter that does not exist; 409 <c>lease_not_in_force</c>; 422 <c>lease_letter_date_invalid</c>.
    /// </summary>
    [HttpPost("{id:guid}/letters/{kind}/sent")]
    [ProducesResponseType(typeof(LeaseOperationsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkSent(Guid id, string kind, [FromBody] MarkLetterSentDto dto, CancellationToken cancellationToken)
    {
        if (!LeaseLetterKinds.TryParseSlug(kind, out var letter))
            return NotFound();
        if (await AuthorizeLeaseAsync(id, cancellationToken) is { } denied)
            return denied;

        await operations.MarkLetterSentAsync(id, letter, dto.SentOn!.Value, dto.Means!.Value, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await operations.GetAsync(id, cancellationToken));
    }

    // ---- authorization and answer --------------------------------------------------------------------------------

    private IActionResult Pdf(LeaseLetterFile file)
    {
        // The letter carries the residences of the parties: never cached.
        Response.Headers.CacheControl = "private, no-store";
        return File(file.Pdf, "application/pdf", file.FileName);
    }

    /// <summary>
    /// Authorizes <c>lease.create</c> on the property of the lease as a row of that property: <c>null</c> when allowed, otherwise the answer
    /// (404 <c>lease_not_found</c> for a lease of another org or none, 403 for a property the caller may not handle).
    /// </summary>
    private async Task<IActionResult?> AuthorizeLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        if (await operations.FindPropertyIdAsync(leaseId, cancellationToken) is not { } propertyId)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, LeaseNotFoundCode, "LeaseNotFound");

        return await authorizationService.IsAuthorizedAsync(User, resource, LeaseOperations.Create) ? null : Forbid();
    }
}

/// <summary>The notice of non-renewal: the reason of the law at the first expiry (required then, ignored later).</summary>
public sealed record DisdettaLetterDto([param: EnumDataType(typeof(LeaseDisdettaReason))] LeaseDisdettaReason? Reason = null);

/// <summary>The acknowledgement of a withdrawal: the day it was received, why, and the months of notice when the contract gave the faculty.</summary>
public sealed record PresaAttoRecessoLetterDto(
    [param: Required] DateOnly? ReceivedOn,
    [param: Required, EnumDataType(typeof(TenantWithdrawalReason))] TenantWithdrawalReason? Reason,
    int? NoticeMonths = null);

/// <summary>The agreement to renew or extend the lease: the first and the last day of the period it adds, and whether it is an extension agreed on a day.</summary>
public sealed record RinnovoLetterDto([param: Required] DateOnly? StartsOn, [param: Required] DateOnly? EndsOn, bool Agreed = false);

/// <summary>The ISTAT update to work out: the variation of the FOI index (percent, above zero, two decimals at most) and the share of it the contract recognizes.</summary>
public sealed record IstatCalculationInputDto([param: Required] decimal? FoiPercent, [param: Required] decimal? SharePercent);

/// <summary>The request to update the rent: the same percentages and the day the new rent applies from.</summary>
public sealed record AggiornamentoIstatLetterDto(
    [param: Required] decimal? FoiPercent, [param: Required] decimal? SharePercent, [param: Required] DateOnly? EffectiveFrom);

/// <summary>The communication of the cedolare secca: the first day the option applies from, when the person says it.</summary>
public sealed record OpzioneCedolareLetterDto(DateOnly? EffectiveFrom = null);

/// <summary>A letter marked as sent: the day (<c>yyyy-MM-dd</c>, from the start of the lease up to today) and how.</summary>
public sealed record MarkLetterSentDto(
    [param: Required] DateOnly? SentOn,
    [param: Required, EnumDataType(typeof(LeaseLetterMeans))] LeaseLetterMeans? Means);
