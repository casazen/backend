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

    public async Task<Booking?> GetByCheckInTokenAsync(Guid checkInToken)
    {
        return await context.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.CheckInToken == checkInToken);
    }

    public async Task<IEnumerable<Booking>> GetByPropertyAsync(Guid propertyId)
    {
        return await context.Bookings
            .Where(b => b.PropertyId == propertyId)
            .Include(b => b.Guest)
            .Include(b => b.Payments)
            .OrderByDescending(b => b.CheckInDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Booking>> GetByGuestAsync(Guid guestId)
    {
        return await context.Bookings
            .Where(b => b.GuestId == guestId)
            .Include(b => b.Property)
            .OrderByDescending(b => b.CheckInDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Booking>> GetAllAsync()
    {
        return await context.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .ToListAsync();
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
        EnsureCheckInToken(booking);
        await using var transaction = await BeginPropertyGuardTransactionAsync(booking.PropertyId);

        if (booking.Status != BookingStatus.Cancelled &&
            await HasActiveOverlapAsync(booking.PropertyId, booking.CheckInDate.Date, booking.CheckOutDate.Date))
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
        EnsureCheckInToken(booking);
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
            EnsureCheckInToken(existing);
            context.Bookings.Update(existing);
            await context.SaveChangesAsync();
            return existing;
        }

        EnsureCheckInToken(booking);
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return booking;
    }

    /// <summary>A confirmed booking carries its self check-in token (also used by the late-payment reconfirmation, BK-04).</summary>
    internal static void EnsureCheckInToken(Booking booking)
    {
        if (booking.Status != BookingStatus.Confirmed)
            return;

        if (!booking.CheckInToken.HasValue)
            booking.CheckInToken = Guid.NewGuid();

        if (!booking.CheckInTokenExpiresAt.HasValue)
            booking.CheckInTokenExpiresAt = booking.CheckOutDate.AddDays(7);
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
