using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalStayController(AppDbContext db, PortalCheckInSessions sessions, TimeProvider clock,
    IAccessCodeService accessCodes, IFeatureFlags flags, GuestHouseGuideStorage guides)
    : GuestPortalControllerBase
{
    [HttpGet("stay")]
    public async Task<ActionResult<StayDetails>> GetStay(CancellationToken ct)
    {
        var portal = Portal;
        var tomorrow = clock.TodayInRome().AddDays(1);
        var canEdit = portal.Status == BookingStatus.Confirmed && portal.CheckOutDate >= tomorrow;
        var available = portal.CheckOutDate >= tomorrow && (portal.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn)
            && await sessions.Usable(sessions.ForStay(portal.OrgId, portal.BookingId)).AnyAsync(ct);

        var row = await Booking().AsNoTracking().Select(b => new
        {
            Address = portal.CanSeeReserved ? b.Property.Address : null,
            b.ExpectedArrivalTime,
            b.NoShowAt,
            b.Property.GuestHouseGuideJson,
            b.Property.HouseRules
        }).SingleOrDefaultAsync(ct);
        if (row is null)
            return LinkInvalid();

        var guide = guides.Read(portal.PropertyId, row.GuestHouseGuideJson).ForVisitor(portal.CanSeeReserved);
        if (portal.CanSeeReserved && !string.IsNullOrWhiteSpace(guide.Access))
        {
            var accessAllowed = flags.IsEnabled(FeatureFlags.CheckInIdentityVerification)
                ? await accessCodes.GetReleasableCodeAsync(portal.BookingId, AccessCodeReleaseChannels.Screen, ct) is not null
                : await accessCodes.GetHoldReasonAsync(portal.BookingId, ct) is null;
            if (!accessAllowed)
                guide = guide with { Access = null };
        }

        return new StayDetails(row.Address, row.HouseRules, row.ExpectedArrivalTime,
            canEdit && row.NoShowAt is null, available && row.NoShowAt is null, guide);
    }

    [HttpPost("access-code")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> AccessCode(CancellationToken ct)
    {
        if (!Portal.CanSeeReserved || !flags.IsEnabled(FeatureFlags.CheckInIdentityVerification))
            return LinkInvalid();

        var code = await accessCodes.ReleaseToGuestAsync(Portal.BookingId, AccessCodeReleaseChannels.Screen, ct);
        return code is null ? LinkInvalid() : Ok(new { code });
    }

    [HttpPut("arrival")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> SetArrival(ArrivalRequest request, CancellationToken ct)
    {
        if (!ArrivalTimeRules.TryNormalize(request.Time, out var arrival))
            return BadRequest();

        var now = clock.GetUtcNow().UtcDateTime;
        var tomorrow = clock.TodayInRome().AddDays(1);
        var changed = await Booking().Where(b =>
                b.CheckOutDate >= tomorrow && b.Status == BookingStatus.Confirmed && b.NoShowAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ExpectedArrivalTime, arrival).SetProperty(b => b.UpdatedAt, now), ct);
        return changed == 1 ? NoContent() : LinkInvalid();
    }

    private IQueryable<Booking> Booking()
    {
        var portal = Portal;
        return db.Bookings.Where(b => b.Id == portal.BookingId && b.OrgId == portal.OrgId && b.PropertyId == portal.PropertyId);
    }

    public sealed record StayDetails(string? Address, string HouseRules, string? ArrivalTime, bool CanEditArrival, bool CheckInAvailable, GuestHouseGuide Guide);

    public sealed record ArrivalRequest(string? Time);
}
