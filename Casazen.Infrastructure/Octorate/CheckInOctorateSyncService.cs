using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Octorate;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>The stable codes of a failed check-in write to Octorate, from the typed Octorate errors (they never reach the caller).</summary>
public static class CheckInErrorCodes
{
    public const string Auth = "octorate_auth";
    public const string RateLimited = "octorate_rate_limited";
    public const string Conflict = "octorate_conflict";
    public const string NotFound = "octorate_not_found";
    public const string Server = "octorate_server";
    public const string GuestRejected = "octorate_guest_rejected";

    /// <summary>The code of a typed Octorate error.</summary>
    public static string Of(OctorateException exception) => exception switch
    {
        OctorateAuthException or OctorateWrongEnvironmentException => Auth,
        OctorateRateLimitException => RateLimited,
        OctorateConflictException => Conflict,
        OctorateNotFoundException => NotFound,
        OctorateRequestRejectedException => GuestRejected,
        _ => Server,
    };
}

/// <inheritdoc />
/// <remarks>
/// <para><b>Mirrored only.</b> With <c>Features:OctorateConnector</c> off, or a booking with no reservation id, an org with no connected connection
/// of this environment, a property with no confirmed mapping or with writes blocked, nothing is written and no row is created.</para>
/// <para><b>The sender.</b> Only a property whose sender is the channel manager sends its guests (<c>draft = false</c>: Octorate transmits to the
/// police). Another sender sends nothing while <c>Octorate:SendGuestsWhenSenderIsNotChannelManager</c> is off, because the sandbox ignores
/// <c>draft</c>: a guest sent as a draft could still be transmitted, twice with <c>autorun</c>. With the switch on such guests go as drafts.
/// A change of sender never sends by itself.</para>
/// <para><b>One at a time.</b> A session lock per booking for its guests, and per booking and state for a push: two sends of the same booking
/// never create the same guest twice; a send that finds the lock taken answers <c>send_in_progress</c>. The attempt is on record
/// (<see cref="OctorateSyncStatus.Pending"/>) before the call and each Octorate answer is saved at once, so an id Octorate gave is never lost
/// and a send cut short is taken again by the retry job.</para>
/// <para><b>Stays that owe no communication.</b> Only a confirmed, checked-in or checked-out stay that is not a no-show sends its guests
/// (<see cref="AlloggiatiStatusRules.IsDue"/>, the set of the Alloggiati job); a request still waiting, a cancelled booking and a no-show send
/// none (<c>booking_not_active</c>), a retry included.</para>
/// <para><b>Failures.</b> A typed Octorate error is a stable code on the row; the attempts are counted per payload and, at
/// <c>Octorate:CheckInMaxAttempts</c>, one "To do" is raised for the booking (closed when a later attempt succeeds). Logs carry ids and codes,
/// never a name or a document.</para>
/// </remarks>
public sealed class CheckInOctorateSyncService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IStayGuestService stayGuests,
    IPropertySenderStore senders,
    IChannelMappingService channelMapping,
    IStayTouristTaxService stayTouristTax,
    IInternalActivityService activities,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    IOptions<OctorateOptions> options,
    ILogger<CheckInOctorateSyncService> logger,
    TimeProvider? timeProvider = null) : ICheckInOctorateSyncService
{
    /// <summary>The reason code of a "To do" raised for a check-in that keeps failing.</summary>
    public const string TodoReason = "octorate_checkin_failed";

    public const string ReasonConnectorOff = "connector_off";
    public const string ReasonNoReservation = "no_reservation";
    public const string ReasonNotConnected = "not_connected";
    public const string ReasonMappingNotConfirmed = "mapping_not_confirmed";
    public const string ReasonWritesBlocked = "writes_blocked";
    public const string ReasonSenderNotApplicable = "sender_not_applicable";
    public const string ReasonSenderNotChannelManager = "sender_not_channel_manager";
    public const string ReasonNoGuests = "no_guests";
    public const string ReasonSendInProgress = "send_in_progress";
    public const string ReasonBookingNotActive = "booking_not_active";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The attempts of a payload or a state before a "To do" is raised (1 to 20; default 5).</summary>
    public int MaxAttempts => Math.Clamp(options.Value.CheckInMaxAttempts, 1, 20);

    public async Task<OctorateCheckInSendResult> SendGuestsAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var target = await ResolveTargetAsync(booking, cancellationToken);
        if (target.Reason is { } notMirrored)
            return Nothing(OctorateCheckInSendState.NotMirrored, notMirrored);

        // Police data leaves only for a stay that owes a communication: confirmed, checked in or checked out, never a no-show (the one definition
        // of the Alloggiati job). A request still waiting (WAITING on Octorate), a cancelled booking and a no-show send nothing, a retry included.
        if (!AlloggiatiStatusRules.IsDue(booking))
            return Nothing(OctorateCheckInSendState.NotApplicable, ReasonBookingNotActive);

        PropertySenderInfo sender;
        try
        {
            sender = await senders.GetAsync(booking.PropertyId, cancellationToken);
        }
        catch (NotFoundException)
        {
            return Nothing(OctorateCheckInSendState.NotMirrored, ReasonMappingNotConfirmed);
        }

        if (!sender.Applies)
            return Nothing(OctorateCheckInSendState.NotApplicable, ReasonSenderNotApplicable);

        var draft = OctorateCheckInMapper.DraftFor(sender.AlloggiatiSender);
        if (draft && !options.Value.SendGuestsWhenSenderIsNotChannelManager)
            return Nothing(OctorateCheckInSendState.NotApplicable, ReasonSenderNotChannelManager);

        await using var sessionLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.OctorateCheckInSync, GuestsLockKey(booking.Id), cancellationToken);
        if (sessionLock is null)
            return new OctorateCheckInSendResult(OctorateCheckInSendState.NothingToSend, ReasonSendInProgress, draft, [], false);

        // The registered guests only: the booker shown before any guest is registered (the unsaved fallback) is not sent.
        var guests = (await stayGuests.GetForBookingAsync(booking, cancellationToken)).Where(g => g.Id != Guid.Empty).ToList();
        var links = await db.StayGuestOctorateLinks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(l => l.OrgId == booking.OrgId && l.BookingId == booking.Id)
            .ToListAsync(cancellationToken);
        if (guests.Count == 0 && links.Count == 0)
            return new OctorateCheckInSendResult(OctorateCheckInSendState.NothingToSend, ReasonNoGuests, draft, [], false);

        var tax = await TaxValuesAsync(booking, cancellationToken);
        var checkIn = RomeCalendar.DateInRome(booking.CheckInDate);
        var checkOut = RomeCalendar.DateInRome(booking.CheckOutDate);
        var now = UtcNow();

        var results = new List<OctorateGuestSendResult>();
        var toSend = new List<(OctorateCheckInGuestUpsert Upsert, StayGuestOctorateLink Link)>();
        var correction = false;
        foreach (var guest in guests)
        {
            var link = links.FirstOrDefault(l => l.Position == guest.Position);
            var upsert = OctorateCheckInMapper.Map(
                guest, checkIn, checkOut, draft, link?.OctorateGuestId, tax.GetValueOrDefault(guest.Position));
            var hash = OctorateCheckInMapper.Hash(upsert);
            if (link is { Status: OctorateSyncStatus.Sent } && link.PayloadHash == hash)
            {
                results.Add(new OctorateGuestSendResult(guest.Position, OctorateGuestSendOutcome.Unchanged, null));
                continue;
            }

            // AC-NF41-3: data already transmitted (a guest sent with draft = false) changes: the host is told it may need a correction.
            if (link is { Status: OctorateSyncStatus.Sent, SentAsDraft: false, OctorateGuestId: not null })
                correction = true;
            toSend.Add((upsert, MarkPending(booking, link, guest.Position, hash, now)));
        }

        // The attempts are on record before the call: a send cut short (a crash, a timeout) stays Pending and the retry job takes it again.
        if (toSend.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        var removedPositions = links.Where(l => guests.All(g => g.Position != l.Position)).ToList();
        var failures = 0;
        var successes = 0;
        using (orgScope.Use(booking.OrgId))
        {
            foreach (var gone in removedPositions)
            {
                if (await RemoveGuestAsync(booking, target.AccommodationId!, gone, cancellationToken))
                    successes++;
                else
                    failures++;
            }

            if (toSend.Count > 0)
            {
                var answers = await client.UpsertCheckInGuestsAsync(
                    target.AccommodationId!, booking.OctorateReservationId!, toSend.Select(s => s.Upsert).ToList(), cancellationToken);
                foreach (var (upsert, link) in toSend)
                {
                    var answer = answers.FirstOrDefault(a => a.Position == upsert.Position)
                        ?? new OctorateCheckInGuestResult(upsert.Position, null, false, null, CheckInErrorCodes.Server);
                    var outcome = await RecordGuestAsync(booking, link, upsert, answer, cancellationToken);
                    results.Add(outcome);
                    if (outcome.Outcome == OctorateGuestSendOutcome.Failed)
                        failures++;
                    else
                        successes++;
                }
            }
        }

        var state = (successes, failures) switch
        {
            (0, 0) => OctorateCheckInSendState.NothingToSend,
            (_, 0) => OctorateCheckInSendState.Sent,
            (0, _) => OctorateCheckInSendState.Failed,
            _ => OctorateCheckInSendState.PartlyFailed,
        };
        logger.LogInformation(
            "Check-in guests of booking {BookingId} sent to Octorate: {State}, {Sent} accepted, {Failed} failed, draft {Draft}",
            booking.Id, state, successes, failures, draft);
        return new OctorateCheckInSendResult(state, null, draft, results.OrderBy(r => r.Position).ToList(), correction);
    }

    public async Task<OctorateStayStateResult> SetStayStateAsync(
        Guid bookingId,
        OctorateStayState state,
        CancellationToken cancellationToken)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var target = await ResolveTargetAsync(booking, cancellationToken);
        if (target.Reason is not null)
            return new OctorateStayStateResult(OctorateStayStateOutcome.NotMirrored, null);

        // Another push of the same state is running: it records the outcome, nothing is lost.
        await using var sessionLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.OctorateCheckInSync, $"{booking.Id:N}:state:{(int)state}", cancellationToken);
        if (sessionLock is null)
            return new OctorateStayStateResult(OctorateStayStateOutcome.Failed, ReasonSendInProgress);

        var row = await db.OctorateStayStateSyncs
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(s => s.OrgId == booking.OrgId && s.BookingId == booking.Id && s.State == state, cancellationToken);
        if (row is { Status: OctorateSyncStatus.Sent })
            return new OctorateStayStateResult(OctorateStayStateOutcome.AlreadySent, null);

        if (row is null)
        {
            row = new OctorateStayStateSync { OrgId = booking.OrgId, BookingId = booking.Id, State = state };
            db.OctorateStayStateSyncs.Add(row);
        }

        // The attempt is on record before the call: a push cut short stays Pending and the retry job takes it again.
        var now = UtcNow();
        row.Attempts++;
        row.LastAttemptAt = now;
        row.Status = OctorateSyncStatus.Pending;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            using (orgScope.Use(booking.OrgId))
                await client.SetCheckInStatusAsync(target.AccommodationId!, booking.OctorateReservationId!, StatusOf(state), cancellationToken);
        }
        catch (OctorateException ex)
        {
            row.Status = OctorateSyncStatus.Failed;
            row.ErrorCode = CheckInErrorCodes.Of(ex);
            row.TodoActivityId ??= row.Attempts >= MaxAttempts ? await RaiseTodoAsync(booking, cancellationToken) : null;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "State {State} of booking {BookingId} not accepted by Octorate on attempt {Attempt}: {ErrorCode}",
                state, booking.Id, row.Attempts, row.ErrorCode);
            return new OctorateStayStateResult(OctorateStayStateOutcome.Failed, row.ErrorCode);
        }

        row.Status = OctorateSyncStatus.Sent;
        row.SentAt = now;
        row.ErrorCode = null;
        row.TodoActivityId = await CloseTodoAsync(row.TodoActivityId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (state == OctorateStayState.CheckInEnd)
            await ReadArrivalAsync(booking, target.AccommodationId!, row, cancellationToken);

        logger.LogInformation("State {State} of booking {BookingId} sent to Octorate", state, booking.Id);
        return new OctorateStayStateResult(OctorateStayStateOutcome.Sent, null);
    }

    public async Task<OctorateCheckInView> GetStatusAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var target = await ResolveTargetAsync(booking, cancellationToken);
        if (target.Reason is not null)
            return new OctorateCheckInView(false, [], []);

        var links = await db.StayGuestOctorateLinks
            .AsNoTracking()
            .Where(l => l.OrgId == booking.OrgId && l.BookingId == booking.Id)
            .OrderBy(l => l.Position)
            .ToListAsync(cancellationToken);
        var rows = await db.OctorateStayStateSyncs
            .AsNoTracking()
            .Where(s => s.OrgId == booking.OrgId && s.BookingId == booking.Id)
            .ToListAsync(cancellationToken);

        var states = Enum.GetValues<OctorateStayState>()
            .Select(state => rows.FirstOrDefault(r => r.State == state) is { } row
                ? new OctorateStayStateView(state, row.Status, row.Attempts, row.ErrorCode, row.SentAt, row.OctorateEffectiveCheckin, row.OctorateArrivalTime)
                : new OctorateStayStateView(state, null, 0, null, null, null, null))
            .ToList();
        return new OctorateCheckInView(
            true,
            links.Select(l => new OctorateGuestLinkView(l.Position, l.Status, l.SentAsDraft, l.Attempts, l.ErrorCode, l.SentAt)).ToList(),
            states);
    }

    /// <summary>The Octorate status of a state of the stay.</summary>
    public static OctorateCheckInStatus StatusOf(OctorateStayState state) => state switch
    {
        OctorateStayState.CheckInEnd => OctorateCheckInStatus.CheckInEnd,
        OctorateStayState.CheckOutEnd => OctorateCheckInStatus.CheckOutEnd,
        OctorateStayState.NoShow => OctorateCheckInStatus.NoShow,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state of a stay."),
    };

    /// <summary>
    /// The link of a guest about to be sent (created when missing), Pending with one more attempt. The attempts are counted per payload: a
    /// new change starts again from one.
    /// </summary>
    private StayGuestOctorateLink MarkPending(
        Booking booking,
        StayGuestOctorateLink? link,
        int position,
        string hash,
        DateTime now)
    {
        if (link is null)
        {
            link = new StayGuestOctorateLink { OrgId = booking.OrgId, BookingId = booking.Id, Position = position, PayloadHash = hash };
            db.StayGuestOctorateLinks.Add(link);
        }

        link.Attempts = link.PayloadHash == hash ? link.Attempts + 1 : 1;
        link.PayloadHash = hash;
        link.Status = OctorateSyncStatus.Pending;
        link.UpdatedAt = now;
        return link;
    }

    /// <summary>Records the answer of one guest on its link; at the last failed attempt one "To do" is raised, closed by a later success.</summary>
    private async Task<OctorateGuestSendResult> RecordGuestAsync(
        Booking booking,
        StayGuestOctorateLink link,
        OctorateCheckInGuestUpsert upsert,
        OctorateCheckInGuestResult answer,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        link.UpdatedAt = now;
        if (answer.Succeeded)
        {
            link.OctorateGuestId = answer.OctorateGuestId ?? link.OctorateGuestId;
            link.Status = OctorateSyncStatus.Sent;
            link.SentAsDraft = upsert.Draft;
            link.SentAt = now;
            link.ErrorCode = null;
            link.TodoActivityId = await CloseTodoAsync(link.TodoActivityId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new OctorateGuestSendResult(
                upsert.Position, answer.Created ? OctorateGuestSendOutcome.Created : OctorateGuestSendOutcome.Updated, null);
        }

        link.Status = OctorateSyncStatus.Failed;
        link.ErrorCode = answer.ErrorCode ?? CheckInErrorCodes.Server;
        link.TodoActivityId ??= link.Attempts >= MaxAttempts ? await RaiseTodoAsync(booking, cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        return new OctorateGuestSendResult(upsert.Position, OctorateGuestSendOutcome.Failed, link.ErrorCode);
    }

    /// <summary>The guest of a position that no longer exists: deleted in Octorate, then its link; kept as failed when the delete fails.</summary>
    private async Task<bool> RemoveGuestAsync(
        Booking booking,
        string accommodationId,
        StayGuestOctorateLink link,
        CancellationToken cancellationToken)
    {
        try
        {
            if (link.OctorateGuestId is { } guestId)
                await client.DeleteCheckInGuestAsync(accommodationId, booking.OctorateReservationId!, guestId, cancellationToken);
        }
        catch (OctorateNotFoundException)
        {
            // Already gone on Octorate: nothing left to remove there.
        }
        catch (OctorateException ex)
        {
            link.Attempts++;
            link.Status = OctorateSyncStatus.Failed;
            link.ErrorCode = CheckInErrorCodes.Of(ex);
            link.UpdatedAt = UtcNow();
            link.TodoActivityId ??= link.Attempts >= MaxAttempts ? await RaiseTodoAsync(booking, cancellationToken) : null;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        await CloseTodoAsync(link.TodoActivityId, cancellationToken);
        db.StayGuestOctorateLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>After the arrival: the arrival Octorate registered, kept on the row. A failed read never fails the push.</summary>
    private async Task ReadArrivalAsync(
        Booking booking,
        string accommodationId,
        OctorateStayStateSync row,
        CancellationToken cancellationToken)
    {
        try
        {
            OctorateReservationArrival arrival;
            using (orgScope.Use(booking.OrgId))
                arrival = await client.GetReservationArrivalAsync(accommodationId, booking.OctorateReservationId!, cancellationToken);

            row.OctorateEffectiveCheckin = arrival.EffectiveCheckin;
            row.OctorateArrivalTime = arrival.ArrivalTime;
            row.ReadAt = UtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OctorateException ex)
        {
            logger.LogWarning(
                "Arrival of booking {BookingId} not read back from Octorate: {ErrorCode}", booking.Id, CheckInErrorCodes.Of(ex));
        }
    }

    /// <summary>
    /// The tourist tax values of each position, only with <c>Octorate:WriteCityTaxValues</c> on, a confirmed and not stale record, and the Octorate
    /// code of a host's reason only when the reason has one (AC-NF46-5).
    /// </summary>
    private async Task<IReadOnlyDictionary<int, OctorateCityTaxValues>> TaxValuesAsync(Booking booking, CancellationToken cancellationToken)
    {
        if (!options.Value.WriteCityTaxValues)
            return new Dictionary<int, OctorateCityTaxValues>();

        bool stale;
        try
        {
            stale = (await stayTouristTax.GetAsync(booking.Id, cancellationToken)).Stale;
        }
        catch (Exception ex) when (ex is DomainException or NotFoundException)
        {
            // A tax that cannot be worked out now is never sent: the guests travel without it.
            return new Dictionary<int, OctorateCityTaxValues>();
        }

        var record = await db.StayTouristTaxes
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Include(t => t.Guests)
            .FirstOrDefaultAsync(t => t.OrgId == booking.OrgId && t.BookingId == booking.Id, cancellationToken);
        var codes = await db.TouristTaxExemptionReasons
            .AsNoTracking()
            .ToDictionaryAsync(r => r.Code, r => r.OctorateExemptionCode, cancellationToken);
        return OctorateCheckInMapper.TaxValues(true, record, stale, codes);
    }

    /// <summary>Where the booking is on Octorate, or why it is not mirrored (nothing is written then).</summary>
    private async Task<CheckInTarget> ResolveTargetAsync(Booking booking, CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return CheckInTarget.NotMirrored(ReasonConnectorOff);
        if (string.IsNullOrWhiteSpace(booking.OctorateReservationId))
            return CheckInTarget.NotMirrored(ReasonNoReservation);

        var environment = OctorateEnvironments.Of(hostEnvironment);
        var connectionIds = await db.OctorateConnections
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.OrgId == booking.OrgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        if (connectionIds.Count == 0)
            return CheckInTarget.NotMirrored(ReasonNotConnected);

        var accommodationId = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == booking.OrgId
                        && m.PropertyId == booking.PropertyId
                        && m.Status == OctorateMappingStatus.Confirmed
                        && connectionIds.Contains(m.ConnectionId))
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(accommodationId))
            return CheckInTarget.NotMirrored(ReasonMappingNotConfirmed);

        if (await channelMapping.IsWriteBlockedAsync(booking.PropertyId, cancellationToken))
            return CheckInTarget.NotMirrored(ReasonWritesBlocked);

        return new CheckInTarget(accommodationId, null);
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { MessageKey = "BookingNotFound" };

    /// <summary>One "To do" for a booking whose check-in keeps failing (idempotent: the open one is returned). No guest data.</summary>
    private async Task<Guid?> RaiseTodoAsync(Booking booking, CancellationToken cancellationToken)
    {
        try
        {
            return await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.IntegrationError,
                    Action = ActivityAction.ReviewBooking,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = booking.Id,
                    Urgency = ActivityUrgency.Medium,
                    PropertyId = booking.PropertyId,
                    OrgId = booking.OrgId,
                    Reason = TodoReason,
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is NotFoundException or ArgumentException)
        {
            logger.LogWarning(ex, "The To do of the check-in of booking {BookingId} could not be raised", booking.Id);
            return null;
        }
    }

    /// <summary>Closes the "To do" of a write that finally succeeded; returns the id to keep (null).</summary>
    private async Task<Guid?> CloseTodoAsync(Guid? todoId, CancellationToken cancellationToken)
    {
        if (todoId is not { } id)
            return null;

        try
        {
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, cancellationToken);
        }
        catch (NotFoundException)
        {
            // Closed or gone already.
        }

        return null;
    }

    private static string GuestsLockKey(Guid bookingId) => $"{bookingId:N}:guests";

    private static OctorateCheckInSendResult Nothing(OctorateCheckInSendState state, string reason) =>
        new(state, reason, Draft: false, [], CorrectionAfterSend: false);

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The accommodation of the booking on Octorate, or the reason it is not mirrored.</summary>
    private sealed record CheckInTarget(string? AccommodationId, string? Reason)
    {
        public static CheckInTarget NotMirrored(string reason) => new(null, reason);
    }
}
