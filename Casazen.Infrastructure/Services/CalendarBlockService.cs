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

    public async Task<CalendarBlock> CreateAsync(ManualBlockRequest request, CancellationToken cancellationToken = default) =>
        (await CreateTrackedAsync(request, cancellationToken)).Block;

    public async Task<ManualBlockChange> CreateTrackedAsync(ManualBlockRequest request, CancellationToken cancellationToken = default)
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

        var resolved = changeRecorder is null ? (CalendarSendOutcome?)null : await changeRecorder.ResolveAsync(request.PropertyId, cancellationToken);

        await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(request.PropertyId, start, end, cancellationToken);

        await using var transaction = db.Database.IsNpgsql() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;
        if (db.Database.IsNpgsql())
            await BookingRepository.LockPropertyDatesAsync(db, request.PropertyId, cancellationToken);

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

        var takenByManualBlock = await db.CalendarBlocks
            .Where(PropertyOccupancy.BlockTakesNightIn(request.PropertyId, start, end))
            .AnyAsync(b => b.Source == CalendarBlockSource.Manual, cancellationToken);
        if (takenByManualBlock)
            throw new DomainConflictException(ManualBlockErrorCodes.OverlapsBlock, ManualBlockErrorCodes.OverlapsBlockMessageKey);

        var change = await RecordChangeAsync(request.PropertyId, start, end, "created", request.Reason, resolved, cancellationToken);

        var block = new CalendarBlock
        {
            Id = request.BlockId ?? Guid.NewGuid(),
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
        return new ManualBlockChange(block, change?.ProcessId, change?.Process.State);
    }

    public async Task<CalendarBlock?> FindAsync(Guid blockId, CancellationToken cancellationToken = default) =>
        await db.CalendarBlocks.AsNoTracking().FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken);

    public async Task DeleteAsync(Guid blockId, CancellationToken cancellationToken = default)
    {
        var block = await db.CalendarBlocks.FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken)
            ?? throw BlockNotFound(blockId);
        if (block.Source != CalendarBlockSource.Manual)
            throw new DomainRuleException(ManualBlockErrorCodes.NotManual, ManualBlockErrorCodes.NotManualMessageKey);

        if (block.ManualReason == CalendarBlockReason.ModeChange)
            throw new DomainRuleException(ManualBlockErrorCodes.HeldByModeChange, ManualBlockErrorCodes.HeldByModeChangeMessageKey);

        if (block.ManualReason == CalendarBlockReason.Owner
            && await db.OwnerStayRequests.AnyAsync(s => s.CalendarBlockId == block.Id && s.BlockReleasedAt == null, cancellationToken))
        {
            throw new DomainRuleException(ManualBlockErrorCodes.HeldByOwnerStay, ManualBlockErrorCodes.HeldByOwnerStayMessageKey);
        }

        var change = await RecordChangeAsync(block.PropertyId, block.StartUtc.Date, block.EndUtc.Date, "removed", block.ManualReason, null, cancellationToken);

        db.CalendarBlocks.Remove(block);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw BlockNotFound(blockId);
        }

        EnqueueRun(change);

        logger.LogInformation("Manual block {BlockId} removed from property {PropertyId}", block.Id, block.PropertyId);
    }

    public async Task<ManualBlockChange?> ReleaseOwnerStayBlockAsync(Guid blockId, CancellationToken cancellationToken = default)
    {
        var block = await db.CalendarBlocks.FirstOrDefaultAsync(b => b.Id == blockId, cancellationToken);
        if (block is null)
            return null;

        if (block.Source != CalendarBlockSource.Manual || block.ManualReason != CalendarBlockReason.Owner)
            throw new DomainRuleException(ManualBlockErrorCodes.NotManual, ManualBlockErrorCodes.NotManualMessageKey);

        var change = await RecordChangeAsync(block.PropertyId, block.StartUtc.Date, block.EndUtc.Date, "removed", block.ManualReason, null, cancellationToken);
        db.CalendarBlocks.Remove(block);
        await db.SaveChangesAsync(cancellationToken);
        EnqueueRun(change);

        logger.LogInformation("Owner stay block {BlockId} removed from property {PropertyId}", block.Id, block.PropertyId);
        return new ManualBlockChange(block, change?.ProcessId, change?.Process.State);
    }

    private async Task<CalendarChangeRecord?> RecordChangeAsync(
        Guid propertyId,
        DateTime start,
        DateTime end,
        string action,
        CalendarBlockReason? reason,
        CalendarSendOutcome? resolved,
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
            cancellationToken,
            resolved: resolved is { } known ? new Dictionary<Guid, CalendarSendOutcome> { [propertyId] = known } : null);
    }

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
