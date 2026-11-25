using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Authorize(Policy = CasazenPolicies.GuestPortalManage)]
[FeatureGate(FeatureFlags.GuestPortal)]
[PrivateAnswer]
public sealed class GuestHostExtrasController(
    AppDbContext db, IHostResourceLookup resources, IAuthorizationService authorization, GuestHostExtraService extras) : ControllerBase
{
    [HttpGet("api/properties/{propertyId:guid}/guest-host-extras")]
    public async Task<IActionResult> Get(Guid propertyId, CancellationToken ct)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();
        var records = await db.GuestHostExtras.AsNoTracking().Where(x => x.OrgId == scope.OrgId)
            .OrderBy(x => x.Name).ToListAsync(ct);
        return Ok(records.Select(x => new
        {
            x.Id, x.Name, x.Description, x.Price, x.Pricing, x.Inventory, x.CutoffHours, x.DuringStay, x.RequiresApproval,
            x.Calendar, Published = x.PropertyIds.Contains(propertyId)
        }));
    }

    [HttpPut("api/properties/{propertyId:guid}/guest-host-extras/{id:guid}")]
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> Save(Guid propertyId, Guid id, ExtraInput input, CancellationToken ct)
    {
        var scope = await resources.ForPropertyAsync(propertyId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, PropertyOperations.Write))
            return NotFound();

        if (string.IsNullOrWhiteSpace(input.Name) || input.Price != decimal.Round(input.Price, 2) ||
            !Enum.IsDefined(input.Pricing) || !Enum.IsDefined(input.Calendar) ||
            (input.Calendar == GuestHostExtraCalendar.EarlyCheckIn && input.DuringStay))
            return BadRequest();

        var (savedId, forbidden) = await extras.SaveAsync(scope.OrgId, propertyId, id, new GuestHostExtra
        {
            Name = input.Name.Trim(), Description = input.Description?.Trim() ?? "", Price = input.Price,
            Pricing = input.Pricing, Inventory = input.Inventory, CutoffHours = input.CutoffHours,
            DuringStay = input.DuringStay, RequiresApproval = input.RequiresApproval, Calendar = input.Calendar,
        }, input.Published, async otherId =>
        {
            var other = await resources.ForPropertyAsync(otherId, ct);
            return other is not null && await authorization.IsAuthorizedAsync(User, other, PropertyOperations.Write);
        }, ct);
        return forbidden ? Forbid() : savedId is { } itemId ? Ok(itemId) : NotFound();
    }

    [HttpGet("api/bookings/{bookingId:guid}/guest-host-extras")]
    public async Task<IActionResult> Orders(Guid bookingId, CancellationToken ct)
    {
        var scope = await resources.ForBookingAsync(bookingId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, BookingOperations.Write))
            return NotFound();

        var orders = await db.GuestHostExtraOrders.AsNoTracking().Where(x => x.OrgId == scope.OrgId && x.BookingId == bookingId)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(orders.Select(GuestHostExtraOrderView.From));
    }

    [HttpPut("api/bookings/{bookingId:guid}/guest-host-extras/{id:guid}")]
    public async Task<IActionResult> ChangeStatus(Guid bookingId, Guid id, OrderStatusInput input, CancellationToken ct)
    {
        var scope = await resources.ForBookingAsync(bookingId, ct);
        if (scope is null || !await authorization.IsAuthorizedAsync(User, scope, BookingOperations.Write))
            return NotFound();
        return await extras.ChangeStatusAsync(scope.OrgId, bookingId, id, input.Status, true, ct) ? NoContent() : NotFound();
    }

    public sealed record OrderStatusInput(GuestHostExtraStatus Status);
    public sealed record ExtraInput(
        [property: Required, MaxLength(120)] string Name,
        [property: MaxLength(2000)] string? Description,
        [property: Range(typeof(decimal), "0.50", "10000.00")] decimal Price,
        GuestHostExtraPricing Pricing,
        [property: Range(1, 10000)] int? Inventory,
        [property: Range(0, 720)] int CutoffHours,
        bool DuringStay, bool RequiresApproval, GuestHostExtraCalendar Calendar, bool Published);
}

public sealed class PublicGuestHostExtrasController(GuestHostExtraService extras) : GuestPortalControllerBase
{
    [HttpGet("host-extras")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var (offers, orders) = await extras.ListAsync(Portal, ct);
        return Ok(new { offers, orders });
    }

    [HttpPost("host-extras/{extraId:guid}/orders")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Order(Guid extraId, OrderInput input, CancellationToken ct) =>
        Ok(await extras.OrderAsync(Portal, extraId, input.Quantity, input.RequestId, ct));

    [HttpPost("host-extra-orders/{id:guid}/payment")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Pay(Guid id, CancellationToken ct) => Ok(await extras.PayAsync(Portal, id, ct));

    [HttpDelete("host-extra-orders/{id:guid}")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        await extras.ChangeStatusAsync(Portal.OrgId, Portal.BookingId, id, GuestHostExtraStatus.Cancelled, false, ct)
            ? NoContent() : NotFound();

    public sealed record OrderInput(int Quantity, Guid RequestId);
}
