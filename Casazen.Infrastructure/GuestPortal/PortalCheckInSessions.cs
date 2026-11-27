using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

public sealed class PortalCheckInSessions(
    AppDbContext db, IGuestPortalLinkService links, IFeatureFlags flags, TimeProvider clock)
{
    private const string Prefix = "stay_";

    public static bool IsProof(string token) =>
        token.StartsWith(Prefix, StringComparison.Ordinal)
        && GuestPortalRules.IsWellFormedToken(token[Prefix.Length..]);

    public IQueryable<GuestCheckInSession> ForStay(Guid orgId, Guid bookingId) =>
        db.GuestCheckInSessions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(s => s.BookingId == bookingId && s.OrgId == orgId);

    public IQueryable<GuestCheckInSession> Usable(IQueryable<GuestCheckInSession> sessions)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return sessions.Where(s => s.ExpiresAt >= now && s.Status != GuestCheckInSessionStatus.Scaduto)
            .OrderByDescending(s => s.Status == GuestCheckInSessionStatus.Completo || s.Status == GuestCheckInSessionStatus.AlloggiatiInviato)
            .ThenByDescending(s => s.CreatedAt);
    }

    internal async Task<IQueryable<GuestCheckInSession>> ResolveAsync(string proof, CancellationToken ct = default)
    {
        var token = proof[Prefix.Length..];
        if (!flags.IsEnabled(FeatureFlags.GuestPortal) || await links.ResolveAsync(token, ct) is not { } link)
            return db.GuestCheckInSessions.Where(_ => false);

        var tomorrow = clock.TodayInRome().AddDays(1);
        return Usable(ForStay(link.OrgId, link.BookingId)
            .Where(s => s.Booking.CheckOutDate >= tomorrow && s.Booking.NoShowAt == null
                && (s.Booking.Status == BookingStatus.Confirmed || s.Booking.Status == BookingStatus.CheckedIn)));
    }
}
