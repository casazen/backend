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

/// <summary>What <see cref="IStayGuestsAutoSendService.TrySendAsync"/> did for a stay.</summary>
public enum StayGuestsAutoSendOutcome
{
    /// <summary>The org did not ask for it (the default): nothing changes, the guests go only when the host confirms the check-in wizard.</summary>
    SettingOff = 0,

    /// <summary><c>Features:OctorateConnector</c> is off.</summary>
    ConnectorOff = 1,

    /// <summary>There is no such stay in the org (another org's stay, or none): nothing was read of it, nothing was sent.</summary>
    StayNotFound = 2,

    /// <summary>The stay is not on Octorate.</summary>
    NotMirrored = 3,

    /// <summary>The stay owes no communication (a request still waiting, a cancelled booking, a no-show).</summary>
    NotDue = 4,

    /// <summary>The data of the guests is not complete yet: nothing is sent until it is.</summary>
    GuestsNotComplete = 5,

    /// <summary>The identity check is on and the latest verification of the stay is not a pass: no guest leaves before it.</summary>
    IdentityNotVerified = 6,

    /// <summary>The guests went through the send path of the check-in wizard: <see cref="StayGuestsAutoSendResult.Send"/> says what happened.</summary>
    Sent = 7,
}

/// <summary>The answer of <see cref="IStayGuestsAutoSendService.TrySendAsync"/>.</summary>
/// <param name="Outcome">What was done.</param>
/// <param name="Send">The send of the guests, only with <see cref="StayGuestsAutoSendOutcome.Sent"/>.</param>
public sealed record StayGuestsAutoSendResult(StayGuestsAutoSendOutcome Outcome, OctorateCheckInSendResult? Send = null);

/// <summary>
/// The guests of a stay written to the channel manager on their own (E20/F01/US01/T01, decision O1): for an org that asked for it, the stay whose
/// guest data is complete and whose identity check passed goes through the same send path as the confirmation of the check-in wizard.
/// </summary>
public interface IStayGuestsAutoSendService
{
    /// <summary>
    /// Sends the guests of the stay if everything allows it, and does nothing otherwise (the outcome says why). Idempotent: the send path keeps the
    /// hash of what it sent, so a second call with nothing changed sends nothing. A stay of another org is never read. An unexpected error is thrown, so
    /// the outbox delivers the event again.
    /// </summary>
    Task<StayGuestsAutoSendResult> TrySendAsync(Guid orgId, Guid bookingId, CancellationToken ct);

    /// <summary>
    /// The stays of the orgs that asked, mirrored on Octorate and due, in the window of <see cref="StayGuestsAutoSendService.SweepLookbackDays"/> to
    /// <see cref="StayGuestsAutoSendService.SweepLookaheadDays"/> days around today, that have a registered guest never written or changed since the
    /// last write: the stays whose event was missed (the setting turned on later, the listing mapped later). At most <paramref name="limit"/>, by arrival.
    /// A stay whose guests were all tried after their last change is not there again: once for each stay and change.
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
    /// <summary>The reason code of the "To do" raised when a guest already sent to the channel manager is corrected.</summary>
    public const string CorrectionTodoReason = "octorate_guest_corrected_after_send";

    /// <summary>The days back from today the sweep looks at the stays (a stay that ended long ago is not chased).</summary>
    public const int SweepLookbackDays = 1;

    /// <summary>The days ahead of today the sweep looks at the stays (the guests of a stay far ahead are not ready yet).</summary>
    public const int SweepLookaheadDays = 14;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<StayGuestsAutoSendResult> TrySendAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.ConnectorOff);

        if (orgId == Guid.Empty || bookingId == Guid.Empty || !await setting.GetAsync(orgId, ct))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.SettingOff);

        // The stay of THIS org: another org's stay is read by nothing here, whoever named it.
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

        // The registered guests only: the booker shown before any guest is registered is not a guest to send.
        var guests = (await stayGuests.GetForBookingAsync(booking, ct)).Where(g => g.Id != Guid.Empty).ToList();
        var issues = CheckInReadinessRules.Evaluate(new CheckInReadinessInput(
            guests, null, DateOnly.FromDateTime(booking.CheckInDate), booking.NumberOfGuests, OctorateReceivesGuests: true, []));
        if (issues.Any(i => i.Blocking))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.GuestsNotComplete);

        // The privacy rule of the identity check: while it is on, no guest leaves before a passed verification (the latest one, as the gate of the
        // lock code reads it). A rejection, or the row that asks for a new photo, is not a pass.
        if (featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification) && !await IdentityPassedAsync(orgId, booking.Id, ct))
            return new StayGuestsAutoSendResult(StayGuestsAutoSendOutcome.IdentityNotVerified);

        OctorateCheckInSendResult send;
        using (orgScope.Use(orgId))
            send = await sync.SendGuestsAsync(booking.Id, ct);

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
        var stays = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => orgIds.Contains(b.OrgId)
                        && b.OctorateReservationId != null
                        && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                        && b.NoShowAt == null
                        && b.CheckInDate <= to
                        && b.CheckOutDate >= from)
            .OrderBy(b => b.CheckInDate)
            .Select(b => new { b.Id, b.OrgId })
            .Take(limit * 4)
            .ToListAsync(ct);
        if (stays.Count == 0)
            return [];

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

        // A stay is to sweep when a registered guest has no link (never written) or changed after its link was last touched (a write attempt, kept
        // or failed, is the "once": the next one waits for a new change).
        return stays
            .Where(s =>
            {
                var links = linkRows[s.Id].ToDictionary(l => l.Position, l => l.UpdatedAt);
                return guestRows[s.Id].Any(g => !links.TryGetValue(g.Position, out var touched) || g.UpdatedAt > touched);
            })
            .Take(limit)
            .Select(s => (s.OrgId, s.Id))
            .ToList();
    }

    /// <summary>The latest row of the verification log of the stay is an Accept, whoever decided it.</summary>
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
