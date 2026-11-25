using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class GuestPortalRecovery(AppDbContext db, IGuestPortalLinkService links, TimeProvider clock)
{
    public async Task<string?> FindAsync(string slug, string bookingCode, string email, CancellationToken ct)
    {
        if (!BookingCodes.TryNormalize(bookingCode, out var code)
            || await OrgService.ResolveOrgIdBySlugAsync(db, slug.Trim(), ct) is not { } orgId)
            return null;

        var address = email.Trim().ToLowerInvariant();
        var booking = await db.Bookings.AsNoTracking().Where(b => b.OrgId == orgId && b.Org.IsActive
            && b.BookingCode == code && b.Guest.DataAnonymizedDate == null && b.Guest.Email.Trim().ToLower() == address)
            .SingleOrDefaultAsync(ct);
        if (booking is null || GuestPortalRules.IneligibleReason(booking, clock.GetUtcNow().UtcDateTime) is not null)
            return null;

        var link = await links.GetOrCreateAsync(orgId, booking.Id, null, ct);
        return link is null ? null : links.BuildUrl(link.Link);
    }
}
