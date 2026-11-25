using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.GuestPortal;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[FeatureGate(FeatureFlags.GuestPortalSupplierServices)]
public sealed class GuestPortalSupplierOrdersController(
    AppDbContext db, GuestSupplierOrders orders, IConfiguration configuration, IEnumerable<IGuestPortalPolicy> policies)
    : GuestPortalControllerBase
{
    [HttpGet("supplier-orders")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!Sandbox)
            return NotFound();

        var offers = await CanOrderAsync(ct) ? await (await CatalogAsync(ct)).ToListAsync(ct) : [];
        var history = await StayOrders().AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(new { environment = StripeSandbox ? "stripe-sandbox" : "simulation", offers, orders = history.Select(x => orders.View(x)) });
    }

    [HttpPost("supplier-orders")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Create(CreateOrder request, CancellationToken ct)
    {
        if (!Sandbox)
            return NotFound();
        if (request.RequestId == Guid.Empty || request.RequestedFor.Offset != TimeSpan.Zero)
            return BadRequest();
        var existing = await StayOrders().SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
        if (existing is not null)
            return existing.OfferId == request.OfferId && existing.RequestedFor == request.RequestedFor.UtcDateTime &&
                   existing.RequestNote == request.Note?.Trim()
                ? Ok(orders.View(existing))
                : Conflict();
        if (!await CanOrderAsync(ct))
            return Conflict();

        var requestedFor = request.RequestedFor.UtcDateTime;
        if (requestedFor <= orders.Now || RomeCalendar.TodayAt(requestedFor) < Portal.CheckInDate.Date ||
            RomeCalendar.TodayAt(requestedFor) > Portal.CheckOutDate.Date)
            return BadRequest();

        var entry = await (await CatalogAsync(ct, request.OfferId)).SingleOrDefaultAsync(ct);
        if (entry is null)
            return NotFound();
        var offer = await db.GuestSupplierOffers.SingleAsync(x => x.Id == entry.Id, ct);
        var price = entry.PriceCents.GetValueOrDefault();
        if (price > GuestSupplierOrders.MaxPriceCents || StripeSandbox && (price <= 0 ||
            price + GuestSupplierOrders.Commission(price, entry.CommissionBasisPoints) < GuestSupplierCheckout.MinTotalCents ||
            requestedFor > orders.Now + GuestSupplierCheckout.Horizon))
            return BadRequest();

        var order = new GuestSupplierOrder
        {
            HostOrgId = Portal.OrgId,
            PropertyId = Portal.PropertyId,
            BookingId = Portal.BookingId,
            SupplierOrgId = offer.SupplierOrgId,
            HostOrigin = entry.HostOrigin,
            OfferId = offer.Id,
            RequestId = request.RequestId,
            StripeSandbox = StripeSandbox,
            ServiceName = entry.ServiceName,
            SupplierName = entry.SupplierName,
            RequestedFor = requestedFor,
            CreatedAt = orders.Now,
            RequestNote = request.Note?.Trim(),
            CommissionBasisPoints = entry.CommissionBasisPoints,
            SupplierPriceCents = price,
            CommissionCents = GuestSupplierOrders.Commission(price, entry.CommissionBasisPoints),
            State = price == 0 ? GuestSupplierOrderState.PendingQuote : GuestSupplierOrderState.Quoted
        };
        db.GuestSupplierOrders.Add(order);
        // The unique order key applies even if two identical submissions concurrently passed the initial read.
        var inserted = await SaveOrderAsync(ct);
        if (inserted)
            return Ok(orders.View(order));

        db.Entry(order).State = EntityState.Detached;
        existing = await StayOrders().AsNoTracking().SingleAsync(x => x.RequestId == request.RequestId, ct);
        return existing.OfferId == order.OfferId && existing.RequestedFor == order.RequestedFor &&
               existing.RequestNote == order.RequestNote ? Ok(orders.View(existing)) : Conflict();
    }

    [HttpPost("supplier-orders/{id:guid}/authorize")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> AuthorizeOrder(Guid id, AuthorizeOrderRequest request, CancellationToken ct)
    {
        if (!Sandbox)
            return NotFound();
        var order = await StayOrders().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (order is null)
            return NotFound();
        if (order.StripeSandbox || !configuration.GetValue<bool>(GuestSupplierOrders.SimulationSetting) ||
            !await CanOrderAsync(ct) || order.State != GuestSupplierOrderState.Quoted ||
            order.RequestedFor <= orders.Now || request.TotalCents != order.SupplierPriceCents + order.CommissionCents)
            return Conflict();

        order.State = order.RequestedFor - orders.Now <= TimeSpan.FromDays(7)
            ? GuestSupplierOrderState.Authorized : GuestSupplierOrderState.CardSaved;
        return await SaveTransitionAsync(order, ct);
    }

    [HttpPost("supplier-orders/{id:guid}/dispute")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Dispute(Guid id, OpenDispute request, CancellationToken ct)
    {
        if (!Sandbox)
            return NotFound();
        var order = await StayOrders().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (order is null)
            return NotFound();

        var complaint = request.Text.Trim();
        if (complaint.Length == 0)
            return BadRequest();
        if (order.State != GuestSupplierOrderState.Completed || orders.Now >= order.CompletedAt!.Value + GuestSupplierOrders.DisputeWindow)
            return Conflict();

        order.Complaint = complaint;
        order.State = GuestSupplierOrderState.Disputed;
        return await SaveTransitionAsync(order, ct);
    }

    private bool StripeSandbox => GuestSupplierCheckout.Enabled(configuration);
    private bool Sandbox => GuestSupplierCheckout.OrdersEnabled(configuration);

    [HttpPost("supplier-orders/{id:guid}/checkout")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Checkout(Guid id, AuthorizeOrderRequest request, [FromServices] GuestSupplierCheckout checkout, CancellationToken ct)
    {
        if (!StripeSandbox) return NotFound();
        var order = await StayOrders().SingleOrDefaultAsync(x => x.Id == id && x.StripeSandbox, ct);
        if (order is null) return NotFound();
        if (!await CanOrderAsync(ct)) return Conflict();
        var result = await checkout.StartAsync(order, request.TotalCents, ct);
        return result is null ? Conflict() : Ok(result);
    }

    [HttpPost("supplier-orders/{id:guid}/reconcile")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Reconcile(Guid id, [FromServices] GuestSupplierCheckout checkout, CancellationToken ct)
    {
        if (!StripeSandbox) return NotFound();
        var order = await StayOrders().SingleOrDefaultAsync(x => x.Id == id && x.StripeSandbox, ct);
        if (order is null) return NotFound();
        return await checkout.ReconcileAsync(order, ct) ? Ok(orders.View(order)) : Conflict();
    }

    private async Task<IQueryable<GuestSupplierOrders.CatalogEntry>> CatalogAsync(CancellationToken ct, Guid? offerId = null)
    {
        return orders.Catalog(Portal.OrgId, Portal.PropertyId,
            await orders.AllowsRegionalSuppliersAsync(Portal.OrgId, Portal.PropertyId, ct), offerId, fixedPriceOnly: StripeSandbox);
    }

    private IQueryable<GuestSupplierOrder> StayOrders() => db.GuestSupplierOrders
        .Where(x => x.HostOrgId == Portal.OrgId && x.BookingId == Portal.BookingId && x.PropertyId == Portal.PropertyId);

    private async Task<bool> CanOrderAsync(CancellationToken ct)
    {
        if (Portal.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn) || RomeCalendar.TodayAt(orders.Now) > Portal.CheckOutDate.Date)
            return false;
        foreach (var policy in policies)
            if (!await policy.CanShowPaidServicesAsync(Portal, ct))
                return false;
        return true;
    }

    private async Task<bool> SaveOrderAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_GuestSupplierOrders_BookingId_RequestId" })
        { return false; }
        return true;
    }

    private async Task<IActionResult> SaveTransitionAsync(GuestSupplierOrder order, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Ok(orders.View(order));
    }

    public sealed record CreateOrder(Guid RequestId, Guid OfferId, DateTimeOffset RequestedFor,
        [MaxLength(2000)] string? Note);
    public sealed record AuthorizeOrderRequest([Range(1, int.MaxValue)] int TotalCents);
    public sealed record OpenDispute([Required, MaxLength(2000)] string Text);
}
