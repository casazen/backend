using System.ComponentModel.DataAnnotations;
using Casazen.Core.Features;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/octorate-preparation")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public sealed class AdminOctoratePreparationController(OctoratePreparationService preparations, IBackgroundJobClient jobs) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PreparationRow>>> Recent(CancellationToken ct) => Ok(await preparations.RecentAsync(ct));

    [HttpGet("candidates")]
    public async Task<ActionResult<PreparationCandidates>> Candidates([FromQuery, MaxLength(120)] string? search, CancellationToken ct) =>
        Ok(await preparations.CandidatesAsync(search, ct));

    [HttpGet("{batchId:guid}")]
    public async Task<ActionResult<PreparationBatch>> Get(Guid batchId, CancellationToken ct) => Ok(await preparations.ViewAsync(batchId, ct));

    [HttpPost]
    public async Task<ActionResult<PreparationBatch>> Start(PreparationBatchInput input, CancellationToken ct)
    {
        if (User.GetUserId() is not { Length: > 0 } staff)
            return Forbid();

        var batch = await preparations.StartAsync(input.Properties, ct);
        EnqueueRun(batch.Id, staff);
        return Accepted(batch);
    }

    [HttpPost("{batchId:guid}/resume")]
    public async Task<IActionResult> Resume(Guid batchId, CancellationToken ct)
    {
        if (User.GetUserId() is not { Length: > 0 } staff)
            return Forbid();

        await preparations.ViewAsync(batchId, ct);
        EnqueueRun(batchId, staff);
        return Accepted();
    }

    [HttpPut("{batchId:guid}/supplier-reference")]
    public async Task<IActionResult> SupplierReference(Guid batchId, SupplierReferenceInput input, CancellationToken ct)
    {
        await preparations.RecordSupplierReferenceAsync(batchId, input.Reference, ct);
        return NoContent();
    }

    [HttpPost("{batchId:guid}/items/{rowId:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid batchId, Guid rowId, ReconcilePreparationInput input, CancellationToken ct)
    {
        var batch = await preparations.ViewAsync(batchId, ct);
        if (!batch.Rows.Any(r => r.Id == rowId))
            return NotFound();
        jobs.Enqueue<OctoratePreparationService>(svc => svc.ReconcileAsync(batchId, rowId, input.AccommodationId, CancellationToken.None));
        return Accepted();
    }

    private void EnqueueRun(Guid batchId, string staff) =>
        jobs.Enqueue<OctoratePreparationService>(svc => svc.RunAsync(batchId, staff, CancellationToken.None));
}

public sealed record PreparationBatchInput([Required, MinLength(1), MaxLength(300)] IReadOnlyList<PreparationSelection> Properties);
public sealed record SupplierReferenceInput([Required, MaxLength(120)] string Reference);
public sealed record ReconcilePreparationInput([Required, MaxLength(64)] string AccommodationId);
