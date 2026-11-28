using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

public enum StayGuestsAutoSendOutcome
{
    SettingOff = 0,

    ConnectorOff = 1,

    StayNotFound = 2,

    NotMirrored = 3,

    NotDue = 4,

    GuestsNotComplete = 5,

    IdentityNotVerified = 6,

    Sent = 7,
}

public sealed record StayGuestsAutoSendResult(StayGuestsAutoSendOutcome Outcome, OctorateCheckInSendResult? Send = null)
{
    public bool Attempted =>
        Outcome == StayGuestsAutoSendOutcome.Sent
        && Send is { State: OctorateCheckInSendState.Sent or OctorateCheckInSendState.PartlyFailed or OctorateCheckInSendState.Failed };
}

/// <summary>
/// Another send of the same stay holds its lock (the wizard's confirmation, the retry job, the sweep): the guests of the stay are being written by
/// someone else. What the event brought may not be in that send, so the event is not consumed: it is delivered again by the outbox dispatcher (ids only
/// in the message).
/// </summary>
public sealed class StayGuestsSendBusyException(Guid bookingId)
    : Exception($"The guests of booking {bookingId} are being written to Octorate by another send: delivered again later.");

public interface IStayGuestsAutoSendService
{
    /// <summary>
    /// Sends the guests of the stay if everything allows it, and does nothing otherwise (the outcome says why). Idempotent: the send path keeps the
    /// hash of what it sent, so a second call with nothing changed sends nothing. A stay of another org is never read. An unexpected error is thrown, so
    /// the outbox delivers the event again; so is <see cref="StayGuestsSendBusyException"/>, when another send of the same stay holds the lock: the
    /// event is never consumed with no effect.
    /// </summary>
    Task<StayGuestsAutoSendResult> TrySendAsync(Guid orgId, Guid bookingId, CancellationToken ct);

    /// <summary>
    /// The stays of the orgs that asked, mirrored on Octorate and due, whose arrival is from <see cref="StayGuestsAutoSendService.SweepLookbackDays"/> days
    /// back to <see cref="StayGuestsAutoSendService.SweepLookaheadDays"/> days ahead of today, that have a registered guest never written or changed since
    /// the last write: the stays whose event was missed (the setting turned on later, the listing mapped later). The first <paramref name="limit"/>, by
    /// arrival, among all of them: a stay with no registered guest, or whose guests were all tried after their last change, never takes a place. The
    /// caller spends its budget of sends on the stays that really went (<see cref="StayGuestsAutoSendResult.Attempted"/>), not on these places.
    /// </summary>
    Task<IReadOnlyList<(Guid OrgId, Guid BookingId)>> FindStaysToSweepAsync(int limit, CancellationToken ct);
}

/// <inheritdoc cref="IStayGuestsAutoSendService" />
/// <remarks>
/// <para><b>Ready</b> is what the wizard calls ready: no blocking issue of <see cref="CheckInReadinessRules"/> (the guests are complete and the head
/// carries a document, and the names Octorate needs of a complete guest are there), and, while <c>Features:CheckInIdentityVerification</c> is on, the
/// latest <see cref="BiometricVerificationLog"/> of the stay is an Accept (the agent's or an operator's, as the gate of the lock code reads it).</para>
/// <para><b>The same path.</b> <see cref="ICheckInOctorateSyncService.SendGuestsAsync"/>, which keeps every rule of the wizard's send: the sender must be
/// the channel manager for a send that is not a draft, the connection connected, the mapping confirmed, the stay due, one send at a time, the hash of
/// the payload, the link of each guest. A guest changed after it was sent goes again and raises the warning of a correction (a "To do", urgent: what
/// went to the police may need a correction on the portal).</para>
/// <para><b>Tenant.</b> Everything is read for the org of the event, never for the org the caller says: a stay of another org answers
/// <see cref="StayGuestsAutoSendOutcome.StayNotFound"/>. Logs carry ids and outcomes, never a name or a document.</para>
/// </remarks>
public sealed class StayGuestsAutoSendService(
    AppDbContext db,
    IOrgGuestAutoSendSetting setting,
    IStayGuestService stayGuests,
    ICheckInOctorateSyncService sync,
    IOctorateOrgScope orgScope,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    ILogger<StayGuestsAutoSendService> logger,
    TimeProvider? timeProvider = null) : IStayGuestsAutoSendService
{
    public const string CorrectionTodoReason = "octorate_guest_corrected_after_send";

    public const int SweepLookbackDays = 1;

    public const int SweepLookaheadDays = 14;

    public const int SweepPageSize = 200;

    public const int SweepMaxPages = 10;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<StayGuestsAutoSendResult> TrySendAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.ConnectorOff);

        if (orgId == Guid.Empty || bookingId == Guid.Empty || !await setting.GetAsync(orgId, ct))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.SettingOff);

        var booking = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.OrgId == orgId, ct);
        if (booking is null)
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.StayNotFound);

        if (string.IsNullOrWhiteSpace(booking.OctorateReservationId))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.NotMirrored);

        if (!AlloggiatiStatusRules.IsDue(booking))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.NotDue);

        var guests = (await stayGuests.GetForBookingAsync(booking, ct)).Where(g => g.Id != Guid.Empty).ToList();
        var issues = CheckInReadinessRules.Evaluate(new CheckInReadinessInput(
            guests, null, DateOnly.FromDateTime(booking.CheckInDate), booking.NumberOfGuests, OctorateReceivesGuests: true, []));
        if (CheckInReadinessRules.SendBlockers(issues).Count > 0)
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.GuestsNotComplete);

        if (featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification) && !await IdentityPassedAsync(orgId, booking.Id, ct))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.IdentityNotVerified);

        OctorateCheckInSendResult send;
        using (orgScope.Use(orgId))
            send = await sync.SendGuestsAsync(booking.Id, ct);

        // Another send of the stay holds the lock (the wizard's confirmation, the retry job, the sweep): what this event brought may not be in it, and the
        // link it leaves would then look as if it were (a change made while it ran is older than its link). The event is not consumed: it is delivered again.
        if (send.Reason == CheckInOctorateSyncService.ReasonSendInProgress)
            throw new StayGuestsSendBusyException(booking.Id);

        logger.LogInformation(
            "Guests of booking {BookingId} of org {OrgId} written to Octorate on their own: {State} {Reason}",
            booking.Id, orgId, send.State, send.Reason);
        if (send.CorrectionAfterSend)
            await RaiseCorrectionTodoAsync(booking, ct);

        return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.Sent, send);
    }

    public async Task<IReadOnlyList<(Guid OrgId, Guid BookingId)>> FindStaysToSweepAsync(int limit, CancellationToken ct)
    {
        if (limit < 1)
            return [];

        var orgIds = await db.Orgs
            .AsNoTracking()
            .Where(o => o.AutoSendGuestsToOctorate)
            .Select(o => o.Id)
            .ToListAsync(ct);
        if (orgIds.Count == 0)
            return [];

        var today = _clock.TodayInRome();
        var from = today.AddDays(-SweepLookbackDays);
        var to = today.AddDays(SweepLookaheadDays);
        var found = new List<(Guid OrgId, Guid BookingId)>();

        for (var page = 0; page < SweepMaxPages && found.Count < limit; page++)
        {
            var stays = await db.Bookings
                .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AsNoTracking()
                .Where(b => orgIds.Contains(b.OrgId)
                            && b.OctorateReservationId != null
                            && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                            && b.NoShowAt == null
                            && b.CheckInDate >= from
                            && b.CheckInDate <= to)
                .OrderBy(b => b.CheckInDate)
                .ThenBy(b => b.Id)
                .Select(b => new { b.Id, b.OrgId })
                .Skip(page * SweepPageSize)
                .Take(SweepPageSize)
                .ToListAsync(ct);
            if (stays.Count == 0)
                break;

            var ids = stays.Select(s => s.Id).ToList();
            var guestRows = (await db.StayGuests
                    .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                    .AsNoTracking()
                    .Where(g => ids.Contains(g.BookingId))
                    .Select(g => new { g.BookingId, g.Position, g.UpdatedAt })
                    .ToListAsync(ct))
                .ToLookup(g => g.BookingId);
            var linkRows = (await db.StayGuestOctorateLinks
                    .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                    .AsNoTracking()
                    .Where(l => ids.Contains(l.BookingId))
                    .Select(l => new { l.BookingId, l.Position, l.UpdatedAt })
                    .ToListAsync(ct))
                .ToLookup(l => l.BookingId);

            foreach (var stay in stays)
            {
                var links = linkRows[stay.Id].ToDictionary(l => l.Position, l => l.UpdatedAt);
                if (!guestRows[stay.Id].Any(g => !links.TryGetValue(g.Position, out var touched) || g.UpdatedAt > touched))
                    continue;

                found.Add((stay.OrgId, stay.Id));
                if (found.Count >= limit)
                    break;
            }

            if (stays.Count < SweepPageSize)
                break;
        }

        return found;
    }

    private async Task<bool> IdentityPassedAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        var latest = await db.BiometricVerificationLogs
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(l => l.OrgId == orgId && l.BookingId == bookingId)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => (BiometricOutcome?)l.Outcome)
            .FirstOrDefaultAsync(ct);
        return latest == BiometricOutcome.Accept;
    }

    /// <summary>
    /// What was sent to the channel manager (and by it to the police) changed after the send: the host may have to correct it on the Alloggiati portal.
    /// One urgent "To do" on the booking; when another "To do" about the integration is open on it, that one is the item, raised to urgent with this reason.
    /// </summary>
    private async Task RaiseCorrectionTodoAsync(Booking booking, CancellationToken ct)
    {
        try
        {
            var id = await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.IntegrationError,
                    Action = ActivityAction.ReviewBooking,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = booking.Id,
                    Urgency = ActivityUrgency.High,
                    PropertyId = booking.PropertyId,
                    OrgId = booking.OrgId,
                    Reason = CorrectionTodoReason,
                },
                ct);
            await activities.RaiseUrgencyAsync(id, ActivityUrgency.High, null, CorrectionTodoReason, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or ArgumentException)
        {
            logger.LogWarning(ex, "The To do about the correction of the guests of booking {BookingId} could not be raised", booking.Id);
        }
    }
}
