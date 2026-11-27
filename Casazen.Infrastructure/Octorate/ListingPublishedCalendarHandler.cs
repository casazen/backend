using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The first publication of a listing reaches the calendar (E08/F05/US02/T02, NF-29, NF-36): the consumer of <see cref="ListingPublishedEvent"/>, which the content push
/// records once for a property when the channel manager has accepted its structure (E02/F03/US01/T02). It records a <see cref="CalendarChangeKind.Publication"/> change
/// for the whole horizon of the property (<c>Octorate:Calendar:HorizonDays</c>, 365 by default, chunked like any other window), so a new listing gets its prices, its
/// availability and its restrictions without a second action of the host.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Once for a publication.</b> The bus delivers an event at least once, and again when another handler of it failed. The id of the process is made from the id of
/// the event, so a second delivery finds the process and leaves, and two deliveries at once end on the primary key: the one that loses finds the process the other recorded.</item>
/// <item><b>Nothing while the connector is off.</b> What the recorder says about the property decides the rest: a property that is not on the channel manager records a process
/// that stays in CasaZen (<c>Applied</c>), a confirmed mapping without a room a process that says so (<c>octorate_room_not_chosen</c>), otherwise the chunks are sent.</item>
/// <item>The org is the event's (ADR-022-R6): the handler runs in a scope under it. Ids only are read from the event (ADR-022-R21).</item>
/// </list>
/// </remarks>
public sealed class ListingPublishedCalendarHandler(
    AppDbContext db,
    ICalendarChangeRecorder recorder,
    IFeatureFlags featureFlags,
    IOptions<OctorateCalendarOptions> options,
    TimeProvider timeProvider,
    ILogger<ListingPublishedCalendarHandler> logger,
    IBackgroundJobClient? jobs = null) : IEventHandler<ListingPublishedEvent>
{
    public async Task HandleAsync(ListingPublishedEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return;

        if (domainEvent.PropertyId == Guid.Empty)
        {
            logger.LogWarning("Event {EventId} ({EventType}) of org {OrgId} names no property: no calendar is sent", domainEvent.Id, domainEvent.Type, domainEvent.OrgId);
            return;
        }

        // One process for one publication: found again on a second delivery.
        var processId = ProcessIdOf(domainEvent.Id);
        if (await db.CalendarChangeProcesses.AnyAsync(p => p.Id == processId, ct))
            return;

        var first = timeProvider.TodayInRomeAsDateOnly();
        var window = new CalendarWindow(domainEvent.PropertyId, first, first.AddDays(options.Value.Horizon - 1));
        CalendarChangeRecord record;
        try
        {
            record = await recorder.RecordAsync(
                CalendarChangeKind.Publication,
                [window],
                null,
                JsonSerializer.Serialize(new { eventId = domainEvent.Id.ToString("N") }),
                ct,
                processId);
        }
        catch (NotFoundException)
        {
            // The property is gone (deleted since the publication): there is no calendar to send, and nothing will make it come back.
            logger.LogInformation("Property {PropertyId} of org {OrgId} is not found: its published calendar is not sent", domainEvent.PropertyId, domainEvent.OrgId);
            return;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another delivery of the same event recorded the process first (the key of the process is the same): it is done. Any other failure goes up.
            db.Entry(record.Process).State = EntityState.Detached;
            foreach (var chunk in record.Chunks)
                db.Entry(chunk).State = EntityState.Detached;

            if (await db.CalendarChangeProcesses.AnyAsync(p => p.Id == processId, ct))
                return;

            throw;
        }

        logger.LogInformation(
            "Calendar of property {PropertyId} of org {OrgId} recorded for its first publication: process {ProcessId}, state {State}",
            domainEvent.PropertyId,
            domainEvent.OrgId,
            processId,
            record.Process.State);
        if (record.NeedsRun)
            Enqueue(processId);
    }

    /// <summary>The id of the process of a publication: made from the id of the event, so the same event always gives the same process.</summary>
    internal static Guid ProcessIdOf(Guid eventId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("calendar-publication:" + eventId.ToString("N")));
        return new Guid(hash.AsSpan(0, 16));
    }

    private void Enqueue(Guid processId)
    {
        try
        {
            jobs?.Enqueue<CalendarChangeProcessJob>(job => job.RunAsync(processId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The process is saved and due: the recurring job sends it within a minute.
            logger.LogWarning(ex, "Calendar change {ProcessId} could not be enqueued: the recurring job sends it", processId);
        }
    }
}
