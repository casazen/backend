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
        var stays = await EligibleStays(email)
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
        var stay = await EligibleStays(email).Where(b => b.Id == bookingId).Select(b => new { b.OrgId }).SingleOrDefaultAsync(ct)
                   ?? throw GuestAccountBookings.NotFound();
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(stay.OrgId);
        var links = scope.ServiceProvider.GetRequiredService<IGuestPortalLinkService>();
        var result = await links.GetOrCreateAsync(stay.OrgId, bookingId, null, ct) ?? throw GuestAccountBookings.NotFound();
        return Ok(new { url = links.BuildUrl(result.Link) });
    }

    private IQueryable<Booking> EligibleStays(string email)
    {
        var oldestDeparture = clock.TodayInRome().AddDays(-30);
        return db.Bookings.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
            .Where(b => b.Org.IsActive && b.NoShowAt == null && b.Guest.DataAnonymizedDate == null
                && b.CheckOutDate >= oldestDeparture && b.Guest.Email.Trim().ToLower() == email
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
