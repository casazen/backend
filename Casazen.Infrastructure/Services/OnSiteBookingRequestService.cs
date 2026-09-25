using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnSiteBookingRequestService"/>
/// <remarks>
/// Each transition runs in a READ COMMITTED transaction that takes the advisory lock of the host cancellation of the same
/// booking (BK-02: an answer and a cancellation never interleave) and then the booking row (<c>FOR UPDATE</c>: the expiry
/// job, which uses <c>SKIP LOCKED</c>, leaves it alone; a job already holding it makes the answer wait and then see the
/// expired request). The booking is read again under the locks, so the second of two concurrent answers finds it no
/// longer pending and gets 409. Without PostgreSQL (EF InMemory in unit tests) nothing is locked.
/// </remarks>
public sealed class OnSiteBookingRequestService(
    AppDbContext db,
    PropertyICalSyncService propertyICalSyncService,
    OnSiteRequestNotifier notifier,
    BookingNotifier bookingNotifier,
    IConfiguration configuration,
    ILogger<OnSiteBookingRequestService> logger,
    TimeProvider? timeProvider = null) : IOnSiteBookingRequestService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OnSiteRequestSnapshot> ConfirmGuestEmailAsync(
        Guid bookingId,
        string token,
        CancellationToken cancellationToken = default)
    {
        Booking booking;
        var forwarded = false;
        await using (var transaction = await BeginLockedAsync(bookingId, cancellationToken))
        {
            booking = await ReadUnderLockAsync(bookingId, cancellationToken) ?? throw LinkInvalid();
            if (booking.Source != BookingSource.Direct ||
                booking.PaymentOption != PaymentOption.OnSite ||
                !OnSiteRequests.EmailVerificationTokenMatches(booking.GuestEmailVerificationTokenHash, token))
            {
                throw LinkInvalid();
            }

            var now = UtcNow();
            if (booking.GuestEmailVerifiedAt is null)
            {
                var expiredByJob = booking.CancellationReason == BookingCancellationReason.OnSiteEmailNotConfirmed;
                if (booking.Status != BookingStatus.Pending && !expiredByJob)
                    throw new DomainConflictException(OnSiteRequestErrorCodes.NotPending, "OnSiteRequestNotPending");
                if (expiredByJob || booking.RequestExpiresAt is not { } confirmBy || confirmBy < now)
                    throw new DomainConflictException(OnSiteRequestErrorCodes.Expired, "OnSiteRequestLinkExpired");

                booking.GuestEmailVerifiedAt = now;
                booking.RequestExpiresAt = now.AddHours(OnSiteRequests.GetApprovalHours(configuration));
                booking.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                forwarded = true;
                logger.LogInformation(
                    "On-site request {BookingId}: guest email confirmed, sent to the host until {RequestExpiresAt:o}",
                    booking.Id,
                    booking.RequestExpiresAt);
            }
        }

        if (forwarded)
            await notifier.HostNewRequestAsync(booking.Id, cancellationToken);

        // A second click on the link answers the current state (waiting, accepted, declined or expired).
        return new OnSiteRequestSnapshot(
            booking.Id,
            booking.Status,
            OnSiteRequests.StateOf(booking, UtcNow()),
            booking.Status == BookingStatus.Pending ? booking.RequestExpiresAt : null);
    }

    public async Task<IReadOnlyList<Booking>> GetAwaitingHostApprovalAsync(
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var query = db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId)
            .Where(OnSiteRequests.IsAwaitingHostApproval(UtcNow()));
        if (scope.OwnerId is { } ownerId)
            query = query.Where(b => b.Property.OwnerId == ownerId);

        return await query
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .OrderBy(b => b.RequestExpiresAt)
            .ThenBy(b => b.CheckInDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<Booking> AcceptAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        Booking booking;
        await using (var transaction = await BeginLockedAsync(bookingId, cancellationToken))
        {
            booking = await LoadAwaitingHostAsync(bookingId, cancellationToken);

            // The request was not exported to the OTAs while waiting: an OTA booking imported meanwhile (iCal block) wins,
            // the host can only decline (A3-06, no overbooking on acceptance).
            if (await propertyICalSyncService.HasOverlappingBlockAsync(
                    booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, cancellationToken))
            {
                throw new DomainConflictException(OnSiteRequestErrorCodes.DatesBlocked, "OnSiteRequestDatesBlocked");
            }

            var now = UtcNow();
            booking.Status = BookingStatus.Confirmed;
            booking.RequestExpiresAt = null;
            booking.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("On-site request {BookingId} accepted by the host: booking confirmed", booking.Id);
        // The standard confirmation (BK-10) with "pay at the property"; the host, who accepted, is not emailed.
        await bookingNotifier.BookingConfirmedAsync(booking.Id, BookingConfirmationKind.OnSite, cancellationToken);
        return booking;
    }

    public async Task<Booking> DeclineAsync(Guid bookingId, string? messageToGuest, CancellationToken cancellationToken = default)
    {
        Booking booking;
        await using (var transaction = await BeginLockedAsync(bookingId, cancellationToken))
        {
            booking = await LoadAwaitingHostAsync(bookingId, cancellationToken);

            var now = UtcNow();
            booking.Status = BookingStatus.Cancelled;
            booking.CancellationReason = BookingCancellationReason.OnSiteRequestDeclined;
            booking.RequestExpiresAt = null;
            booking.UpdatedAt = now;
            // Nothing was collected: the "pay at the property" row is closed like any uncollected payment (BK-02, BK-21).
            foreach (var payment in await db.Payments
                         .Where(p => p.BookingId == booking.Id &&
                                     (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Failed))
                         .ToListAsync(cancellationToken))
            {
                payment.Status = PaymentStatus.Canceled;
                payment.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("On-site request {BookingId} declined by the host: dates released", booking.Id);
        await notifier.DeclinedAsync(booking.Id, string.IsNullOrWhiteSpace(messageToGuest) ? null : messageToGuest.Trim(), cancellationToken);
        return booking;
    }

    /// <summary>The request under the locks, if the host can still answer it; otherwise the reason as a domain error.</summary>
    private async Task<Booking> LoadAwaitingHostAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await ReadUnderLockAsync(bookingId, cancellationToken)
            ?? throw new NotFoundException("Booking not found");

        if (booking.Source != BookingSource.Direct || booking.PaymentOption != PaymentOption.OnSite)
            throw new DomainRuleException(OnSiteRequestErrorCodes.NotOnSiteRequest, "BookingNotOnSiteRequest");
        if (booking.Status != BookingStatus.Pending)
            throw new DomainConflictException(OnSiteRequestErrorCodes.NotPending, "OnSiteRequestNotPending");
        if (booking.RequestExpiresAt is not { } expiresAt || expiresAt < UtcNow())
            throw new DomainConflictException(OnSiteRequestErrorCodes.Expired, "OnSiteRequestExpired");
        if (booking.GuestEmailVerifiedAt is null)
            throw new DomainConflictException(OnSiteRequestErrorCodes.EmailNotConfirmed, "OnSiteRequestEmailNotConfirmed");

        return booking;
    }

    /// <summary>
    /// The booking as committed now. The context may already track it (the controller read it for the authorization,
    /// before the locks): a tracked entity is not refreshed by a query, so it is reloaded, otherwise the second of two
    /// concurrent answers would still see it pending.
    /// </summary>
    private async Task<Booking?> ReadUnderLockAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is not null)
            await db.Entry(booking).ReloadAsync(cancellationToken);
        return booking;
    }

    private async Task<IDbContextTransaction?> BeginLockedAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")));
        if (!db.Database.IsNpgsql())
            return transaction;

        try
        {
            await db.Database
                .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Bookings" WHERE "Id" = {bookingId} FOR UPDATE""")
                .ToListAsync(cancellationToken);
        }
        catch
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            throw;
        }

        return transaction;
    }

    /// <summary>Wrong booking or token: the same 404 either way, so the link tells nothing about other bookings.</summary>
    private static NotFoundException LinkInvalid() =>
        new("On-site request confirmation link not valid")
        {
            Code = OnSiteRequestErrorCodes.LinkInvalid,
            MessageKey = "OnSiteRequestLinkInvalid",
        };

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
