using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Casazen.Infrastructure.Repositories;

public class BookingRepository(AppDbContext context) : IBookingRepository
{
    private const string PropertyUnavailableMessage = "Property not available for selected dates";

    public async Task<Booking?> GetByIdAsync(Guid id)
    {
        return await context.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<IReadOnlyList<Booking>> GetByScopeAsync(
        HostScope scope,
        Guid? propertyId = null,
        Guid? guestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // One query whatever the number of bookings (A2-17): org and scope filters in SQL, property and guest joined,
        // never a lookup per row. Read only, so nothing is tracked.
        var query = context.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId)
            .InScope(scope);
        if (propertyId is { } property)
            query = query.Where(b => b.PropertyId == property);
        if (guestId is { } guest)
            query = query.Where(b => b.GuestId == guest);

        return await query
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .OrderByDescending(b => b.CheckInDate)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<Booking> Items, int TotalCount)> GetPagedByScopeAsync(
        HostScope scope,
        int page,
        int pageSize,
        Guid? propertyId = null,
        Guid? guestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var query = context.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId)
            .InScope(scope);
        if (propertyId is { } property)
            query = query.Where(b => b.PropertyId == property);
        if (guestId is { } guest)
            query = query.Where(b => b.GuestId == guest);

        var ordered = query
            .OrderByDescending(b => b.CheckInDate)
            .ThenBy(b => b.Id);

        var totalCount = await ordered.CountAsync(cancellationToken);
        var items = await ordered
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IEnumerable<Booking>> GetByDateRangeAsync(
        Guid propertyId,
        DateTime startDate,
        DateTime endDate,
        int? directPendingTtlMinutes = null)
    {
        return await context.Bookings
            .Where(HostCalendarRange.BookingShownIn(propertyId, startDate, endDate))
            .Where(CheckoutHolds.OccupiesDates(ExpiredHoldCutoff(directPendingTtlMinutes)))
            .Include(b => b.Guest)
            .ToListAsync();
    }

    public async Task<bool> IsAvailableAsync(
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        int? directPendingTtlMinutes = null)
    {
        // Normalize to date-only to prevent time-component false conflicts (e.g. same-day turnover).
        // A checkout on Apr 5 at 10:00 and a checkin on Apr 5 at 15:00 is a valid same-day turnover.
        var checkInDate = checkIn.Date;
        var checkOutDate = checkOut.Date;

        var conflicting = await HasActiveOverlapAsync(
            propertyId,
            checkInDate,
            checkOutDate,
            ExpiredHoldCutoff(directPendingTtlMinutes));

        return !conflicting;
    }

    public async Task<Booking> AddAsync(Booking booking)
    {
        await using var transaction = await BeginPropertyGuardTransactionAsync(booking.PropertyId);

        if (booking.Status != BookingStatus.Cancelled &&
            (await HasActiveOverlapAsync(booking.PropertyId, booking.CheckInDate.Date, booking.CheckOutDate.Date) ||
             await HasBlockingCalendarBlockAsync(booking)))
        {
            throw new InvalidOperationException(PropertyUnavailableMessage);
        }

        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        if (transaction is not null)
            await transaction.CommitAsync();

        return booking;
    }

    public async Task<Booking> UpdateAsync(Booking booking)
    {
        await using var transaction = await BeginPropertyGuardTransactionAsync(booking.PropertyId);

        if (booking.Status != BookingStatus.Cancelled &&
            await HasActiveOverlapAsync(
                booking.PropertyId,
                booking.CheckInDate.Date,
                booking.CheckOutDate.Date,
                pendingCutoff: null,
                excludeBookingId: booking.Id))
        {
            throw new InvalidOperationException(PropertyUnavailableMessage);
        }

        // A booking loaded by this context is saved with its changed columns only: Update() would mark the whole graph
        // (property, guest, payments) modified and write back values another request may have changed meanwhile.
        if (context.Entry(booking).State == EntityState.Detached)
            context.Bookings.Update(booking);
        await context.SaveChangesAsync();

        if (transaction is not null)
            await transaction.CommitAsync();

        return booking;
    }

    public async Task DeleteAsync(Guid id)
    {
        var booking = await context.Bookings.FindAsync(id);
        if (booking != null)
        {
            context.Bookings.Remove(booking);
            await context.SaveChangesAsync();
        }
    }

    public async Task DiscardCheckoutAttemptAsync(Guid bookingId)
    {
        var propertyId = await context.Bookings
            .Where(b => b.Id == bookingId)
            .Select(b => (Guid?)b.PropertyId)
            .FirstOrDefaultAsync();
        if (propertyId is null)
            return;

        await using var transaction = await BeginPropertyGuardTransactionAsync(propertyId.Value);

        var booking = await context.Bookings
            .Include(b => b.Payments)
            .FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking is null)
            return;

        var guestId = booking.GuestId;
        context.Payments.RemoveRange(booking.Payments);
        context.Bookings.Remove(booking);
        await context.SaveChangesAsync();

        // IgnoreQueryFilters: a reference from any org keeps the guest, as the Restrict foreign keys count every row.
        var guestStillReferenced =
            await context.Bookings.IgnoreQueryFilters().AnyAsync(b => b.GuestId == guestId) ||
            await context.AlloggiatiWebReports.IgnoreQueryFilters().AnyAsync(r => r.GuestId == guestId);
        if (!guestStillReferenced && await context.Guests.FindAsync(guestId) is { } guest)
        {
            context.Guests.Remove(guest);
            await context.SaveChangesAsync();
        }

        if (transaction is not null)
            await transaction.CommitAsync();
    }

    public async Task<Booking?> GetByExternalIdAsync(Guid propertyId, string externalId, BookingSource source)
    {
        return await context.Bookings
            .FirstOrDefaultAsync(b => b.PropertyId == propertyId
                && b.ExternalId == externalId
                && b.Source == source);
    }

    public async Task<Booking> UpsertOtaBookingAsync(Booking booking)
    {
        var existing = await GetByExternalIdAsync(booking.PropertyId, booking.ExternalId, booking.Source);
        if (existing != null)
        {
            existing.Status = booking.Status;
            existing.TotalPrice = booking.TotalPrice;
            existing.CheckInDate = booking.CheckInDate;
            existing.CheckOutDate = booking.CheckOutDate;
            existing.GuestId = booking.GuestId;
            existing.NumberOfGuests = booking.NumberOfGuests;
            existing.UpdatedAt = DateTime.UtcNow;
            context.Bookings.Update(existing);
            await context.SaveChangesAsync();
            return existing;
        }

        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return booking;
    }

    private async Task<bool> HasActiveOverlapAsync(
        Guid propertyId,
        DateTime checkInDate,
        DateTime checkOutDate,
        HoldExpiryCutoff? pendingCutoff = null,
        Guid? excludeBookingId = null)
    {
        // With a cutoff, expired checkout holds do not count (availability); without one (the final check under the
        // property lock before an insert or update) every booking that is not cancelled does. The nights are those of the
        // public availability (PropertyOccupancy, BK-05).
        var query = context.Bookings
            .Where(PropertyOccupancy.BookingTakesNightIn(propertyId, checkInDate, checkOutDate))
            .Where(CheckoutHolds.OccupiesDates(pendingCutoff));

        if (excludeBookingId.HasValue)
            query = query.Where(b => b.Id != excludeBookingId.Value);

        return await query.AnyAsync();
    }

    /// <summary>
    /// A calendar block takes a night of a new CasaZen booking (PC-09). The callers check the blocks before (with the
    /// public availability); this check, under the property lock, closes the gap with a manual block created meanwhile:
    /// manual blocks are written under the same lock (<c>CalendarBlockService</c>), so a booking and a manual block never
    /// take the same night. A stay of an OTA channel (CO-21: created from its own imported block) is the channel's
    /// reservation: blocks never refuse it here.
    /// </summary>
    private async Task<bool> HasBlockingCalendarBlockAsync(Booking booking) =>
        !FiscalCopy.IsOtaBookingSource(booking.Source) &&
        await context.CalendarBlocks.AnyAsync(
            PropertyOccupancy.BlockTakesNightIn(booking.PropertyId, booking.CheckInDate.Date, booking.CheckOutDate.Date));

    private static HoldExpiryCutoff? ExpiredHoldCutoff(int? directPendingTtlMinutes) =>
        directPendingTtlMinutes.HasValue
            ? CheckoutHolds.CutoffAt(DateTime.UtcNow, directPendingTtlMinutes.Value)
            : null;

    private async Task<IDbContextTransaction?> BeginPropertyGuardTransactionAsync(Guid propertyId)
    {
        if (!string.Equals(context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return null;

        var lockKey = ToAdvisoryLockKey(propertyId);
        if (context.Database.CurrentTransaction is not null)
        {
            await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);
            return null;
        }

        var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);
        return transaction;
    }

    /// <summary>
    /// Takes, inside the caller's transaction, the property lock of <see cref="AddAsync"/> and <see cref="UpdateAsync"/>:
    /// the dates of the property cannot be taken by another booking until the caller commits (BK-04, late payment
    /// reconfirmed against a concurrent checkout). Nothing is locked outside PostgreSQL.
    /// </summary>
    internal static async Task LockPropertyDatesAsync(AppDbContext context, Guid propertyId, CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return;

        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The property lock is transaction-scoped: begin a transaction first.");

        await context.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            [ToAdvisoryLockKey(propertyId)],
            cancellationToken);
    }

    private static long ToAdvisoryLockKey(Guid value) =>
        BitConverter.ToInt64(value.ToByteArray(), 0);
}
