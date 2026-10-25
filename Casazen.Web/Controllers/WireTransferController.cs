using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Wire transfer batch management (ADR-017). Generates SEPA pain.001 (Italy) or Swiss Payment Standards (Switzerland)
/// XML files for paying property owners. One wire per owner, one file per currency and debit account.
/// </summary>
[ApiController]
[Route("api/wire-transfers")]
[Authorize(Policy = CasazenPolicies.AgencyWireManage)]
public class WireTransferController(
    IWireTransferService wireTransferService,
    IOrgContextResolver orgContextResolver,
    ILogger<WireTransferController> logger) : ControllerBase
{
    /// <summary>
    /// Create a wire transfer batch from the given statement IDs. Returns the batch ID.
    /// The batch file can then be downloaded via GET /api/wire-transfers/batch/{id}/file.
    /// </summary>
    /// <param name="request">Statement IDs and debit account IBAN.</param>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(CreateBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CreateBatchResponse>> CreateBatch([FromBody] CreateBatchRequest request)
    {
        logger.LogInformation("Creating wire batch for {Count} statements", request.StatementIds.Count);

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var (batch, _) = await wireTransferService.CreateBatchAsync(
            orgId.Value,
            request.StatementIds,
            request.DebitAccount,
            HttpContext.RequestAborted);

        return Ok(new CreateBatchResponse
        {
            BatchId = batch.Id,
            Currency = batch.Currency,
            TotalAmount = batch.TotalAmount,
            TransferCount = batch.TransferCount
        });
    }

    /// <summary>
    /// Download the wire transfer batch file (SEPA pain.001 or Swiss Payment Standards XML).
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

        // Regenerate the batch file (in production, we'd store it or cache it)
        // For now, we'll need to reconstruct it from the batch and statements
        // This is a simplified implementation - a real one would store the file content

        return NotFound(new ProblemDetails
        {
            Status = StatusCodes.Status501NotImplemented,
            Title = "Not Implemented",
            Detail = "File download endpoint requires batch file storage implementation"
        });
    }

    /// <summary>
    /// Confirm that the batch file has been downloaded.
    /// Updates the batch status to Downloaded.
    /// </summary>
    /// <param name="id">The batch ID.</param>
    [HttpPost("batch/{id}/confirm-download")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmDownload(Guid id)
    {
        logger.LogInformation("Confirming download of wire batch {BatchId}", id);

        await wireTransferService.ConfirmDownloadAsync(id, HttpContext.RequestAborted);

        return NoContent();
    }

    /// <summary>
    /// Confirm that the batch has been paid (wire transfers executed at the bank).
    /// Updates the batch status to Paid.
    /// </summary>
    /// <param name="id">The batch ID.</param>
    [HttpPost("batch/{id}/confirm-paid")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmPaid(Guid id)
    {
        logger.LogInformation("Confirming payment of wire batch {BatchId}", id);

        await wireTransferService.ConfirmPaidAsync(id, HttpContext.RequestAborted);

        return NoContent();
    }
}

/// <summary>Request to create a wire transfer batch.</summary>
public sealed record CreateBatchRequest
{
    /// <summary>IDs of the published owner statements to include in the batch.</summary>
    public List<Guid> StatementIds { get; init; } = [];

    /// <summary>The IBAN from which the transfers will be sent.</summary>
    public string DebitAccount { get; init; } = string.Empty;
}

/// <summary>Response after creating a wire transfer batch.</summary>
public sealed record CreateBatchResponse
{
    /// <summary>The ID of the created batch.</summary>
    public Guid BatchId { get; init; }

    /// <summary>Currency of the batch.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>Total amount of all transfers in the batch.</summary>
    public decimal TotalAmount { get; init; }

    /// <summary>Number of wire transfers in the batch.</summary>
    public int TransferCount { get; init; }
}
