using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalRequestsController(AppDbContext db, IInternalActivityService activities, TimeProvider clock)
    : GuestPortalControllerBase
{
    [HttpGet("privacy/requests")]
    public async Task<IActionResult> PrivacyRequests(CancellationToken ct)
    {
        var portal = Portal;
        var requests = await db.InternalActivities.AsNoTracking()
            .Where(a => a.OrgId == portal.OrgId && a.PropertyId == portal.PropertyId
                && a.EntityType == InternalActivity.BookingEntityType && a.EntityId == portal.BookingId
                && (a.ActivityType == nameof(ActivityType.GuestDataCopyRequested)
                    || a.ActivityType == nameof(ActivityType.GuestDataErasureRequested)))
            .OrderByDescending(a => a.CreatedAt).Take(20)
            .Select(a => new { a.ActivityType, a.CreatedAt, a.Deadline, a.CompletedAt })
            .ToListAsync(ct);
        return Ok(requests);
    }

    [HttpPost("privacy/requests")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    [RequestSizeLimit(2048)]
    public async Task<IActionResult> RequestPrivacy(PrivacyRequest request, CancellationToken ct)
    {
        var type = request.Kind switch
        {
            "copy" => ActivityType.GuestDataCopyRequested,
            "erasure" => ActivityType.GuestDataErasureRequested,
            _ => (ActivityType?)null,
        };
        if (type is null)
            return BadRequest();

        await activities.CreateAsync(new CreateTodoRequest
        {
            Type = type.Value,
            Action = ActivityAction.ReviewBooking,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = Portal.BookingId,
            OrgId = Portal.OrgId,
            PropertyId = Portal.PropertyId,
            Urgency = ActivityUrgency.High,
            Deadline = clock.GetUtcNow().UtcDateTime.AddDays(30),
        }, ct);

        return Accepted();
    }

    [HttpPost("documents/{documentId:guid}/correction-request")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> RequestCorrection(Guid documentId, CancellationToken ct)
    {
        var portal = Portal;
        if (!await db.FiscalDocuments.AnyAsync(d => d.Id == documentId && d.OrgId == portal.OrgId
                && d.BookingId == portal.BookingId && d.Status == FiscalDocumentStatus.Issued
                && d.Source == FiscalDocumentSource.CasaZen && d.Purpose == FiscalDocumentPurpose.GuestStay
                && d.Kind == FiscalDocumentKind.Invoice, ct))
            return LinkInvalid();

        await activities.CreateAsync(new CreateTodoRequest
        {
            Type = ActivityType.GuestInvoiceCorrectionRequested,
            Action = ActivityAction.CorrectInvoice,
            EntityType = "FiscalDocument",
            EntityId = documentId,
            OrgId = portal.OrgId,
            PropertyId = portal.PropertyId,
            Urgency = ActivityUrgency.Medium,
        }, ct);

        return Accepted();
    }

    public sealed record PrivacyRequest([property: Required, MaxLength(16)] string Kind);
}
