using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.DTOs.GuestMessages;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalChatController(AppDbContext db, IGuestMessageService messages) : GuestPortalControllerBase
{
    [HttpGet("chat")]
    public async Task<ActionResult<GuestThreadDto>> Get(CancellationToken ct)
    {
        var booking = await BookingAsync(ct);
        return booking is null ? LinkInvalid() : GuestThreadDto.From(await messages.GetForGuestAsync(booking, ct));
    }

    [HttpPost("chat")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<ActionResult<SentGuestMessageDto>> Send(
        SendMessageRequest request, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        var booking = await BookingAsync(ct);
        return booking is null ? LinkInvalid() : SentGuestMessageDto.From(await messages.SendAsGuestAsync(booking, request.Text, key, ct));
    }

    private Task<Booking?> BookingAsync(CancellationToken ct)
    {
        var portal = Portal;
        return db.Bookings.AsNoTracking().Include(b => b.Property).Include(b => b.Org).Include(b => b.Guest)
            .SingleOrDefaultAsync(b => b.Id == portal.BookingId && b.OrgId == portal.OrgId
                && b.NoShowAt == null && b.Guest.DataAnonymizedDate == null, ct);
    }
}
