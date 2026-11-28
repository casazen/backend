using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Wire transfer batch management (ADR-017, E16/F01/US01/T01, E16/F01/US02/T01). Builds the bank file that pays the owners from their confirmed payouts: the Swiss
/// <c>pain.001.001.09.ch.03</c> file for CHF, the ISO 20022 <c>pain.001.001.09</c> file of the SEPA credit transfer for EUR. One transfer per payout, one file per
/// currency. Every endpoint takes the org of the caller and the service filters on it explicitly: a batch of another org is a 404.
/// </summary>
/// <remarks>
/// The whole controller is behind <c>agency.wire.manage</c>, held by the owner and the administrator of an org on the agency plan, who reach every property of
/// the org: there is no property scope to apply (<c>WireTransferPermission_IsHeldOnlyByTheRolesThatReachEveryProperty_Test</c>). The two reads of the wire page
/// (<c>payable-payouts</c> and <c>batch/{id}/payouts</c>) list the payouts of every owner of the org and ask for the whole org on top of the permission, as the owner
/// accounting does: a caller limited to some properties gets 403 (<c>WireReads_PropertyLimitedCaller_IsForbidden_Test</c>).
/// </remarks>
[ApiController]
[Route("api/wire-transfers")]
[Authorize(Policy = CasazenPolicies.AgencyWireManage)]
public class WireTransferController(
    IWireTransferService wireTransferService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<WireTransferController> logger) : ControllerBase
{
    [HttpPost("batch")]
    [ProducesResponseType(typeof(CreateBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CreateBatchResponse>> CreateBatch([FromBody] CreateBatchRequest request)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        if (request.StatementIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.DebitAccount))
            throw new DomainRuleException(WireTransferErrors.FromStatementRetired, "WireBatchFromStatementRetired");

        var payoutIds = request.PayoutIds ?? [];
        logger.LogInformation("Creating wire batch for {Count} payouts", payoutIds.Count);

        var (batch, _) = await wireTransferService.CreateBatchAsync(
            orgId.Value,
            payoutIds,
            request.ExecutionDate ?? default,
            HttpContext.RequestAborted);

        return Ok(new CreateBatchResponse
        {
            BatchId = batch.Id,
            Currency = batch.Currency,
            TotalAmount = batch.TotalAmount,
            TransferCount = batch.TransferCount,
            ExecutionDate = batch.ExecutionDate,
            Format = batch.MessageFormat
        });
    }

    [HttpGet("batches")]
    [ProducesResponseType(typeof(IReadOnlyList<WireBatchListItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<WireBatchListItemResponse>>> ListBatches()
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var batches = await wireTransferService.ListBatchesAsync(orgId.Value, HttpContext.RequestAborted);

        return Ok(batches.Select(WireBatchListItemResponse.From).ToList());
    }

    /// <summary>
    /// The payouts that a wire file of one currency can still pay: confirmed (never reversed), of a statement version published to the owner and in no batch yet, oldest
    /// first. One list per currency, EUR or CHF, never both. Each payout comes with its owner, the period of its statement, its net amount and whether the owner has an IBAN
    /// as a yes or a no: <b>never the IBAN</b>. Needs the whole org: a caller limited to some properties gets 403.
    /// </summary>
    /// <param name="currency">EUR or CHF. Missing, or a currency with no wire file: 422 <c>wire_currency_not_supported</c>.</param>
    [HttpGet("payable-payouts")]
    [ProducesResponseType(typeof(IReadOnlyList<WirePayoutResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<WirePayoutResponse>>> ListPayablePayouts([FromQuery] string? currency)
    {
        if (await OrgWideAsync() is not { } orgId)
            return Forbid();

        var payouts = await wireTransferService.ListPayablePayoutsAsync(orgId, currency, HttpContext.RequestAborted);

        Response.Headers.CacheControl = "no-store";
        return Ok(payouts.Select(WirePayoutResponse.From).ToList());
    }

    [HttpGet("batch/{id}/payouts")]
    [ProducesResponseType(typeof(WireBatchPayoutsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WireBatchPayoutsResponse>> GetBatchPayouts(Guid id)
    {
        if (await OrgWideAsync() is not { } orgId)
            return Forbid();

        var batch = await wireTransferService.GetBatchPayoutsAsync(orgId, id, HttpContext.RequestAborted);

        Response.Headers.CacheControl = "no-store";
        return Ok(WireBatchPayoutsResponse.From(batch));
    }

    [HttpGet("batch/{id}/file")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatchFile(Guid id)
    {
        logger.LogInformation("Downloading wire batch file {BatchId}", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var (batch, xmlContent) = await wireTransferService.GetBatchFileAsync(
            orgId.Value,
            id,
            HttpContext.RequestAborted);

        var fileName = $"wire-batch-{batch.Id:N}-{batch.Currency}-{batch.CreatedAt:yyyyMMdd}.xml";
        var bytes = System.Text.Encoding.UTF8.GetBytes(xmlContent);

        return File(bytes, "application/xml", fileName);
    }

    [HttpPost("batch/{id}/confirm-download")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmDownload(Guid id)
    {
        logger.LogInformation("Confirming download of wire batch {BatchId}", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        await wireTransferService.ConfirmDownloadAsync(orgId.Value, id, HttpContext.RequestAborted);

        return NoContent();
    }

    [HttpPost("batch/{id}/confirm-paid")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmPaid(Guid id)
    {
        logger.LogInformation("Confirming payment of wire batch {BatchId}", id);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        await wireTransferService.ConfirmPaidAsync(orgId.Value, id, HttpContext.RequestAborted);

        return NoContent();
    }

    private Task<Guid?> OrgWideAsync() =>
        HostCallerResolver.OrgWideAsync(User, orgContextResolver, hostScopeResolver, HttpContext.RequestAborted);
}
