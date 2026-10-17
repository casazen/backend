using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IBookingTimelineService"/>
/// <remarks>
/// Up to five queries whatever the length of the history: the booking, its payments, its refunds, and, for a stay that is confirmed or
/// later, its check-in links and its Alloggiati reports. Every read is of the booking's org; the entries are built by
/// <see cref="BookingTimeline"/>. Nothing personal is read: no guest, no property.
/// </remarks>
public sealed class BookingTimelineService(AppDbContext db, TimeProvider? timeProvider = null) : IBookingTimelineService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<BookingTimelineEvent>?> GetAsync(
        Guid bookingId,
        bool includeAmounts,
        CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
            return null;

        var now = _clock.GetUtcNow();
        var orgId = booking.OrgId;

        var payments = (await db.Payments
                .AsNoTracking()
                .Where(p => p.OrgId == orgId && p.BookingId == bookingId)
                .Select(p => new { p.Status, p.Method, p.Amount, p.ProcessedAt, p.CreatedAt })
                .ToListAsync(cancellationToken))
            .Select(p => new BookingTimelinePayment(p.Status, p.Method, p.Amount, p.ProcessedAt ?? p.CreatedAt))
            .ToList();

        var refunds = (await db.PaymentRefunds
                .AsNoTracking()
                .Where(r => r.OrgId == orgId && r.Payment.BookingId == bookingId)
                .Select(r => new { r.Status, r.Amount, r.CompletedAt, r.CreatedAt })
                .ToListAsync(cancellationToken))
            .Select(r => new BookingTimelineRefund(r.Status, r.Amount, r.CompletedAt ?? r.CreatedAt))
            .ToList();

        // The link and the Alloggiati communication exist for a stay that is confirmed or later: a request, a hold or a cancelled booking has none.
        var checkIn = new BookingTimelineCheckIn(HostTodayCheckInState.NotSent, null, null);
        var alloggiati = new BookingTimelineAlloggiati(null, null);
        if (booking.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut)
        {
            checkIn = CheckInOf(await LoadSessionsAsync(orgId, bookingId, cancellationToken), now.UtcDateTime);
            alloggiati = await AlloggiatiOfAsync(booking, cancellationToken);
        }

        return BookingTimeline.Build(booking, new BookingTimelineFacts(payments, refunds, checkIn, alloggiati), now, includeAmounts);
    }

    // Only the columns the state of a link is read from (the hash of its token stays in the database).
    private async Task<List<GuestCheckInSession>> LoadSessionsAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken) =>
        await db.GuestCheckInSessions
            .AsNoTracking()
            .Where(s => s.OrgId == orgId && s.BookingId == bookingId)
            .Select(s => new GuestCheckInSession
            {
                Id = s.Id,
                Status = s.Status,
                ExpiresAt = s.ExpiresAt,
                SentAt = s.SentAt,
                LinkEmailStatus = s.LinkEmailStatus,
                CompletedAt = s.CompletedAt,
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync(cancellationToken);

    // The state is the one the Home reads (one rule: the link the guest submitted wins, else the latest). The link that counts is the
    // one the guest submitted, else the latest; one whose email failed went nowhere, so nothing was sent.
    private static BookingTimelineCheckIn CheckInOf(IReadOnlyList<GuestCheckInSession> sessions, DateTime nowUtc)
    {
        var state = HostTodayService.StateOf(sessions, nowUtc);
        var counting = (state == HostTodayCheckInState.Completed ? sessions.Where(s => s.IsCompleted) : sessions)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .FirstOrDefault();
        if (counting is null)
            return new BookingTimelineCheckIn(state, null, null);

        var linkAt = counting.LinkEmailStatus == GuestCheckInLinkEmailStatus.Failed ? (DateTime?)null : counting.SentAt ?? counting.CreatedAt;
        return new BookingTimelineCheckIn(state, linkAt, counting.CompletedAt);
    }

    // The report that stands for the booking (the same one the Alloggiati page and the Home read).
    private async Task<BookingTimelineAlloggiati> AlloggiatiOfAsync(Booking booking, CancellationToken cancellationToken)
    {
        var reports = await db.AlloggiatiWebReports
            .AsNoTracking()
            .Where(r => r.OrgId == booking.OrgId && r.BookingId == booking.Id)
            .Select(r => new AlloggiatiWebReport
            {
                Id = r.Id,
                GuestId = r.GuestId,
                Status = r.Status,
                ReportedAt = r.ReportedAt,
                UpdatedAt = r.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var report = AlloggiatiStatusRules.SlotReport(reports, booking.GuestId);
        return new BookingTimelineAlloggiati(report?.Status, report?.ReportedAt);
    }
}
