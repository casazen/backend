using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Casazen.Web.Controllers;

public sealed class GuestStayChangeController(AppDbContext db, IHostBookingService bookings,
    IInAppNotificationPublisher notifications, TimeProvider clock) : GuestPortalControllerBase
{
    internal const string NotificationType = "guest-stay-change-requested";

    [HttpGet("stay-change")]
    public async Task<IActionResult> Read(CancellationToken ct)
    {
        var booking = await FindBooking(ct);
        if (booking is null || !Portal.IsDirect)
            return LinkInvalid();

        var latest = await Changes().OrderByDescending(change => change.CreatedAt).FirstOrDefaultAsync(ct);
        var (adults, children) = BookingGuestCounts.Of(booking);
        return Ok(new
        {
            canRequest = Eligible(booking) && latest is not { ApprovedAt: null, RejectedAt: null },
            adults, children, latest = latest is null ? null : StayChangeView.From(latest)
        });
    }

    [HttpPost("stay-change/quote")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Quote(StayChangeInput input, CancellationToken ct)
    {
        var booking = await FindBooking(ct);
        if (booking is null || !Portal.IsDirect)
            return LinkInvalid();

        var quote = await Quote(booking, input, ct);
        return Ok(new { quote.PreviousTotal, quote.NewTotal, quote.Currency });
    }

    [HttpPost("stay-change")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> RequestChange(StayChangeInput input, CancellationToken ct)
    {
        var booking = await FindBooking(ct);
        if (booking is null || !Portal.IsDirect)
            return LinkInvalid();

        var ages = input.ChildrenAges ?? [];
        var arrival = UtcDate(input.Arrival);
        var departure = UtcDate(input.Departure);
        var existing = await Changes().SingleOrDefaultAsync(change => change.ApprovedAt == null && change.RejectedAt == null, ct);
        if (existing is not null)
        {
            if (existing.Arrival == arrival && existing.Departure == departure && existing.Adults == input.Adults
                && existing.ChildrenAges.SequenceEqual(ages) && existing.ProposedTotal == input.ExpectedTotal)
                return Ok(StayChangeView.From(existing));

            throw new DomainConflictException("guest_stay_change_pending", "GuestStayChangePending");
        }

        var quote = await Quote(booking, input, ct);
        if (input.ExpectedTotal != quote.NewTotal)
            throw new DomainConflictException("guest_stay_change_stale", "GuestStayChangeStale");

        var change = new GuestStayChange
        {
            OrgId = booking.OrgId, BookingId = booking.Id,
            OriginalArrival = booking.CheckInDate, OriginalDeparture = booking.CheckOutDate,
            OriginalGuests = booking.NumberOfGuests, OriginalChildren = booking.NumberOfChildren,
            Arrival = arrival, Departure = departure, Adults = input.Adults, ChildrenAges = ages,
            OriginalTotal = quote.PreviousTotal, ProposedTotal = quote.NewTotal,
            Currency = quote.Currency, CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.GuestStayChanges.Add(change);
        var recipients = new[] { booking.Property.OwnerId, booking.Property.ResponsibleUserId }
            .Where(user => !string.IsNullOrWhiteSpace(user)).Distinct().ToArray();
        foreach (var user in recipients)
        {
            db.InAppNotifications.Add(new InAppNotification
            {
                OrgId = booking.OrgId, UserId = user!, Type = NotificationType, EntityId = booking.Id,
                DeliveryKey = $"guest-stay-change:{change.Id:N}", CreatedAt = change.CreatedAt
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UIX_GuestStayChanges_Pending" })
        {
            throw new DomainConflictException("guest_stay_change_pending", "GuestStayChangePending");
        }

        foreach (var user in recipients)
            await notifications.PublishAsync(user!, NotificationType, booking.Id, ct);
        return Ok(StayChangeView.From(change));
    }

    private async Task<BookingEditQuote> Quote(Booking booking, StayChangeInput input, CancellationToken ct)
    {
        var ages = input.ChildrenAges ?? [];
        var (adults, children) = BookingGuestCounts.Of(booking);
        if (!Eligible(booking) || ages.Any(age => age is < 0 or > 17)
            || input.Arrival < clock.TodayInRomeAsDateOnly() || input.Departure == default
            || (input.Arrival == DateOnly.FromDateTime(booking.CheckInDate)
                && input.Departure == DateOnly.FromDateTime(booking.CheckOutDate)
                && input.Adults == adults && ages.Length == children))
            throw new DomainRuleException("guest_stay_change_invalid", "GuestStayChangeInvalid");

        return await bookings.QuoteEditAsync(new HostBookingUpdate(booking.Id, UtcDate(input.Arrival),
            UtcDate(input.Departure), input.Adults + ages.Length, ages.Length, ages, booking.SpecialRequests, GuestWillPay: true), ct);
    }

    private Task<Booking?> FindBooking(CancellationToken ct) => db.Bookings.AsNoTracking().Include(b => b.Property)
        .SingleOrDefaultAsync(b => b.Id == Portal.BookingId && b.OrgId == Portal.OrgId && b.PropertyId == Portal.PropertyId, ct);

    private IQueryable<GuestStayChange> Changes() => db.GuestStayChanges.AsNoTracking()
        .Where(change => change.BookingId == Portal.BookingId && change.OrgId == Portal.OrgId);

    private bool Eligible(Booking booking) => booking.Source == BookingSource.Direct && booking.Status == BookingStatus.Confirmed
        && booking.NoShowAt is null && booking.CheckInDate.Date >= clock.TodayInRome();

    private static DateTime UtcDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public sealed record StayChangeInput(DateOnly Arrival, DateOnly Departure, [Range(1, 100)] int Adults,
        [MaxLength(100)] int[]? ChildrenAges, decimal? ExpectedTotal);

    public sealed record StayChangeView(Guid Id, DateOnly Arrival, DateOnly Departure, int Adults, int Children,
        decimal PreviousTotal, decimal NewTotal, string Currency, DateTime CreatedAt, DateTime? ApprovedAt, DateTime? RejectedAt)
    {
        public static StayChangeView From(GuestStayChange change) => new(change.Id, DateOnly.FromDateTime(change.Arrival),
            DateOnly.FromDateTime(change.Departure), change.Adults, change.ChildrenAges.Length, change.OriginalTotal,
            change.ProposedTotal, change.Currency, change.CreatedAt, change.ApprovedAt, change.RejectedAt);
    }
}
