using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Which stays the compliance summary, the Alloggiati list and the To do may name
/// (<see cref="OpenableStayRules.CanCompleteStay"/>).
/// </summary>
internal static class ListedStays
{
    internal readonly record struct Decision(bool DataComplete, bool Listed);

    public static async Task<Dictionary<Guid, Decision>> OfAsync(
        AppDbContext db,
        IReadOnlyCollection<Guid> bookingIds,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var decisions = new Dictionary<Guid, Decision>();
        if (bookingIds.Count == 0)
            return decisions;

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Where(b => bookingIds.Contains(b.Id))
            .Where(OpenableStayRules.CanShow)
            .ToListAsync(cancellationToken);
        var stored = (await db.StayGuests
                .AsNoTracking()
                .Where(g => bookingIds.Contains(g.BookingId))
                .ToListAsync(cancellationToken))
            .ToLookup(g => g.BookingId);

        foreach (var booking in bookings)
        {
            var guests = stored[booking.Id].OrderBy(g => g.Position).ToList();
            if (guests.Count == 0)
                guests.Add(StayGuest.FromBooker(booking, booking.Guest));

            var complete = AlloggiatiRecordRules.IsDataComplete(guests);
            decisions[booking.Id] = new Decision(
                complete,
                OpenableStayRules.CanCompleteStay(
                    booking.Property.Name,
                    booking.Guest.FirstName,
                    booking.Guest.LastName,
                    complete,
                    booking.CheckOutDate,
                    today));
        }

        return decisions;
    }
}
