using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Alerts;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>
/// The popup alerts of the caller (ADR-025, NF-08): the queue the frontend shows one alert at a time, and the two answers to an
/// alert, «dismiss» and «remind me later». The alerts are raised and pushed by the system through <see cref="Casazen.Core.Services.IAlertRouter"/>;
/// here they are only read and answered.
/// </summary>
/// <remarks>
/// <para>The alerts are the caller's own, in every host context, so the policy is <see cref="CasazenPolicies.Authenticated"/> and the user
/// is always the token's <c>sub</c>, never a parameter. What the caller reaches is the alerts of its own org narrowed to its
/// <see cref="HostScope"/>, resolved from its org membership in the database (<see cref="IHostScopeResolver"/>), never from the request:
/// an owner, an administrator, a manager or a collaborator with every property sees the alerts of the org; a collaborator «Solo alcuni» only
/// the ones about a booking or a property it was given, never one about anything else (ADR-025-R4). A caller with no org or no scope gets an
/// empty list. The id of an alert of somebody else, or of another org, or one that does not exist, is a 404, never a 403, the same answer as
/// the in-app notifications give.</para>
/// <para>The answer is <see cref="PendingAlertDto"/>: references only, no name and no text (ADR-025-R5). It is private and never cached
/// (<c>Cache-Control: private, no-store</c>): the frontend polls it when the SignalR connection is down.</para>
/// </remarks>
[ApiController]
[Route("api/me/alerts")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class AlertsController(
    AppDbContext db,
    IHostScopeResolver hostScopeResolver,
    IOrgContextResolver orgContextResolver,
    TimeProvider timeProvider) : ControllerBase
{
    public const string CannotSnoozePastDeadlineCode = "cannot_snooze_past_deadline";

    public const string CannotSnoozePastDeadlineMessageKey = "PopupAlertCannotSnoozePastDeadline";

    [HttpGet("pending")]
    [ProducesResponseType(typeof(IReadOnlyList<PendingAlertDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<PendingAlertDto>>> GetPending(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Ok(Array.Empty<PendingAlertDto>());

        var orgId = scope.OrgId;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var pending = db.PopupAlerts
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.UserId == userId
                        && a.DismissedAt == null
                        && (a.RemindAt == null || a.RemindAt <= now));

        if (!scope.IsOrgWide)
        {
            var bookings = db.Bookings.Where(b => b.OrgId == orgId).InScope(scope);
            var properties = db.Properties.Where(p => p.OrgId == orgId).InScope(scope);
            pending = pending.Where(a =>
                (a.EntityType == PopupAlert.BookingEntityType && bookings.Any(b => b.Id == a.EntityId))
                || (a.EntityType == PopupAlert.PropertyEntityType && properties.Any(p => p.Id == a.EntityId)));
        }

        var alerts = await pending
            .OrderByDescending(a => a.Urgency)
            .ThenBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);

        return Ok(alerts.Select(PendingAlertDto.From).ToList());
    }

    /// <summary>
    /// Dismisses one alert of the caller (<c>DismissedAt</c> = now): it is never pending again. Idempotent: an alert already dismissed
    /// answers 204 too and keeps the first instant. The body, if any, is ignored.
    /// </summary>
    /// <response code="204">Dismissed (or already dismissed).</response>
    /// <response code="404">The caller has no such alert: another user's, another org's or unknown.</response>
    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Dismiss(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DismissRequest? request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var alert = await OwnAlertAsync(id, userId, cancellationToken);
        if (alert.DismissedAt is null)
        {
            alert.DismissedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    [HttpPatch("{id:guid}/remind")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remind(Guid id, [FromBody] RemindRequest request, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        if (request?.RemindAt is not { } requested)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "PopupAlertRemindAtRequired");

        var alert = await OwnAlertAsync(id, userId, cancellationToken);
        if (alert.DismissedAt is not null)
            return NoContent();

        var remindAt = requested.UtcDateTime;
        if (alert.Deadline is { } deadline && remindAt > deadline)
            return this.ApiProblem(StatusCodes.Status400BadRequest, CannotSnoozePastDeadlineCode, CannotSnoozePastDeadlineMessageKey);

        alert.RemindAt = remindAt;
        alert.DeliveredAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<PopupAlert> OwnAlertAsync(Guid id, string userId, CancellationToken cancellationToken)
    {
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
            throw new NotFoundException("Alert not found.");

        return await db.PopupAlerts.FirstOrDefaultAsync(a => a.Id == id && a.OrgId == orgId && a.UserId == userId, cancellationToken)
               ?? throw new NotFoundException("Alert not found.");
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
