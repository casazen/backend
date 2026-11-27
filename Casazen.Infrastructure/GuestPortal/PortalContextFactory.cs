using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

/// <summary>
/// Makes the <see cref="PortalContext"/> of a resolved stay link (E50/F01/US01/T02, decision A3): the booking, the org, the property, the language, the
/// kind of booking and what the guest may see. The only place that sets <see cref="PortalContext.CanSeeReserved"/>: today it is the completed check-in of a
/// stay to come or in progress (<see cref="GuestPortalRules.IsStayOpenToReservedData"/>: not a cancelled stay, a no-show or a stay checked out); the check-in
/// of the portal (E51) refines it here.
/// </summary>
/// <remarks>
/// Every read names the org and the booking of the link and ignores the tenant filter of the request (only that one: the soft delete of the properties
/// stays), because the token is the tenant of a portal request whoever is signed in. A booking that is gone, of another org than the link's, or whose
/// property was deleted gives no context: the same 404 as a token that does not open.
/// </remarks>
public class PortalContextFactory(AppDbContext db)
{
    public virtual async Task<PortalContext?> CreateAsync(GuestPortalLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        var booking = await (
                from b in db.Bookings.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                join o in db.Orgs on b.OrgId equals o.Id
                where b.Id == link.BookingId && b.OrgId == link.OrgId
                select new { b.PropertyId, b.Status, b.NoShowAt, b.Source, b.CheckInDate, b.CheckOutDate, o.DefaultLanguage })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
            return null;

        // The soft-delete filter of the properties stays: a deleted house opens nothing.
        var propertyExists = await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AnyAsync(p => p.Id == booking.PropertyId && p.OrgId == link.OrgId, cancellationToken);
        if (!propertyExists)
            return null;

        // The check-in sessions of the stay, latest first: whether one is completed (the one definition, GuestCheckInSession.IsCompleted) and the
        // language the guest chose last.
        var sessions = await db.GuestCheckInSessions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => s.BookingId == link.BookingId && s.OrgId == link.OrgId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Status, s.Language })
            .ToListAsync(cancellationToken);
        var checkInCompleted = sessions.Any(s => new GuestCheckInSession { Status = s.Status }.IsCompleted);
        var sessionLanguage = sessions.Select(s => s.Language).FirstOrDefault(l => GuestLanguages.Normalize(l) is not null);

        return new PortalContext
        {
            LinkId = link.Id,
            OrgId = link.OrgId,
            BookingId = link.BookingId,
            PropertyId = booking.PropertyId,
            Language = GuestLanguages.Resolve(sessionLanguage, booking.DefaultLanguage),
            Status = booking.Status,
            Source = booking.Source,
            IsOta = FiscalCopy.IsOtaBookingSource(booking.Source),
            IsDirect = booking.Source == BookingSource.Direct,
            CheckInCompleted = checkInCompleted,

            // Reserved data (the precise address, the access, the door code, the Wi-Fi) only after the check-in (§5.1, AC-NF153-3) and only for a stay
            // to come or in progress: a cancelled stay, a no-show and one already checked out keep the link but get none, as the lock code (GuestPortalRules).
            CanSeeReserved = checkInCompleted && GuestPortalRules.IsStayOpenToReservedData(booking.Status, booking.NoShowAt),
            CheckInDate = booking.CheckInDate,
            CheckOutDate = booking.CheckOutDate,
        };
    }
}
