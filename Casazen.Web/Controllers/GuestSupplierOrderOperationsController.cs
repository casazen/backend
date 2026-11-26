using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{propertyId:guid}/guest-supplier-orders")]
[Authorize(Policy = CasazenPolicies.GuestPortalManage)]
[FeatureGate(FeatureFlags.GuestPortal)]
[FeatureGate(FeatureFlags.GuestPortalSupplierServices)]
[PrivateAnswer]
public sealed class HostGuestSupplierOrdersController(AppDbContext db, GuestSupplierOrders orders, IConfiguration configuration,
    IHostResourceLookup resources, IAuthorizationService authorization, IOrgContextResolver orgs, IHostSupplierCatalogService catalog, ITrustedSupplierService invitations, ISupplierComuneMatcher comuneMatcher,
    Microsoft.Extensions.Options.IOptions<Casazen.Core.Options.SupplierPaymentsOptions> paymentOptions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid propertyId, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var offers = await orders.OfferViews(Offers(orgId, propertyId)).ToListAsync(ct);
        var history = GuestSupplierCheckout.OrdersEnabled(configuration)
            ? await Orders(orgId, propertyId).AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct) : [];
        return Ok(new { offers, orders = history.Select(x => orders.View(x, x.HostOrigin) with { Complaint = x.HostOrigin ? x.Complaint : null }) });
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite(Guid propertyId, InviteSupplier request, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var actor = User.GetUserId();
        if (actor is null)
            return Unauthorized();

        await invitations.InviteSupplierAsync(orgId, new InviteSupplierRequest(request.Email, [propertyId], request.VatNumber, request.FiscalCode, request.Phone), actor);
        return NoContent();
    }

    public sealed record InviteSupplier([Required, EmailAddress, MaxLength(255)] string Email,
        [MaxLength(32)] string? VatNumber = null, [MaxLength(24)] string? FiscalCode = null, [MaxLength(50)] string? Phone = null);

    [HttpGet("catalog")]
    public async Task<IActionResult> Search(Guid propertyId, [FromQuery] string? search, CancellationToken ct, [FromQuery] string? origin = null)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var term = search?.Trim().ToLowerInvariant();
        if (term is not { Length: >= 2 and <= 100 } || origin is not (null or "host" or "marketplace"))
            return BadRequest();

        var property = await db.Properties.AsNoTracking().SingleAsync(x => x.Id == propertyId && x.OrgId == orgId, ct);
        var allowRegional = await orders.AllowsRegionalSuppliersAsync(orgId, propertyId, ct);
        var candidates = orders.ManagementListings(orgId, propertyId, allowRegional)
            .Where(x => (origin == null || x.HostOrigin == (origin == "host")) &&
                (x.Name.ToLower().Contains(term) || x.Supplier.ToLower().Contains(term)));
        var profiles = await db.SupplierProfiles.AsNoTracking()
            .Where(p => candidates.Any(x => x.OrgId == p.OrgId)).ToListAsync(ct);
        var localIds = (await comuneMatcher.FilterAsync(profiles, Casazen.Core.Suppliers.ComuneTarget.ForProperty(property), ct))
            .Select(p => p.OrgId).ToArray();
        var marketplaceRate = MarketplaceRate;
        var matches = await (
            from item in candidates
            where localIds.Contains(item.OrgId)
            orderby item.Name, item.Id
            select new
            {
                item.Id,
                item.Name,
                item.Category,
                item.OrgId,
                item.Supplier,
                item.HostOrigin,
                CommissionBasisPoints = item.HostOrigin
                    ? db.GuestSupplierOffers.Where(o => o.PropertyId == propertyId && o.ServiceId == item.Id)
                        .Select(o => o.CommissionBasisPoints).FirstOrDefault()
                    : marketplaceRate
            }
        ).Take(30).ToListAsync(ct);
        return Ok(matches);
    }

    [HttpPut("offers/{serviceId:guid}")]
    public async Task<IActionResult> Configure(Guid propertyId, Guid serviceId, OfferTerms request, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var existing = await Offers(orgId, propertyId).SingleOrDefaultAsync(x => x.ServiceId == serviceId, ct);
        if (existing is null || request.Enabled)
        {
            var allowRegional = await orders.AllowsRegionalSuppliersAsync(orgId, propertyId, ct);
            var listing = await orders.ManagementListings(orgId, propertyId, allowRegional)
                .SingleOrDefaultAsync(x => x.Id == serviceId, ct);
            if (listing is null)
                return NotFound();
            await catalog.ListServicesAsync(orgId, propertyId, listing.OrgId, null, ct);

            if (existing is null)
            {
                existing = new GuestSupplierOffer
                {
                    HostOrgId = orgId,
                    PropertyId = propertyId,
                    ServiceId = serviceId,
                    SupplierOrgId = listing.OrgId,
                    HostOrigin = listing.HostOrigin
                };
                db.GuestSupplierOffers.Add(existing);
            }
        }
        var basisPoints = existing.HostOrigin
            ? request.CommissionBasisPoints
            : MarketplaceRate;
        if (basisPoints is < 0 or > 5000)
            return BadRequest();
        if (existing.CommissionBasisPoints != basisPoints)
            existing.Accepted = false;
        existing.CommissionBasisPoints = basisPoints;
        existing.Enabled = request.Enabled;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_GuestSupplierOffers_PropertyId_ServiceId" })
        { return Conflict(); }
        return Ok(existing);
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid propertyId, Guid id, GuestSupplierDecision request, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        if (!GuestSupplierCheckout.OrdersEnabled(configuration))
            return NotFound();
        var order = await Orders(orgId, propertyId).SingleOrDefaultAsync(x => x.Id == id && x.HostOrigin, ct);
        if (order is null)
            return NotFound();
        return await GuestSupplierDecisions.ResolveAsync(db, orders, order, request, ct);
    }

    private int MarketplaceRate => decimal.ToInt32(decimal.Round(paymentOptions.Value.RequireCommissionPercent() * 100m, 0, MidpointRounding.AwayFromZero));

    private IQueryable<GuestSupplierOffer> Offers(Guid orgId, Guid propertyId) =>
        db.GuestSupplierOffers.Where(x => x.HostOrgId == orgId && x.PropertyId == propertyId);

    private IQueryable<GuestSupplierOrder> Orders(Guid orgId, Guid propertyId) =>
        db.GuestSupplierOrders.Where(x => x.HostOrgId == orgId && x.PropertyId == propertyId);

    private async Task<(Guid Org, ActionResult? Failure)> ScopeAsync(Guid propertyId, CancellationToken ct)
    {
        var orgId = await orgs.GetOrProvisionOrgIdAsync(ct);
        var property = await resources.ForPropertyAsync(propertyId, ct);
        if (orgId is null || property is null)
            return (Guid.Empty, NotFound());
        return await authorization.IsAuthorizedAsync(User, property, PropertyOperations.Write)
            ? (orgId.Value, null) : (Guid.Empty, Forbid());
    }

    public sealed record OfferTerms([Range(0, 5000)] int CommissionBasisPoints, bool Enabled);
}

[ApiController]
[Route("api/supplier/guest-orders")]
[Authorize(Policy = CasazenPolicies.Supplier)]
[FeatureGate(FeatureFlags.GuestPortal)]
[FeatureGate(FeatureFlags.GuestPortalSupplierServices)]
[PrivateAnswer]
public sealed class SupplierGuestOrdersController(AppDbContext db, GuestSupplierOrders orders, IConfiguration configuration,
    ISupplierOrgContextResolver orgs, GuestSupplierCheckout? checkout = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await orgs.GetLinkedSupplierOrgIdAsync(ct) is not { } id)
            return NotFound();

        var offers = await orders.OfferViews(db.GuestSupplierOffers.Where(x => x.SupplierOrgId == id && x.Enabled)).ToListAsync(ct);
        var history = GuestSupplierCheckout.OrdersEnabled(configuration)
            ? await db.GuestSupplierOrders.AsNoTracking().Where(x => x.SupplierOrgId == id)
                .OrderByDescending(x => x.CreatedAt).ToListAsync(ct) : [];
        return Ok(new { offers, orders = history.Select(x => orders.View(x, true)) });
    }

    [HttpPost("offers/{id:guid}/consent")]
    public async Task<IActionResult> Consent(Guid id, ConsentTerms terms, CancellationToken ct)
    {
        if (await orgs.GetLinkedSupplierOrgIdAsync(ct) is not { } supplier)
            return NotFound();
        var offer = await db.GuestSupplierOffers.SingleOrDefaultAsync(x => x.Id == id && x.SupplierOrgId == supplier, ct);
        if (offer is null)
            return NotFound();
        if (!offer.Enabled || offer.CommissionBasisPoints != terms.CommissionBasisPoints || offer.Version != terms.Version)
            return Conflict();

        offer.Accepted = terms.Accepted;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Ok(offer);
    }

    [HttpPost("{id:guid}/transition")]
    public async Task<IActionResult> Transition(Guid id, SupplierTransition request, CancellationToken ct)
    {
        if (await SupplierAsync(ct) is not { } supplier)
            return NotFound();
        var order = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.Id == id && x.SupplierOrgId == supplier, ct);
        if (order is null)
            return NotFound();

        if (order.StripeSandbox && request.Action is "accept" or "decline")
        {
            if (!GuestSupplierCheckout.Enabled(configuration) || checkout is null) return NotFound();
            return await checkout.SupplierActionAsync(order, request.Action, ct) ? Ok(orders.View(order, true)) : Conflict();
        }
        if (!order.StripeSandbox && !configuration.GetValue<bool>(GuestSupplierOrders.SimulationSetting)) return Conflict();

        switch (request.Action)
        {
            case "quote" when order.State == GuestSupplierOrderState.PendingQuote &&
                    request.PriceCents is { } price && order.RequestedFor > orders.Now:
                if (order.StripeSandbox && price + GuestSupplierOrders.Commission(price, order.CommissionBasisPoints) < GuestSupplierCheckout.MinTotalCents)
                    return BadRequest();
                order.SupplierPriceCents = price;
                order.CommissionCents = GuestSupplierOrders.Commission(price, order.CommissionBasisPoints);
                order.State = GuestSupplierOrderState.Quoted;
                break;
            case "accept" when order.State is GuestSupplierOrderState.Authorized or GuestSupplierOrderState.CardSaved &&
                    order.RequestedFor > orders.Now:
                order.State = GuestSupplierOrderState.Accepted;
                break;
            case "decline" when order.State is GuestSupplierOrderState.PendingQuote or GuestSupplierOrderState.Quoted
                    or GuestSupplierOrderState.Authorized or GuestSupplierOrderState.CardSaved:
                order.State = GuestSupplierOrderState.Declined;
                break;
            case "complete" when order.State == GuestSupplierOrderState.Accepted:
                order.CompletedAt = orders.Now;
                order.State = GuestSupplierOrderState.Completed;
                break;
            case "reply" when order.State == GuestSupplierOrderState.Disputed && order.SupplierReply is null &&
                    !string.IsNullOrWhiteSpace(request.Text):
                order.SupplierReply = request.Text.Trim();
                break;
            default:
                return Conflict();
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Ok(orders.View(order, true));
    }

    private async Task<Guid?> SupplierAsync(CancellationToken ct) =>
        GuestSupplierCheckout.OrdersEnabled(configuration) ? await orgs.GetLinkedSupplierOrgIdAsync(ct) : null;

    public sealed record ConsentTerms(bool Accepted, int CommissionBasisPoints, uint Version);
    public sealed record SupplierTransition(
        [Required, MaxLength(20)] string Action,
        [Range(1, GuestSupplierOrders.MaxPriceCents)] int? PriceCents,
        [MaxLength(2000)] string? Text);
}

[ApiController]
[Route("api/admin/guest-supplier-orders")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
[FeatureGate(FeatureFlags.GuestPortal)]
[FeatureGate(FeatureFlags.GuestPortalSupplierServices)]
[PrivateAnswer]
public sealed class AdminGuestSupplierOrdersController(AppDbContext db, GuestSupplierOrders orders, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!GuestSupplierCheckout.OrdersEnabled(configuration))
            return NotFound();

        var open = await db.GuestSupplierOrders.AsNoTracking()
            .Where(x => !x.HostOrigin && x.State == GuestSupplierOrderState.Disputed)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return Ok(open.Select(x => orders.View(x, true)));
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, GuestSupplierDecision request, CancellationToken ct)
    {
        if (!GuestSupplierCheckout.OrdersEnabled(configuration))
            return NotFound();
        var order = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.Id == id && !x.HostOrigin, ct);
        if (order is null)
            return NotFound();
        return await GuestSupplierDecisions.ResolveAsync(db, orders, order, request, ct);
    }
}

public sealed record GuestSupplierDecision([Range(0, int.MaxValue)] int RefundCents,
    [Required, MaxLength(2000)] string Text);

internal static class GuestSupplierDecisions
{
    internal static async Task<IActionResult> ResolveAsync(AppDbContext db, GuestSupplierOrders orders, GuestSupplierOrder order,
        GuestSupplierDecision request, CancellationToken ct)
    {
        if (order.StripeSandbox && request.RefundCents != 0 || order.State != GuestSupplierOrderState.Disputed || request.RefundCents > order.SupplierPriceCents + order.CommissionCents)
            return new ConflictResult();

        order.RefundCents = request.RefundCents;
        order.Decision = request.Text.Trim();
        order.State = GuestSupplierOrderState.Resolved;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new ConflictResult(); }
        return new OkObjectResult(orders.View(order, true));
    }
}
