using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The «To do» of the scheduled messages (E05/F07/US01/T03, NF-71, AC-NF71-2), kept in one place for the dispatch and the service: ONE open item for each booking
/// (<see cref="ActivityType.ScheduledMessageIssue"/>, action <see cref="ActivityAction.FixScheduledMessage"/>, entity <c>Booking</c>), shared by its blocked messages because
/// <see cref="IInternalActivityService.CreateAsync"/> keeps one open item for each type and entity. It is raised when a message is blocked (or fails for good) and closed when no message of the
/// booking is blocked or failed for good any more: when the last one is sent, skipped, cancelled or expires, or the booking is cancelled. It carries the code of the reason and never a word of the message.
/// </summary>
internal static class ScheduledMessageTodos
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    /// <summary>
    /// Raises the item of the booking of <paramref name="row"/> (or finds the open one) and returns its id; <c>null</c> when the property is gone (deleted since: nothing to raise it on).
    /// </summary>
    internal static async Task<Guid?> RaiseAsync(IInternalActivityService activities, ScheduledMessage row, string reason, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.ScheduledMessageIssue,
                    Action = ActivityAction.FixScheduledMessage,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = row.BookingId,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = row.PropertyId,
                    OrgId = row.OrgId,
                    Reason = reason.Length <= TodoLimits.ReasonMaxLength ? reason : reason[..TodoLimits.ReasonMaxLength],
                },
                ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("The property of booking {BookingId} (org {OrgId}) is gone: no To do for its scheduled message", row.BookingId, row.OrgId);
            return null;
        }
    }

    /// <summary>
    /// Closes the open item of the booking unless another message of it (not <paramref name="exceptRowId"/>, the one that just left the trouble) is still blocked or failed for good. Idempotent: no item, or one
    /// closed, changes nothing.
    /// </summary>
    internal static async Task CloseIfIdleAsync(
        AppDbContext db,
        IInternalActivityService activities,
        Guid orgId,
        Guid bookingId,
        Guid exceptRowId,
        ILogger logger,
        CancellationToken ct)
    {
        var blocked = ScheduledMessageStatus.Blocked;
        var failed = ScheduledMessageStatus.Failed;
        var maxAttempts = ScheduledMessageRules.MaxSendAttempts;
        var stillTroubled = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .AnyAsync(
                m => m.OrgId == orgId && m.BookingId == bookingId && m.Id != exceptRowId && (m.Status == blocked || (m.Status == failed && m.Attempts >= maxAttempts)),
                ct);
        if (stillTroubled)
            return;

        var open = await activities.FindOpenAsync(orgId, nameof(ActivityAction.FixScheduledMessage), InternalActivity.BookingEntityType, bookingId, ct);
        if (open is null)
            return;

        try
        {
            await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("The To do of the scheduled messages of booking {BookingId} (org {OrgId}) was closed meanwhile", bookingId, orgId);
        }
    }
}
