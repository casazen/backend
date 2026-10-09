using System.Data;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The scheduled change of rental mode of a property (PM-02, decisions D16 and D19; <c>gap/06</c> §4.4): preview, creation,
/// cancellation and the hourly application. See <see cref="IPropertyModeService"/> for the contract and
/// <see cref="PropertyModeRules"/> for the rules; operations in <c>docs/runbooks/property-rental-mode.md</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Concurrency.</b> The creation, the cancellation and the application of a change run in one transaction under the
/// dates lock of the property (<see cref="BookingRepository.LockPropertyDatesAsync"/>), the lock every new booking and every
/// manual calendar block takes before its final overlap check (BK-04). Whoever comes second sees the other's rows: a stay
/// that is saved before the change is created is in its check; a stay saved after finds the calendar block of the change
/// and is refused. Outside PostgreSQL (EF InMemory in the unit tests) nothing is locked.</item>
/// <item><b>Idempotence.</b> A change leaves <see cref="PropertyModeChangeStatus.Scheduled"/> in the same transaction as its
/// effect (mode, calendar block). A second run, a retry or a concurrent run re-reads the row inside the lock and finds it
/// done. The whole run holds a session advisory lock too (<see cref="PostgresAdvisoryLocks.Scope.PropertyModeChangeRun"/>),
/// on top of Hangfire's <c>DisableConcurrentExecution</c>.</item>
/// <item><b>The only writer of the mode.</b> <c>PropertyRepository.UpdateAsync</c> never writes <c>Properties.RentalMode</c>
/// (PM-01); after the creation of the property this service is the only code that does.</item>
/// <item><b>The calendar block.</b> A change <b>to long-term</b> closes the dates with a manual <see cref="CalendarBlock"/>
/// of reason <see cref="CalendarBlockReason.ModeChange"/>, from the night before its day
/// (<see cref="PropertyModeRules.CalendarBlockStart"/>) for <see cref="PropertyModeRules.CalendarBlockYears"/> years:
/// written when the change is programmed (from then on the booking site, the host and the portals that read the iCal
/// export stop taking those nights, including the night a new stay would take and still check out on the day of the
/// change, so a stay cannot arrive between the programming and the day), confirmed when it is applied, removed if the
/// change is cancelled or fails. A change <b>to short stays</b> removes the block when it is applied.
/// Written in the transaction of the change, not through <see cref="ICalendarBlockService"/> (its checks are the host's: a
/// year at most, not in the past, no overlap).</item>
/// <item><b>Compliance.</b> A property back in short-stay mode is evaluated again by
/// <see cref="IPropertyComplianceStatusService.ReevaluateAsync"/>: its status was frozen while it was long-term (PM-01) and
/// its CIN, documents or checklist may no longer be complete. Done after the commit: a failure there is logged and the
/// nightly check (<c>property-compliance-check</c>) takes it up.</item>
/// <item><b>Notices.</b> The host is told after the commit, never inside the transaction (a failed e-mail never undoes a
/// change).</item>
/// </list>
/// </remarks>
public sealed class PropertyModeService(
    AppDbContext db,
    IConfiguration configuration,
    IPropertyComplianceStatusService complianceStatus,
    INotificationService notifications,
    ILogger<PropertyModeService> logger,
    TimeProvider? timeProvider = null) : IPropertyModeService
{
    private const string RunLockKey = "property-mode-change";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<PropertyModePreview> PreviewAsync(
        Guid propertyId,
        RentalMode to,
        DateTime? date = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDefinedTarget(to);
        var property = await LoadPropertyAsync(propertyId, cancellationToken);
        EnsureDifferentMode(property, to);

        return await AssessAsync(property, to, date, cancellationToken);
    }

    public async Task<PropertyModeState> GetStateAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var property = await LoadPropertyAsync(propertyId, cancellationToken);
        var scheduled = await db.PropertyModeChanges
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PropertyId == propertyId && c.Status == PropertyModeChangeStatus.Scheduled, cancellationToken);
        var last = await db.PropertyModeChanges
            .AsNoTracking()
            .Where(c => c.PropertyId == propertyId && c.Status != PropertyModeChangeStatus.Scheduled)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return new PropertyModeState(propertyId, property.RentalMode, scheduled, last);
    }

    public async Task<PropertyModeChange> ScheduleAsync(
        Guid propertyId,
        RentalMode to,
        DateTime effectiveDate,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        EnsureDefinedTarget(to);

        PropertyModeChange change;
        await using (var transaction = await BeginLockedAsync(propertyId, cancellationToken))
        {
            // Inside the lock: every booking that was being saved has finished, the next one will find the change.
            var property = await LoadPropertyAsync(propertyId, cancellationToken);
            EnsureDifferentMode(property, to);

            var day = AsDay(effectiveDate);
            var preview = await AssessAsync(property, to, day, cancellationToken);
            PropertyModeRules.EnsureAllowed(preview);

            change = new PropertyModeChange
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                FromMode = property.RentalMode,
                ToMode = to,
                EffectiveDate = day,
                Status = PropertyModeChangeStatus.Scheduled,
                CreatedByUserId = userId,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            db.PropertyModeChanges.Add(change);

            // To long-term the calendar closes from the night before the change right now, so nothing new can take a night
            // that would still be in the way on the day (a checkout that morning). The site and the host are refused, the
            // portals read the export. Back to short stays nothing closes.
            if (to == RentalMode.Long)
                await CloseCalendarAsync(property.Id, property.OrgId, day, cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsOneScheduledViolation(ex))
            {
                // Another change was programmed between the check and the insert (a writer outside the lock).
                throw new DomainConflictException(PropertyModeErrorCodes.ChangeExists, PropertyModeErrorCodes.ChangeExistsMessageKey);
            }

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Mode change {ChangeId} of property {PropertyId} programmed by user {UserId}: {From} to {To} on {EffectiveDate:yyyy-MM-dd}",
            change.Id, change.PropertyId, userId, change.FromMode, change.ToMode, change.EffectiveDate);

        await NotifyAsync(new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Scheduled), cancellationToken);
        return change;
    }

    public async Task CancelAsync(Guid propertyId, Guid changeId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        PropertyModeChange change;
        await using (var transaction = await BeginLockedAsync(propertyId, cancellationToken))
        {
            // Read inside the lock: the job may have applied or failed the change a moment ago.
            change = await db.PropertyModeChanges
                    .FirstOrDefaultAsync(c => c.Id == changeId && c.PropertyId == propertyId, cancellationToken)
                ?? throw new NotFoundException($"Mode change {changeId} not found")
                {
                    Code = PropertyModeErrorCodes.ChangeNotFound,
                    MessageKey = PropertyModeErrorCodes.ChangeNotFoundMessageKey,
                };
            if (change.Status != PropertyModeChangeStatus.Scheduled)
            {
                throw new DomainConflictException(
                    PropertyModeErrorCodes.ChangeNotScheduled, PropertyModeErrorCodes.ChangeNotScheduledMessageKey);
            }

            change.Status = PropertyModeChangeStatus.Cancelled;
            change.CancelledAt = _clock.GetUtcNow().UtcDateTime;
            change.CancelledByUserId = userId;

            // The change to long-term had closed the calendar: the nights are free again. A change to short stays closed
            // nothing, and the block of the property (it is long-term) stays where it is.
            if (change.ToMode == RentalMode.Long)
                await ReopenCalendarAsync(propertyId, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Mode change {ChangeId} of property {PropertyId} cancelled by user {UserId}", change.Id, change.PropertyId, userId);
    }

    public async Task<PropertyModeRunResult> ApplyDueAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.PropertyModeChangeRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Property mode change run skipped: another run is in progress");
            return new PropertyModeRunResult(Skipped: true, Examined: 0, Applied: 0, Failed: 0);
        }

        // Background run: no tenant context, every org's changes. "Due" is the calendar day of Rome (RomeCalendar): the
        // first run after its midnight applies a change, a run after a long stop applies the ones it missed.
        var today = _clock.TodayInRome();
        var due = await db.PropertyModeChanges
            .AsNoTracking()
            .Where(c => c.Status == PropertyModeChangeStatus.Scheduled && c.EffectiveDate <= today)
            .OrderBy(c => c.EffectiveDate)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        int applied = 0, failed = 0;
        foreach (var changeId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (await ApplyOneAsync(changeId, today, cancellationToken))
                {
                    case Outcome.Applied:
                        applied++;
                        break;
                    case Outcome.Failed:
                        failed++;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The change stays scheduled and the next run tries again; one change never stops the others.
                failed++;
                logger.LogError(ex, "Mode change {ChangeId} could not be processed; it stays scheduled", changeId);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (due.Count > 0)
        {
            logger.LogInformation(
                "Property mode change run: {Examined} due, {Applied} applied, {Failed} failed", due.Count, applied, failed);
        }

        return new PropertyModeRunResult(Skipped: false, due.Count, applied, failed);
    }

    private enum Outcome
    {
        /// <summary>Not scheduled any more (another run, a cancellation) or gone: nothing to do.</summary>
        NothingToDo,
        Applied,
        Failed,
    }

    /// <summary>
    /// Applies one change that is due, or fails it. Everything it writes (mode, calendar block, status) is one transaction
    /// under the dates lock of the property; the compliance evaluation and the notice come after the commit.
    /// </summary>
    private async Task<Outcome> ApplyOneAsync(Guid changeId, DateTime today, CancellationToken cancellationToken)
    {
        var propertyId = await db.PropertyModeChanges
            .Where(c => c.Id == changeId)
            .Select(c => (Guid?)c.PropertyId)
            .FirstOrDefaultAsync(cancellationToken);
        if (propertyId is null)
            return Outcome.NothingToDo;

        PropertyModeChange change;
        PropertyModeNotice? notice = null;
        var reevaluate = false;
        var outcome = Outcome.NothingToDo;
        await using (var transaction = await BeginLockedAsync(propertyId.Value, cancellationToken))
        {
            // Read again inside the lock: a second run, a cancellation or a retry has already moved the change.
            var current = await db.PropertyModeChanges.FirstOrDefaultAsync(c => c.Id == changeId, cancellationToken);
            if (current is null || current.Status != PropertyModeChangeStatus.Scheduled)
                return Outcome.NothingToDo;

            change = current;
            var now = _clock.GetUtcNow().UtcDateTime;
            var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == change.PropertyId, cancellationToken);
            if (property is null)
            {
                // Deleted since (soft delete): nothing to change, nobody to tell.
                Fail(change, PropertyModeErrorCodes.PropertyNotFound, now);
                outcome = Outcome.Failed;
                if (change.ToMode == RentalMode.Long)
                    await ReopenCalendarAsync(change.PropertyId, cancellationToken);
            }
            else if (property.RentalMode != change.FromMode)
            {
                // Put back by hand since it was programmed (runbook): the change does not describe the property any more.
                // The calendar block is left as it is: it belongs to whatever mode the property has now.
                Fail(change, PropertyModeErrorCodes.PropertyChanged, now);
                notice = new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Failed);
                outcome = Outcome.Failed;
            }
            else
            {
                // A change that is due is checked for today: its own day when it is today, and when the run is late a stay
                // that left in the meantime is no obstacle. Same rule as the creation (PropertyModeRules).
                var candidates = await LoadCandidatesAsync(property.Id, change.ToMode, today, cancellationToken);
                var blockers = PropertyModeRules.BlockersFor(candidates, today);
                if (PropertyModeRules.FailureReasonOf(blockers) is { } reason)
                {
                    Fail(change, reason, now);
                    notice = new PropertyModeNotice(
                        change.Id, PropertyModeNoticeKind.Failed, PropertyModeRules.EarliestDate(today, candidates));
                    outcome = Outcome.Failed;
                    if (change.ToMode == RentalMode.Long)
                        await ReopenCalendarAsync(property.Id, cancellationToken);
                }
                else
                {
                    property.RentalMode = change.ToMode;
                    property.UpdatedAt = now;
                    if (change.ToMode == RentalMode.Long)
                        await CloseCalendarAsync(property.Id, property.OrgId, change.EffectiveDate, cancellationToken);
                    else
                        await ReopenCalendarAsync(property.Id, cancellationToken);

                    change.Status = PropertyModeChangeStatus.Applied;
                    change.AppliedAt = now;
                    notice = new PropertyModeNotice(change.Id, PropertyModeNoticeKind.Applied);
                    reevaluate = change.ToMode == RentalMode.Short;
                    outcome = Outcome.Applied;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        if (outcome == Outcome.Applied)
        {
            logger.LogInformation(
                "Mode change {ChangeId}: property {PropertyId} is now {To}", change.Id, change.PropertyId, change.ToMode);
        }
        else
        {
            logger.LogWarning(
                "Mode change {ChangeId} of property {PropertyId} failed on {EffectiveDate:yyyy-MM-dd}: {Reason}",
                change.Id, change.PropertyId, change.EffectiveDate, change.FailureReason);
        }

        if (reevaluate)
            await ReevaluateComplianceAsync(change.PropertyId, cancellationToken);

        if (notice is not null)
            await NotifyAsync(notice, cancellationToken);

        return outcome;
    }

    private static void Fail(PropertyModeChange change, string reason, DateTime now)
    {
        change.Status = PropertyModeChangeStatus.Failed;
        change.FailedAt = now;
        change.FailureReason = reason;
    }

    // ─── The calendar block of a property that goes long-term ───────────────────────────────────────

    private IQueryable<CalendarBlock> HeldBlocks(Guid propertyId) =>
        db.CalendarBlocks.Where(b => b.PropertyId == propertyId
                                     && b.Source == CalendarBlockSource.Manual
                                     && b.ManualReason == CalendarBlockReason.ModeChange);

    /// <summary>
    /// Closes the nights of the property from the night before <paramref name="effectiveDay"/>
    /// (<see cref="PropertyModeRules.CalendarBlockStart"/>) until <see cref="PropertyModeRules.CalendarBlockEnd"/>
    /// (two years after the day): one block of reason <see cref="CalendarBlockReason.ModeChange"/>, so the booking site,
    /// the host and the export to the portals take those nights as taken. The night before is the one a new stay would
    /// take and still check out on the day of the change, which <see cref="PropertyModeRules.BlockersFor"/> treats as not
    /// free yet; occupancy is half-open, so a block that started on the day would leave that night open. Idempotent: an
    /// identical block is kept (the portals keep its event), any other block of that reason (a stale one) is removed.
    /// Saved by the caller, in its transaction.
    /// </summary>
    private async Task CloseCalendarAsync(Guid propertyId, Guid orgId, DateTime effectiveDay, CancellationToken cancellationToken)
    {
        var day = AsDay(effectiveDay);
        var start = AsDay(PropertyModeRules.CalendarBlockStart(day));
        var end = AsDay(PropertyModeRules.CalendarBlockEnd(day));
        var held = await HeldBlocks(propertyId).ToListAsync(cancellationToken);
        var keep = held.FirstOrDefault(b => b.StartUtc.Date == start && b.EndUtc.Date == end);
        db.CalendarBlocks.RemoveRange(held.Where(b => !ReferenceEquals(b, keep)));
        if (keep is not null)
            return;

        db.CalendarBlocks.Add(new CalendarBlock
        {
            PropertyId = propertyId,
            OrgId = orgId,
            Source = CalendarBlockSource.Manual,
            FeedId = null,
            ExternalUid = null,
            StartUtc = start,
            EndUtc = end,
            ManualReason = CalendarBlockReason.ModeChange,
            Summary = null,
        });
    }

    /// <summary>Removes the block of reason <see cref="CalendarBlockReason.ModeChange"/>: the nights are free again. Saved by the caller.</summary>
    private async Task ReopenCalendarAsync(Guid propertyId, CancellationToken cancellationToken) =>
        db.CalendarBlocks.RemoveRange(await HeldBlocks(propertyId).ToListAsync(cancellationToken));

    private async Task ReevaluateComplianceAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        try
        {
            // The status was frozen while the property was long-term (PM-01): a CIN, a document or the checklist may have
            // changed since. An active property that lost a requirement is suspended now, with the usual e-mail.
            await complianceStatus.ReevaluateAsync(propertyId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The mode is committed. The nightly check (property-compliance-check) evaluates the property anyway.
            logger.LogError(ex, "Compliance of property {PropertyId} could not be evaluated after its change to short stays", propertyId);
        }
    }

    private async Task NotifyAsync(PropertyModeNotice notice, CancellationToken cancellationToken)
    {
        try
        {
            if (!await notifications.SendPropertyModeChangeAsync(notice, cancellationToken))
                logger.LogWarning("{Kind} notice of mode change {ChangeId} not queued", notice.Kind, notice.ChangeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The change is committed: a notice that cannot be prepared never undoes it.
            logger.LogError(ex, "{Kind} notice of mode change {ChangeId} could not be prepared", notice.Kind, notice.ChangeId);
        }
    }

    // ─── What stands in the way ─────────────────────────────────────────────────────────────────────

    private async Task<PropertyModePreview> AssessAsync(
        Property property,
        RentalMode to,
        DateTime? requested,
        CancellationToken cancellationToken)
    {
        var today = _clock.TodayInRome();
        var candidates = await LoadCandidatesAsync(property.Id, to, PropertyModeRules.FirstPossibleDay(today), cancellationToken);
        var scheduled = await db.PropertyModeChanges
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.PropertyId == property.Id && c.Status == PropertyModeChangeStatus.Scheduled, cancellationToken);

        return PropertyModeRules.Assess(property.Id, property.RentalMode, to, today, requested, candidates, scheduled);
    }

    /// <summary>
    /// What may stand in the way of a change to <paramref name="to"/> on a day from <paramref name="since"/> on: to long-term
    /// the stays and imported blocks, to short stays the leases and drafts (rules in <see cref="PropertyModeRules"/>). Only
    /// what ends on or after <paramref name="since"/> is read; the rest cannot stand in the way of any day that counts.
    /// </summary>
    private async Task<IReadOnlyList<PropertyModeBlocker>> LoadCandidatesAsync(
        Guid propertyId,
        RentalMode to,
        DateTime since,
        CancellationToken cancellationToken) =>
        to == RentalMode.Long
            ? await LoadShortRentCandidatesAsync(propertyId, since.Date, cancellationToken)
            : await LoadLongRentCandidatesAsync(propertyId, since.Date, cancellationToken);

    /// <summary>
    /// The stays (pending, confirmed, checked in; an abandoned checkout hold takes no dates, BK-21) and the blocks imported
    /// from the portal calendars that are not already counted through an OTA stay (CO-21,
    /// <see cref="PropertyOccupancy.IsRepresentedByStay"/>). The reservations of the portals arrive as blocks. The manual
    /// blocks of the host (owner stay, works) are not in the way: they close nights, they do not host anybody.
    /// </summary>
    private async Task<IReadOnlyList<PropertyModeBlocker>> LoadShortRentCandidatesAsync(
        Guid propertyId,
        DateTime since,
        CancellationToken cancellationToken)
    {
        var holdCutoff = CheckoutHolds.CutoffAt(_clock.GetUtcNow().UtcDateTime, CheckoutHolds.GetTtlMinutes(configuration));
        var stays = await db.Bookings
            .AsNoTracking()
            .Where(b => b.PropertyId == propertyId)
            .Where(b => b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
            .Where(CheckoutHolds.OccupiesDates(holdCutoff))
            .Where(b => b.CheckOutDate.Date >= since)
            .Select(b => new { b.Id, b.CheckInDate, b.CheckOutDate, b.Status, b.Source })
            .ToListAsync(cancellationToken);

        var blocks = await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.PropertyId == propertyId && b.Source == CalendarBlockSource.ICalImport && b.EndUtc.Date >= since)
            .Select(b => new
            {
                Block = new CalendarBlock { Id = b.Id, PropertyId = b.PropertyId, StartUtc = b.StartUtc, EndUtc = b.EndUtc, BookingId = b.BookingId },
                Channel = b.Feed != null ? (ICalFeedChannel?)b.Feed.Channel : null,
                Stay = b.Booking == null
                    ? null
                    : new Booking { Id = b.Booking.Id, Status = b.Booking.Status, CheckInDate = b.Booking.CheckInDate, CheckOutDate = b.Booking.CheckOutDate },
            })
            .ToListAsync(cancellationToken);

        var candidates = new List<PropertyModeBlocker>();
        candidates.AddRange(stays.Select(s => new PropertyModeBlocker(
            PropertyModeBlockerKind.Stay,
            s.Id,
            s.CheckInDate.Date,
            s.CheckOutDate.Date,
            s.CheckOutDate.Date.AddDays(1),
            Status: s.Status.ToString(),
            Source: s.Source.ToString())));
        candidates.AddRange(blocks
            .Where(b => !PropertyOccupancy.IsRepresentedByStay(b.Block, b.Stay))
            .Select(b => new PropertyModeBlocker(
                PropertyModeBlockerKind.ImportedBlock,
                b.Block.Id,
                b.Block.StartUtc.Date,
                b.Block.EndUtc.Date,
                b.Block.EndUtc.Date.AddDays(1),
                Status: null,
                Source: (b.Channel ?? ICalFeedChannel.Other).ToString())));
        return candidates;
    }

    /// <summary>
    /// The leases that are not drafts nor rejected and end on or after <paramref name="since"/>: the rule that refuses to
    /// delete a property (<c>PropertyRepository.SoftDeleteAsync</c>), asked for the day chosen instead of today, from the one
    /// definition <see cref="LeaseOccupancy"/>. And every draft: no day frees the property from a draft, it is deleted first
    /// (D16).
    /// </summary>
    private async Task<IReadOnlyList<PropertyModeBlocker>> LoadLongRentCandidatesAsync(
        Guid propertyId,
        DateTime since,
        CancellationToken cancellationToken)
    {
        var running = await db.LeaseContracts
            .AsNoTracking()
            .Where(LeaseOccupancy.RunsOnOrAfter(propertyId, RomeCalendar.StartOfDayUtc(since)))
            .Select(l => new { l.Id, l.StartDate, l.EndDate, l.Status })
            .ToListAsync(cancellationToken);
        var drafts = await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.PropertyId == propertyId && l.Status == LeaseStatus.Draft)
            .Select(l => new { l.Id, l.StartDate, l.EndDate, l.Status })
            .ToListAsync(cancellationToken);

        var candidates = new List<PropertyModeBlocker>();
        candidates.AddRange(running.Select(l => new PropertyModeBlocker(
            PropertyModeBlockerKind.Lease,
            l.Id,
            l.StartDate.Date,
            l.EndDate.Date,
            l.EndDate.Date.AddDays(1),
            Status: l.Status.ToString())));
        candidates.AddRange(drafts.Select(l => new PropertyModeBlocker(
            PropertyModeBlockerKind.DraftLease,
            l.Id,
            l.StartDate.Date,
            l.EndDate.Date,
            FreeFrom: null,
            Status: l.Status.ToString())));
        return candidates;
    }

    // ─── Small helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The property, read only; another org's property is not found (tenant filter), neither is a deleted one.</summary>
    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken cancellationToken) =>
        await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
        ?? throw new NotFoundException($"Property {propertyId} not found")
        {
            Code = PropertyModeErrorCodes.PropertyNotFound,
            MessageKey = PropertyModeErrorCodes.PropertyNotFoundMessageKey,
        };

    /// <summary>
    /// Opens the transaction (PostgreSQL only; none when the caller has one) and takes the dates lock of the property, the
    /// lock the bookings take (<see cref="BookingRepository.LockPropertyDatesAsync"/>). The returned transaction, when
    /// there is one, is committed by the caller; disposing it without a commit rolls back.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginLockedAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var transaction = PostgresAdvisoryLocks.IsSupported(db) && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        try
        {
            await BookingRepository.LockPropertyDatesAsync(db, propertyId, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            throw;
        }

        return transaction;
    }

    private static void EnsureDefinedTarget(RentalMode to)
    {
        if (!Enum.IsDefined(to))
        {
            throw new DomainRuleException(
                PropertyModeErrorCodes.TargetInvalid, PropertyModeErrorCodes.TargetInvalidMessageKey);
        }
    }

    private static void EnsureDifferentMode(Property property, RentalMode to)
    {
        if (property.RentalMode == to)
        {
            throw new DomainRuleException(
                PropertyModeErrorCodes.AlreadyInMode, PropertyModeErrorCodes.AlreadyInModeMessageKey);
        }
    }

    /// <summary>A stay date: midnight UTC of the calendar day (the storage convention).</summary>
    private static DateTime AsDay(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static bool IsOneScheduledViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PropertyModeChange.OneScheduledIndexName,
        };
}
