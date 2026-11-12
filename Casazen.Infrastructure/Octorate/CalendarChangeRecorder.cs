using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="ICalendarChangeRecorder"/> (E08/F05/US02/T01, NF-36, NF-78): adds the process of a calendar change and its chunks to the unit of work of the caller,
/// never saves them. See the interface for the rules; nothing here calls the channel manager but the lookup of where a property books.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only a property that books into the channel manager.</b> <see cref="IOctorateBookingTargets.LookupAsync"/> decides: not connected, a mapping that
/// is not confirmed or is write-blocked, or the connector off, and the property gets no chunk (its change stays in CasaZen). A confirmed mapping with no
/// room chosen gets <c>Failed</c> chunks with <c>octorate_room_not_chosen</c>, so the host sees why nothing was sent.</item>
/// <item><b>A lookup that cannot be answered is not a reason to refuse the change.</b> The channel manager not answering (the lookup reads its rooms) records the
/// chunks <c>Pending</c> with a line in the log (the code, never a body): the push asks again and fails the chunk by its own rules, so nothing is lost and nothing is
/// swallowed.</item>
/// <item><b>Today of the property.</b> The days before it are never sent (<c>DayRuleRules.TodayOf</c> on the injected clock).</item>
/// <item><b>Logs</b> carry ids and codes only.</item>
/// </list>
/// </remarks>
public sealed class CalendarChangeRecorder(
    AppDbContext db,
    IOctorateBookingTargets targets,
    IFeatureFlags featureFlags,
    IOptions<OctorateCalendarOptions> options,
    TimeProvider timeProvider,
    ILogger<CalendarChangeRecorder> logger) : ICalendarChangeRecorder
{
    public async Task<CalendarChangeRecord> RecordAsync(
        CalendarChangeKind kind,
        IReadOnlyCollection<CalendarWindow> windows,
        string? userId,
        string? summaryJson,
        CancellationToken cancellationToken = default,
        Guid? processId = null,
        IReadOnlyDictionary<Guid, CalendarSendOutcome>? resolved = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
            throw new ArgumentException("A change of the calendar names at least one window.", nameof(windows));

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of change of the calendar.");

        if (windows.Any(w => w.PropertyId == Guid.Empty || w.To < w.From))
            throw new ArgumentException("A window of a change of the calendar names a property and does not end before it starts.", nameof(windows));

        var propertyIds = windows.Select(w => w.PropertyId).Distinct().ToList();
        var properties = await db.Properties
            .AsNoTracking()
            .Where(p => propertyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.OrgId, p.Timezone })
            .ToListAsync(cancellationToken);
        if (properties.Count != propertyIds.Count)
        {
            throw new NotFoundException("Property not found")
            {
                Code = ManualBlockErrorCodes.PropertyNotFound,
                MessageKey = ManualBlockErrorCodes.PropertyNotFoundMessageKey,
            };
        }

        var orgs = properties.Select(p => p.OrgId).Distinct().ToList();
        if (orgs.Count != 1)
            throw new ArgumentException("The properties of a change of the calendar belong to one org.", nameof(windows));

        var orgId = orgs[0];
        var now = timeProvider.GetUtcNow();
        var stamp = now.UtcDateTime;
        var settings = options.Value;

        // The summary first: an invalid one is the caller's bug, found before anything is looked up or added.
        var summary = CalendarChangeSummary.Build(windows, summaryJson);

        // The days before today of each property are never sent: a window starts today, one that is over is dropped.
        var planned = new List<CalendarWindow>();
        foreach (var window in windows)
        {
            var today = DayRuleRules.TodayOf(properties.First(p => p.Id == window.PropertyId).Timezone, now);
            if (CalendarChangeRules.PushWindow(today, window.From, window.To) is { } cut)
                planned.Add(new CalendarWindow(window.PropertyId, cut.From, cut.To));
        }

        // Every lookup before anything is added: one that stores the only room of a mapping saves the unit of work (see the remarks of the interface). A property the
        // caller already asked about (before its lock or its transaction) is not asked again.
        var outcomes = new Dictionary<Guid, CalendarSendOutcome>();
        foreach (var propertyId in planned.Select(w => w.PropertyId).Distinct())
        {
            outcomes[propertyId] = resolved is not null && resolved.TryGetValue(propertyId, out var known)
                ? known
                : await ResolveAsync(propertyId, cancellationToken);
        }

        var id = processId ?? Guid.NewGuid();
        var chunks = new List<CalendarChangeChunk>();
        foreach (var window in planned)
        {
            var outcome = outcomes[window.PropertyId];
            if (outcome == CalendarSendOutcome.NotMirrored)
                continue;

            foreach (var range in CalendarChunkPlanner.Split(window.From, window.To, settings.MaxDays))
            {
                var failed = outcome == CalendarSendOutcome.RoomNotChosen;
                chunks.Add(new CalendarChangeChunk
                {
                    OrgId = orgId,
                    ProcessId = id,
                    PropertyId = window.PropertyId,
                    FromDate = range.From,
                    ToDate = range.To,
                    State = failed ? CalendarChunkState.Failed : CalendarChunkState.Pending,
                    ErrorCode = failed ? CalendarPushErrorCodes.RoomNotChosen : null,
                    UpdatedAt = stamp,
                });
            }
        }

        var states = chunks.Select(c => c.State).ToList();
        var state = CalendarPushRules.Derive(states);
        var process = new CalendarChangeProcess
        {
            Id = id,
            OrgId = orgId,
            Kind = kind,
            State = state,
            RequestedByUserId = Clip(userId),
            CreatedAt = stamp,
            FinishedAt = CalendarPushRules.IsSettled(states) ? stamp : null,
            PropertyCount = propertyIds.Count,
            DayCount = planned.Sum(w => w.Days),
            SummaryJson = summary,
            QuotaUnitsEstimated = chunks
                .Where(c => c.State == CalendarChunkState.Pending)
                .Sum(c => CalendarChunkPlanner.EstimateQuotaUnits(c.ToDate.DayNumber - c.FromDate.DayNumber + 1, settings.RoomDaysPerUnit)),
            ErrorCode = state == CalendarChangeState.Failed ? chunks.First(c => c.State == CalendarChunkState.Failed).ErrorCode : null,
            NextAttemptAt = state == CalendarChangeState.Queued ? stamp : null,
        };

        // Added through the sets: rows with a preset id added any other way are tracked as modified and fail to save.
        db.CalendarChangeProcesses.Add(process);
        foreach (var chunk in chunks)
            db.CalendarChangeChunks.Add(chunk);

        logger.LogInformation(
            "Calendar change {ProcessId} ({Kind}) of org {OrgId} recorded: {Properties} properties, {Chunks} chunks, state {State}",
            id,
            kind,
            orgId,
            propertyIds.Count,
            chunks.Count,
            state);
        return new CalendarChangeRecord(process, chunks);
    }

    public async Task<CalendarSendOutcome> ResolveAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        // The connector off asks nothing, as everything else of the connector: no property is on the channel manager.
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return CalendarSendOutcome.NotMirrored;

        try
        {
            var lookup = await targets.LookupAsync(propertyId, cancellationToken);
            return lookup.Gap switch
            {
                OctorateTargetGap.None => CalendarSendOutcome.Send,
                OctorateTargetGap.RoomNotChosen => CalendarSendOutcome.RoomNotChosen,
                _ => CalendarSendOutcome.NotMirrored,
            };
        }
        catch (OctorateException ex)
        {
            // Only a property with a confirmed mapping reads its rooms from the channel manager: it is on it, and the push asks again.
            logger.LogWarning(
                "The channel manager could not be asked about property {PropertyId} ({Code}, HTTP {Status}): its change is recorded to be sent",
                propertyId,
                ex.Code,
                ex.OctorateStatusCode);
            return CalendarSendOutcome.Send;
        }
        catch (ArgumentException ex)
        {
            // An id of the mapping the client refuses to put in an address: nothing can be sent for this property.
            logger.LogWarning(ex, "The channel manager cannot be asked about property {PropertyId}: its change is not sent", propertyId);
            return CalendarSendOutcome.NotMirrored;
        }
    }

    private static string? Clip(string? userId) =>
        userId is { Length: > CalendarChangeProcess.UserIdMaxLength } ? userId[..CalendarChangeProcess.UserIdMaxLength] : userId;
}
