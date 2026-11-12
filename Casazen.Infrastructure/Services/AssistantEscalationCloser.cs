using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A person answered a conversation (<see cref="IThreadReplyObserver"/>, E05/F05/US02/T01): the «To do» of the messages the assistant escalated in it are closed
/// (<see cref="IInternalActivityService.CloseAsync"/>), and their rows of the decision log say when (<see cref="AssistantDecisionLog.ActivityClosedAt"/>), so a «To do» never outlives the
/// answer. A «To do» a person already closed, or that is gone, is not an error. Ids only in the logs.
/// </summary>
public sealed class AssistantEscalationCloser(
    AppDbContext db,
    IInternalActivityService activities,
    ILogger<AssistantEscalationCloser> logger,
    TimeProvider? timeProvider = null) : IThreadReplyObserver
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task OnHostReplyAsync(Guid orgId, MessageThreadKey threadKey, CancellationToken ct)
    {
        var key = threadKey.ToString();
        var open = await db.AssistantDecisionLogs
            .Where(l => l.OrgId == orgId && l.ThreadKey == key && l.ActivityId != null && l.ActivityClosedAt == null)
            .ToListAsync(ct);
        if (open.Count == 0)
            return;

        foreach (var activityId in open.Select(l => l.ActivityId!.Value).Distinct())
        {
            try
            {
                await activities.CloseAsync(activityId, InternalActivity.ClosedBySystem, ct);
            }
            catch (NotFoundException)
            {
                logger.LogInformation("The To do {ActivityId} of an escalated message of org {OrgId} was not there any more: nothing to close", activityId, orgId);
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var row in open)
            row.ActivityClosedAt = now;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("A person answered conversation {ThreadKey} of org {OrgId}: {Count} escalated messages closed", key, orgId, open.Count);
    }
}
