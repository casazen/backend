using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Host changes to an existing booking (PC-07, A2-07, A2-08). See <see cref="IHostBookingService"/>.
/// </summary>
/// <remarks>
/// Every change takes the advisory lock of the booking used by the cancellation (BK-02), so a change and a cancellation
/// sent together never overwrite each other. New dates (and a confirmation) are saved through
/// <see cref="IBookingRepository.UpdateAsync"/>, which adds the lock of the property and checks, in the same
/// transaction, that no other booking takes them. Only the columns that changed are written.
/// </remarks>
public sealed class HostBookingService(
    AppDbContext db,
    IBookingService bookingService,
    IBookingRepository bookingRepository,
    ICheckoutHoldExpiryService checkoutHoldExpiry,
    PropertyICalSyncService propertyICalSyncService,
    IOnSiteBookingRequestService onSiteRequests,
    ILogger<HostBookingService> logger,
    TimeProvider? timeProvider = null) : IHostBookingService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<Booking> UpdateAsync(HostBookingUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var checkIn = update.CheckInDate.Date;
        var checkOut = update.CheckOutDate.Date;
        if (checkOut <= checkIn || (checkOut - checkIn).Days > TouristTaxCalculator.MaxNights)
            throw new DomainRuleException(BookingErrorCodes.InvalidDates, "BookingInvalidDates");
        if (update.NumberOfGuests < 1 || update.NumberOfChildren < 0 || update.NumberOfChildren >= update.NumberOfGuests)
            throw new DomainRuleException(BookingErrorCodes.GuestsInvalid, "BookingGuestsInvalid");

        var current = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == update.BookingId)
            .Select(b => new { b.PropertyId, b.CheckInDate, b.CheckOutDate })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BookingNotFound(update.BookingId);

        // Abandoned checkout holds of the new dates are released first, as for a new booking (BK-21). Outside the
        // booking lock: the expiry takes its own locks and may call Stripe.
        if (checkIn != current.CheckInDate.Date || checkOut != current.CheckOutDate.Date)
            await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(current.PropertyId, checkIn, checkOut, cancellationToken);

        await using var transaction = await LockBookingAsync(update.BookingId, cancellationToken);
        var booking = await LoadAsync(update.BookingId, cancellationToken);

        if (booking.Status == BookingStatus.Cancelled)
            throw new DomainRuleException(BookingErrorCodes.NotEditable, "BookingNotEditable");

        var datesChanged = checkIn != booking.CheckInDate.Date || checkOut != booking.CheckOutDate.Date;
        var (storedAdults, storedChildren) = BookingGuestCounts.Of(booking);
        var adults = update.NumberOfGuests - update.NumberOfChildren;
        var guestsChanged = adults != storedAdults || update.NumberOfChildren != storedChildren;

        if (datesChanged || guestsChanged)
        {
            if (booking.Status is BookingStatus.CheckedIn or BookingStatus.CheckedOut)
                throw new DomainRuleException(BookingErrorCodes.StayLocked, "BookingStayLocked");

            // A booking from the booking site or a channel keeps the price, payment, free cancellation deadline and
            // check-in link agreed there: only the bookings entered by the host are priced here.
            if (booking.Source != BookingSource.Manual)
                throw new DomainRuleException(BookingErrorCodes.SourceLocked, "BookingUpdateSourceLocked");

            if (checkIn != booking.CheckInDate.Date && checkIn < _clock.TodayInRome())
                throw new DomainRuleException(BookingErrorCodes.CheckInInPast, "BookingCheckInInPast");

            // Same pricing as a new manual booking: nightly rate x nights + cleaning fee, tourist tax of BK-03.
            var price = await bookingService.PriceHostStayAsync(
                booking.Property, checkIn, checkOut, adults, update.NumberOfChildren, update.ChildrenAges, cancellationToken);
            if (price.TouristTax.Status == TouristTaxQuoteStatus.ChildAgesRequired)
                throw new DomainRuleException(DirectBookingErrorCodes.ChildAgesRequired, "TouristTaxChildAgesRequired");

            booking.BasePrice = price.BasePrice;
            booking.CleaningFee = price.CleaningFee;
            booking.TouristTax = price.TouristTax.AmountOrZero;
            booking.TouristTaxAmount = price.TouristTax.AmountOrZero;
            booking.TotalPrice = price.TotalPrice;
            booking.NumberOfGuests = update.NumberOfGuests;
            booking.NumberOfAdults = adults;
            booking.NumberOfChildren = update.NumberOfChildren;
        }

        if (datesChanged)
        {
            if (await propertyICalSyncService.HasOverlappingBlockAsync(booking.PropertyId, checkIn, checkOut, cancellationToken))
                throw DatesUnavailable();

            booking.CheckInDate = checkIn;
            booking.CheckOutDate = checkOut;
        }

        booking.SpecialRequests = update.SpecialRequests?.Trim() ?? string.Empty;
        booking.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        await SaveAsync(booking, transaction, checkOverlap: datesChanged, cancellationToken);
        logger.LogInformation(
            "Booking {BookingId} updated by the host (dates changed: {DatesChanged}, guests changed: {GuestsChanged})",
            booking.Id,
            datesChanged,
            guestsChanged);
        return booking;
    }

    public async Task<Booking> ConfirmAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var current = await db.Bookings.AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.PropertyId, b.CheckInDate, b.CheckOutDate, b.Source, b.PaymentOption })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw BookingNotFound(bookingId);

        // One confirmation for every pending booking: a "pay at the property" request is the host's acceptance of
        // decision D5, with its own rules (guest email confirmed, deadline, OTA blocks) and emails (BK-06).
        if (current.Source == BookingSource.Direct && current.PaymentOption == PaymentOption.OnSite)
            return await onSiteRequests.AcceptAsync(bookingId, cancellationToken);

        await checkoutHoldExpiry.ExpireOverlappingHoldsAsync(
            current.PropertyId, current.CheckInDate, current.CheckOutDate, cancellationToken);

        await using var transaction = await LockBookingAsync(bookingId, cancellationToken);
        var booking = await LoadAsync(bookingId, cancellationToken);

        if (booking.Status != BookingStatus.Pending)
            throw new DomainConflictException(BookingErrorCodes.NotPending, "BookingNotPending");
        if (booking.Source != BookingSource.Manual)
            throw new DomainRuleException(BookingErrorCodes.NotConfirmable, "BookingNotConfirmable");

        // A pending booking may have been overtaken meanwhile by another booking (checked by the repository under the
        // property lock) or by a block imported from a channel.
        if (await propertyICalSyncService.HasOverlappingBlockAsync(
                booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, cancellationToken))
            throw DatesUnavailable();

        booking.Status = BookingStatus.Confirmed;
        booking.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        // The repository also issues the guest check-in token of a confirmed booking.
        await SaveAsync(booking, transaction, checkOverlap: true, cancellationToken);
        logger.LogInformation("Manual booking {BookingId} confirmed by the host", booking.Id);
        return booking;
    }

    private async Task SaveAsync(
        Booking booking,
        IDbContextTransaction? transaction,
        bool checkOverlap,
        CancellationToken cancellationToken)
    {
        if (checkOverlap)
        {
            try
            {
                // Joins the transaction: property lock + overlap check against every other booking not cancelled.
                await bookingRepository.UpdateAsync(booking);
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Property not available", StringComparison.OrdinalIgnoreCase))
            {
                throw DatesUnavailable();
            }
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private Task<IDbContextTransaction?> LockBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")));

    /// <summary>
    /// The booking as committed now, under the lock. The context may already track it (the controller read it for the
    /// authorization, before the lock): a query does not refresh a tracked entity, so it is reloaded, otherwise a change
    /// racing a cancellation would still see the old status and write over it.
    /// </summary>
    private async Task<Booking> LoadAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw BookingNotFound(bookingId);
        await db.Entry(booking).ReloadAsync(cancellationToken);
        return booking;
    }

    private static NotFoundException BookingNotFound(Guid bookingId) =>
        new($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private static DomainConflictException DatesUnavailable() =>
        new(BookingErrorCodes.DatesUnavailable, "BookingDatesUnavailable");
}
