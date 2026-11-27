using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/guest/stays")]
[Authorize(Policy = CasazenPolicies.GuestAccount)]
[FeatureGate(FeatureFlags.GuestPortal)]
[PrivateAnswer]
public sealed class GuestAccountStaysController(
    AppDbContext db, IAccountEmailResolver accountEmails, IServiceScopeFactory scopes, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountRead)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var email = await VerifiedEmailAsync(ct);
        var attached = AttachedLinks(User.GetUserId()!);
        var stays = await EligibleStays().Where(b => b.Guest.Email.Trim().ToLower() == email ||
                attached.Any(l => l.BookingId == b.Id && l.OrgId == b.OrgId))
            .OrderByDescending(b => b.CheckInDate).ThenBy(b => b.Id).Take(50)
            .Select(b => new
            {
                b.Id,
                property = b.Property.Name,
                host = b.Org.DisplayName,
                b.CheckInDate,
                b.CheckOutDate,
            }).ToListAsync(ct);
        return Ok(stays);
    }

    [HttpPost("{bookingId:guid}/open")]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountSession)]
    public async Task<IActionResult> Open(Guid bookingId, CancellationToken ct)
    {
        var email = await VerifiedEmailAsync(ct);
        var stay = await EligibleStays().Where(b => b.Id == bookingId).Select(b => new { b.OrgId, b.Guest.Email }).SingleOrDefaultAsync(ct)
                   ?? throw GuestAccountBookings.NotFound();
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(stay.OrgId);
        var links = scope.ServiceProvider.GetRequiredService<IGuestPortalLinkService>();
        if (!string.Equals(stay.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            var attached = await AttachedLinks(User.GetUserId()!).SingleOrDefaultAsync(l => l.BookingId == bookingId && l.OrgId == stay.OrgId, ct);
            if (attached is null) throw GuestAccountBookings.NotFound();
            return Ok(new { url = links.BuildUrl(attached) });
        }

        var result = await links.GetOrCreateAsync(stay.OrgId, bookingId, null, ct) ?? throw GuestAccountBookings.NotFound();
        return Ok(new { url = links.BuildUrl(result.Link) });
    }

    [HttpPost("attach")]
    [EnableRateLimiting(RateLimitPolicies.GuestAccountSession)]
    public async Task<IActionResult> Attach(AttachStay request, [FromServices] IGuestPortalLinkService links, CancellationToken ct)
    {
        var email = await VerifiedEmailAsync(ct);
        var link = await links.ResolveAsync(request.Token, ct) ?? throw GuestAccountBookings.NotFound();
        var booking = await EligibleStays().Where(b => b.Id == link.BookingId && b.OrgId == link.OrgId)
            .Select(b => new { b.Source, b.Guest.Email }).SingleOrDefaultAsync(ct);
        if (booking is null || !FiscalCopy.IsOtaBookingSource(booking.Source)) throw GuestAccountBookings.NotFound();
        if (string.Equals(booking.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase)) return NoContent();
        var domain = booking.Email?.Trim().Split('@').LastOrDefault()?.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(booking.Email) && domain is not ("guest.booking.com" or "guest.airbnb.com" or "m.guest.airbnb.com"))
            throw GuestAccountBookings.NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        var subject = User.GetUserId()!;
        var eligible = EligibleStays();
        var changed = await db.GuestPortalLinks.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(l => l.Id == link.Id && l.OrgId == link.OrgId && l.BookingId == link.BookingId &&
                l.RevokedAt == null && l.ExpiresAt > now && (l.AccountUserId == null || l.AccountUserId == subject) &&
                eligible.Any(b => b.OrgId == l.OrgId && b.Id == l.BookingId && b.Guest.Email == booking.Email))
            .ExecuteUpdateAsync(set => set.SetProperty(l => l.AccountUserId, subject)
                .SetProperty(l => l.AccountAttachedAt, l => l.AccountAttachedAt ?? now), ct);
        if (changed == 0) throw GuestAccountBookings.NotFound();
        return NoContent();
    }

    public sealed record AttachStay(string Token);

    private IQueryable<GuestPortalLink> AttachedLinks(string subject)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.GuestPortalLinks.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
            .Where(l => l.AccountUserId == subject && l.RevokedAt == null && l.ExpiresAt > now);
    }

    private IQueryable<Booking> EligibleStays()
    {
        var oldestDeparture = clock.TodayInRome().AddDays(-30);
        return db.Bookings.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
            .Where(b => b.Org.IsActive && b.NoShowAt == null && b.Guest.DataAnonymizedDate == null
                && b.CheckOutDate >= oldestDeparture
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut));
    }

    private async Task<string> VerifiedEmailAsync(CancellationToken ct)
    {
        var (email, verified) = await accountEmails.ResolveAsync(User, User.GetUserId()!, ct);
        if (!verified || string.IsNullOrWhiteSpace(email))
            throw new DomainForbiddenException(GuestAccountErrorCodes.EmailNotVerified, "GuestEmailNotVerified");

        return email.Trim().ToLowerInvariant();
    }
}
