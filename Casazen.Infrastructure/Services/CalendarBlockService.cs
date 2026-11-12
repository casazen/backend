using System.Data;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Repositories;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Manual calendar blocks of the host (PC-09, A2-25). See <see cref="ICalendarBlockService"/>. A block is a row of
/// <c>CalendarBlocks</c> with <see cref="CalendarBlockSource.Manual"/>, no feed and a <see cref="CalendarBlock.ManualReason"/>:
/// the occupancy rule (<see cref="PropertyOccupancy"/>, BK-05) already counts every block of the property, so the booking
/// site, the booking checks and the export to the OTAs (<see cref="ICalExportService.ExportsBlock"/>) respect it with no
/// code of their own.
/// </summary>
/// <remarks>
/// Concurrency: the block is written under the dates lock of the property (<see cref="BookingRepository.LockPropertyDatesAsync"/>),
/// the lock every new booking takes before its final overlap check, which also reads the blocks
/// (<see cref="BookingRepository.AddAsync"/>). Whoever comes second sees the other's row: a booking and a block never
/// take the same night. Abandoned checkout holds of the dates are released first, outside the lock (as for a booking
/// entered by the host, BK-21: the expiry takes its own locks and may call Stripe).
/// </remarks>
public sealed class CalendarBlockService(
    AppDbContext db,
    ICheckoutHoldExpiryService checkoutHoldExpiry,
    ILogger<CalendarBlockService> logger,
    TimeProvider? timeProvider = null,
    ICalendarChangeRecorder? changeRecorder = null,
    IBackgroundJobClient? jobs = null) : ICalendarBlockService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<CalendarBlock>> ListManualAsync(
        Guid propertyId,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var from = (fromDate ?? _clock.TodayInRome()).Date;
        var query = db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.PropertyId == propertyId && b.Source == CalendarBlockSource.Manual)
            .Where(b => b.EndUtc.Date > from);
        if (toDate is { } toValue)
        {
            var to = toValue.Date;
            query = query.Where(b => b.StartUtc.Date < to);
        }

        return await query
            .OrderBy(b => b.StartUtc)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<CalendarBlock> CreateAsync(ManualBlockRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var start = request.StartDate.Date;
        var end = request.EndDate.Date;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        Validate(start, end, request.Reason, note);

        var orgId = await db.Properties
            .Where(p => p.Id == request.PropertyId)
            .Select(p => (Guid?)p.OrgId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Property {request.PropertyId} not found")
            {
                Code = ManualBlockErrorCodes.PropertyNotFound,
                MessageKey = ManualBlockErrorCodes.PropertyNotFoundMessageKey,
            };

        await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(request.PropertyId, start, end, cancellationToken);

        await using var transaction = db.Database.IsNpgsql() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        if (db.Database.IsNpgsql())
            await BookingRepository.LockPropertyDatesAsync(db, request.PropertyId, cancellationToken);

        // Strict, as the final check of a booking insert: every booking not cancelled counts (expired holds of these
        // dates were cancelled above; a hold whose guest paid late keeps its dates).
        var takenByBooking = await db.Bookings
            .Where(PropertyOccupancy.BookingTakesNightIn(request.PropertyId, start, end))
            .Where(CheckoutHolds.OccupiesDates(null))
            .AnyAsync(cancellationToken);
        if (takenByBooking)
        {
            logger.LogInformation(
                "Manual block refused on property {PropertyId}: a booking takes a night from {Start:yyyy-MM-dd} to {End:yyyy-MM-dd}",
                request.PropertyId, start, end);
            throw new DomainConflictException(ManualBlockErrorCodes.OverlapsBooking, ManualBlockErrorCodes.OverlapsBookingMessageKey);
        }

        // Two manual blocks on the same night would only duplicate the closure (and a double click would create two):
        // refused. A night already closed by an imported block may be closed by hand too, it stays closed when the
        // channel frees it.
        var takenByManualBlock = await db.CalendarBlocks
            .Where(PropertyOccupancy.BlockTakesNightIn(request.PropertyId, start, end))
            .AnyAsync(b => b.Source == CalendarBlockSource.Manual, cancellationToken);
        if (takenByManualBlock)
            throw new DomainConflictException(ManualBlockErrorCodes.OverlapsBlock, ManualBlockErrorCodes.OverlapsBlockMessageKey);

        // E08/F05/US02/T02 (NF-78): the change that tells the channel manager the nights are closed is recorded in this unit of work, under this lock, so the block
        // and its process exist together or not at all. It comes before the block is staged: the lookup of where the property books may save the unit of work.
        var change = await RecordChangeAsync(request.PropertyId, start, end, "created", request.Reason, cancellationToken);

        var block = new CalendarBlock
        {
            PropertyId = request.PropertyId,
            OrgId = orgId,
            Source = CalendarBlockSource.Manual,
            FeedId = null,
            ExternalUid = null,
            StartUtc = start,
            EndUtc = end,
            ManualReason = request.Reason,
            Summary = note,
        };
        db.CalendarBlocks.Add(block);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        EnqueueRun(change);

        logger.LogInformation(
            "Manual block {BlockId} ({Reason}) created on property {PropertyId}: {Nights} nights from {Start:yyyy-MM-dd}",
            block.Id, block.ManualReason, block.PropertyId, (end - start).Days, start);
        return block;
    }

    public async Task<CalendarBlock?> FindAsync(Guid blockId, CancellationToken cancellationToken = default) =>
        await db.CalendarBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken);

    public async Task DeleteAsync(Guid blockId, CancellationToken cancellationToken = default)
    {
        var block = await db.CalendarBlocks.FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken)
            ?? throw BlockNotFound(blockId);
        if (block.Source != CalendarBlockSource.Manual)
            throw new DomainRuleException(ManualBlockErrorCodes.NotManual, ManualBlockErrorCodes.NotManualMessageKey);

        // PM-02: the block of a property that went long-term keeps the portals from selling it. Only the return to short
        // stays removes it (PropertyModeService, in the same transaction as the mode); by hand it would reopen the dates.
        if (block.ManualReason == CalendarBlockReason.ModeChange)
            throw new DomainRuleException(ManualBlockErrorCodes.HeldByModeChange, ManualBlockErrorCodes.HeldByModeChangeMessageKey);

        // E08/F05/US02/T02 (NF-78): the nights are open again on the channel manager: the change is recorded in the same save as the removal.
        var change = await RecordChangeAsync(block.PropertyId, block.StartUtc.Date, block.EndUtc.Date, "removed", block.ManualReason, cancellationToken);

        db.CalendarBlocks.Remove(block);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Removed meanwhile by another request: the outcome the host asked for.
            throw BlockNotFound(blockId);
        }

        EnqueueRun(change);

        logger.LogInformation("Manual block {BlockId} removed from property {PropertyId}", block.Id, block.PropertyId);
    }

    /// <summary>
    /// Adds to the unit of work the change that sends the nights of a block (or of its removal) to the channel manager (<see cref="ICalendarChangeRecorder"/>): the nights
    /// from <paramref name="start"/> (included) to <paramref name="end"/> (excluded). A property that is not on the channel manager records an <c>Applied</c> process and
    /// nothing else. <c>null</c> when no recorder is registered. The summary names the action and the reason: no note, no name.
    /// </summary>
    private async Task<CalendarChangeRecord?> RecordChangeAsync(
        Guid propertyId,
        DateTime start,
        DateTime end,
        string action,
        CalendarBlockReason? reason,
        CancellationToken cancellationToken)
    {
        if (changeRecorder is null)
            return null;

        var window = new CalendarWindow(propertyId, DateOnly.FromDateTime(start), DateOnly.FromDateTime(end).AddDays(-1));
        return await changeRecorder.RecordAsync(
            CalendarChangeKind.Block,
            [window],
            null,
            JsonSerializer.Serialize(new { action, reason = reason?.ToString() }),
            cancellationToken);
    }

    /// <summary>
    /// After the commit, a process that is <c>Queued</c> is enqueued once for an immediate run; the recurring job is the safety net, so a queue that cannot take it is
    /// not a failure of the block.
    /// </summary>
    private void EnqueueRun(CalendarChangeRecord? change)
    {
        if (change is not { NeedsRun: true } || jobs is null)
            return;

        var processId = change.ProcessId;
        try
        {
            jobs.Enqueue<CalendarChangeProcessJob>(job => job.RunAsync(processId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Calendar change {ProcessId} could not be enqueued: the recurring job sends it", processId);
        }
    }

    private void Validate(DateTime start, DateTime end, CalendarBlockReason reason, string? note)
    {
        if (end <= start)
            throw new DomainRuleException(ManualBlockErrorCodes.InvalidRange, ManualBlockErrorCodes.InvalidRangeMessageKey);
        if (start < _clock.TodayInRome())
            throw new DomainRuleException(ManualBlockErrorCodes.InPast, ManualBlockErrorCodes.InPastMessageKey);
        if ((end - start).Days > ManualBlocks.MaxNights)
        {
            throw new DomainRuleException(
                ManualBlockErrorCodes.TooLong, ManualBlockErrorCodes.TooLongMessageKey, ManualBlocks.MaxNights);
        }
        // ModeChange (PM-02) is written by the mode change service, never chosen by the host.
        if (!Enum.IsDefined(reason) || reason == CalendarBlockReason.ModeChange)
            throw new DomainRuleException(ManualBlockErrorCodes.InvalidReason, ManualBlockErrorCodes.InvalidReasonMessageKey);
        if (note is { Length: > CalendarBlock.ManualNoteMaxLength })
            throw new DomainRuleException(ManualBlockErrorCodes.NoteTooLong, ManualBlockErrorCodes.NoteTooLongMessageKey);
    }

    private static NotFoundException BlockNotFound(Guid blockId) =>
        new($"Calendar block {blockId} not found")
        {
            Code = ManualBlockErrorCodes.NotFound,
            MessageKey = ManualBlockErrorCodes.NotFoundMessageKey,
        };
}
