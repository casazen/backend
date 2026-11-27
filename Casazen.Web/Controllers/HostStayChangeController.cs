using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/bookings/{bookingId:guid}/guest-stay-change")]
[FeatureGate(FeatureFlags.GuestPortal)]
[Authorize(Policy = CasazenPolicies.BookingWrite)]
[PrivateAnswer]
public sealed class HostStayChangeController(AppDbContext db, IHostResourceLookup resources,
    IAuthorizationService authorization, IHostBookingService bookings, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Read(Guid bookingId, CancellationToken ct)
    {
        var (booking, _, denied) = await Access(bookingId, ct);
        if (denied is not null)
            return denied;

        var change = await Changes(booking!).Where(change => change.ApprovedAt == null && change.RejectedAt == null).SingleOrDefaultAsync(ct);
        return Ok(new { pending = change is null ? null : GuestStayChangeController.StayChangeView.From(change) });
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid bookingId, Guid id, CancellationToken ct)
    {
        var (booking, resource, denied) = await Access(bookingId, ct);
        if (denied is not null)
            return denied;

        var change = await Changes(booking!).SingleOrDefaultAsync(change => change.Id == id, ct);
        if (change is null)
            return NotFound();
        if (change.ApprovedAt is not null)
            return NoContent();

        var canMoveMoney = await authorization.IsAuthorizedAsync(User, resource!, PaymentOperations.Write);
        await bookings.UpdateAsync(new HostBookingUpdate(bookingId, change.Arrival, change.Departure,
            change.Adults + change.ChildrenAges.Length, change.ChildrenAges.Length, change.ChildrenAges, booking!.SpecialRequests,
            RequestedByUserId: User.GetUserId(), CanMoveMoney: canMoveMoney, GuestStayChangeId: id, GuestWillPay: true),
            CancellationToken.None);
        return NoContent();
    }

    [HttpPost("{id:guid}/decline")]
    public async Task<IActionResult> Decline(Guid bookingId, Guid id, CancellationToken ct)
    {
        var (booking, _, denied) = await Access(bookingId, ct);
        if (denied is not null)
            return denied;

        var changes = Changes(booking!).Where(change => change.Id == id);
        var now = clock.GetUtcNow().UtcDateTime;
        var changed = await changes.Where(change => change.ApprovedAt == null && change.RejectedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(change => change.RejectedAt, now), ct);
        if (changed == 1 || await changes.AnyAsync(change => change.RejectedAt != null, ct))
            return NoContent();
        if (await changes.AnyAsync(ct))
            throw new DomainConflictException("guest_stay_change_stale", "GuestStayChangeStale");

        return NotFound();
    }

    private async Task<(Booking?, HostResource?, IActionResult?)> Access(Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null || await resources.ForPropertyAsync(booking.PropertyId, ct) is not { } property)
            return (null, null, NotFound());

        var scope = property with { OrgId = booking.OrgId };
        if (!await authorization.IsAuthorizedAsync(User, scope, BookingOperations.Write))
            return (null, null, Forbid());
        return (booking, scope, null);
    }

    private IQueryable<GuestStayChange> Changes(Booking booking) => db.GuestStayChanges.AsNoTracking()
        .Where(change => change.BookingId == booking.Id && change.OrgId == booking.OrgId);
}
