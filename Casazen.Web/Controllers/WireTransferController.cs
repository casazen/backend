using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Payments;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Wire transfer batch management (ADR-017, E16/F01/US01/T01). Builds the bank file that pays the owners from their confirmed payouts: the Swiss
/// <c>pain.001.001.09.ch.03</c> file for CHF, the SEPA <c>pain.001.001.03</c> file for EUR. One transfer per payout, one file per currency. Every endpoint takes the
/// org of the caller and the service filters on it explicitly: a batch of another org is a 404.
/// </summary>
/// <remarks>
/// The whole controller is behind <c>agency.wire.manage</c>, held by the owner and the administrator of an org on the agency plan, who reach every property of
/// the org: there is no property scope to apply (<c>WireTransferPermission_IsHeldOnlyByTheRolesThatReachEveryProperty_Test</c>).
/// </remarks>
[ApiController]
[Route("api/wire-transfers")]
[Authorize(Policy = CasazenPolicies.AgencyWireManage)]
public class WireTransferController(
    IWireTransferService wireTransferService,
    IOrgContextResolver orgContextResolver,
    ILogger<WireTransferController> logger) : ControllerBase
{
    /// <summary>
    /// Create a wire transfer batch from the given confirmed payouts. Returns the batch ID.
    /// The batch file can then be downloaded via GET /api/wire-transfers/batch/{id}/file.
    /// </summary>
    /// <param name="request">The payout IDs and the execution date. The request of before the payouts (statement IDs and a debit account) is refused.</param>
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

        // The request of before the payouts is still read, to be answered with its code: a batch is never made from statements, and a debit account that was
        // typed is never ignored in silence (the money would leave another account than the one the caller named).
        if (request.StatementIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.DebitAccount))
            throw new DomainRuleException(WireTransferErrors.FromStatementRetired, "WireBatchFromStatementRetired");

        var payoutIds = request.PayoutIds ?? [];
        logger.LogInformation("Creating wire batch for {Count} payouts", payoutIds.Count);

        // No date is the date that never is: the service answers 422 wire_execution_date_invalid.
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

    /// <summary>
    /// The batches of the org, newest first: state, currency, number of transfers and total, with no IBAN, no name and no file.
    /// </summary>
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
    /// Download the wire transfer batch file (Swiss pain.001.001.09.ch.03 or SEPA pain.001.001.03 XML).
    /// Automatically marks the batch as Downloaded on first retrieval.
    /// </summary>
    /// <param name="id">The batch ID.</param>
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

        // Return the XML file as a download
        var fileName = $"wire-batch-{batch.Id:N}-{batch.Currency}-{batch.CreatedAt:yyyyMMdd}.xml";
        var bytes = System.Text.Encoding.UTF8.GetBytes(xmlContent);

        return File(bytes, "application/xml", fileName);
    }

    /// <summary>
    /// Confirm that the batch file has been downloaded.
    /// Updates the batch status to Downloaded. A batch of another org is a 404.
    /// </summary>
    /// <param name="id">The batch ID.</param>
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

    /// <summary>
    /// Confirm that the batch has been paid (wire transfers executed at the bank).
    /// Updates the batch status to Paid. A batch of another org is a 404.
    /// </summary>
    /// <param name="id">The batch ID.</param>
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
}
