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
    IHostResourceLookup resources, IAuthorizationService authorization, IOrgContextResolver orgs, IHostSupplierCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid propertyId, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var offers = await Offers(orgId, propertyId).AsNoTracking().ToListAsync(ct);
        var history = await Orders(orgId, propertyId).AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(new { offers, orders = history.Select(x => orders.View(x, true)) });
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Search(Guid propertyId, [FromQuery] string? search, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var term = search?.Trim();
        if (term is not { Length: >= 2 and <= 100 })
            return BadRequest();

        var allowRegional = await orders.AllowsRegionalSuppliersAsync(orgId, propertyId, ct);
        var trusted = orders.TrustedSupplierIds(orgId, propertyId);
        var matches = await db.SupplierServiceListings.AsNoTracking()
            .Where(x => x.Status == SupplierServiceListingStatus.Active && x.SupplierProfile.Status == SupplierStatus.Active)
            .Where(x => trusted.Contains(x.OrgId) || allowRegional && x.SupplierProfile.ShowcaseSlug != null &&
                !db.SupplierShowcaseContents.Any(s => s.OrgId == x.OrgId && (s.PublishedJson == null || s.PausedAt != null)))
            .Where(x => x.Name.ToLower().Contains(term.ToLower()) || x.SupplierProfile.LegalName.ToLower().Contains(term.ToLower()))
            .OrderBy(x => x.Name)
            .Take(30)
            .Select(x => new { x.Id, x.Name, x.Category, x.OrgId, Supplier = x.SupplierProfile.LegalName })
            .ToListAsync(ct);
        return Ok(matches);
    }

    [HttpPut("offers/{serviceId:guid}")]
    public async Task<IActionResult> Configure(Guid propertyId, Guid serviceId, OfferTerms request, CancellationToken ct)
    {
        var (orgId, failure) = await ScopeAsync(propertyId, ct);
        if (failure is not null)
            return failure;
        var existing = await Offers(orgId, propertyId).SingleOrDefaultAsync(x => x.ServiceId == serviceId, ct);
        if (existing is null)
        {
            var listing = await db.SupplierServiceListings.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Id == serviceId && x.Status == SupplierServiceListingStatus.Active && x.DeletedAt == null)
                .Select(x => new { x.OrgId, x.SupplierProfile.Email, x.SupplierProfile.ShowcaseSlug, x.SupplierProfile.Status, x.SupplierProfile.CreatedAt })
                .SingleOrDefaultAsync(ct);
            if (listing is null || listing.Status != SupplierStatus.Active)
                return NotFound();

            var trusted = await orders.TrustedSupplierIds(orgId, propertyId).ContainsAsync(listing.OrgId, ct);
            if (!trusted && (listing.ShowcaseSlug is null || !await orders.AllowsRegionalSuppliersAsync(orgId, propertyId, ct)
                || await db.SupplierShowcaseContents.AnyAsync(s => s.OrgId == listing.OrgId && (s.PublishedJson == null || s.PausedAt != null), ct)))
                return NotFound();

            await catalog.ListServicesAsync(orgId, propertyId, listing.OrgId, null, ct);

            var email = listing.Email.Trim().ToLowerInvariant();
            var origin = email.Length == 0 ? null : await db.SupplierInviteRecords.IgnoreQueryFilters()
                .Where(x => x.IsUsed && x.Email.Trim().ToLower() == email && x.CreatedAt <= listing.CreatedAt && x.ExpiresAt > listing.CreatedAt)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.InvitedByOrgId).FirstOrDefaultAsync(ct);
            existing = new GuestSupplierOffer
            {
                HostOrgId = orgId, PropertyId = propertyId, ServiceId = serviceId,
                SupplierOrgId = listing.OrgId, HostOrigin = trusted && origin == orgId
            };
            db.GuestSupplierOffers.Add(existing);
        }
        var basisPoints = existing.HostOrigin
            ? request.CommissionBasisPoints
            : configuration.GetValue("GuestPortal:SupplierSimulationMarketplaceCommissionBasisPoints", 1000);
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
        var order = await Orders(orgId, propertyId).SingleOrDefaultAsync(x => x.Id == id && x.HostOrigin, ct);
        if (order is null)
            return NotFound();
        return await GuestSupplierDecisions.ResolveAsync(db, orders, order, request, ct);
    }

    private IQueryable<GuestSupplierOffer> Offers(Guid orgId, Guid propertyId) =>
        db.GuestSupplierOffers.Where(x => x.HostOrgId == orgId && x.PropertyId == propertyId);

    private IQueryable<GuestSupplierOrder> Orders(Guid orgId, Guid propertyId) =>
        db.GuestSupplierOrders.Where(x => x.HostOrgId == orgId && x.PropertyId == propertyId);

    private async Task<(Guid Org, ActionResult? Failure)> ScopeAsync(Guid propertyId, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>(GuestSupplierOrders.SimulationSetting))
            return (Guid.Empty, NotFound());
        var orgId = await orgs.GetOrProvisionOrgIdAsync(ct);
        var property = await resources.ForPropertyAsync(propertyId, ct);
        if (orgId is null || property is null)
            return (Guid.Empty, NotFound());
        return await authorization.IsAuthorizedAsync(User, property, PropertyOperations.Write)
            ? (orgId.Value, null) : (Guid.Empty, Forbid());
    }

    public sealed record OfferTerms([property: Range(0, 5000)] int CommissionBasisPoints, bool Enabled);
}

[ApiController]
[Route("api/supplier/guest-orders")]
[Authorize(Policy = CasazenPolicies.Supplier)]
[FeatureGate(FeatureFlags.GuestPortal)]
[FeatureGate(FeatureFlags.GuestPortalSupplierServices)]
[PrivateAnswer]
public sealed class SupplierGuestOrdersController(AppDbContext db, GuestSupplierOrders orders, IConfiguration configuration,
    ISupplierOrgContextResolver orgs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await SupplierAsync(ct) is not { } id)
            return NotFound();

        var offers = await db.GuestSupplierOffers.AsNoTracking().Where(x => x.SupplierOrgId == id && x.Enabled).ToListAsync(ct);
        var history = await db.GuestSupplierOrders.AsNoTracking().Where(x => x.SupplierOrgId == id)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        return Ok(new { offers, orders = history.Select(x => orders.View(x, true)) });
    }

    [HttpPost("offers/{id:guid}/consent")]
    public async Task<IActionResult> Consent(Guid id, ConsentTerms terms, CancellationToken ct)
    {
        if (await SupplierAsync(ct) is not { } supplier)
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

        switch (request.Action)
        {
            case "quote" when order.State == GuestSupplierOrderState.PendingQuote &&
                    request.PriceCents is { } price && order.RequestedFor > orders.Now:
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
        configuration.GetValue<bool>(GuestSupplierOrders.SimulationSetting) ? await orgs.GetLinkedSupplierOrgIdAsync(ct) : null;

    public sealed record ConsentTerms(bool Accepted, int CommissionBasisPoints, uint Version);
    public sealed record SupplierTransition(
        [property: Required, MaxLength(20)] string Action,
        [property: Range(1, GuestSupplierOrders.MaxPriceCents)] int? PriceCents,
        [property: MaxLength(2000)] string? Text);
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
        if (!configuration.GetValue<bool>(GuestSupplierOrders.SimulationSetting))
            return NotFound();

        var open = await db.GuestSupplierOrders.AsNoTracking()
            .Where(x => !x.HostOrigin && x.State == GuestSupplierOrderState.Disputed)
            .OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return Ok(open.Select(x => orders.View(x, true)));
    }

    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, GuestSupplierDecision request, CancellationToken ct)
    {
        if (!configuration.GetValue<bool>(GuestSupplierOrders.SimulationSetting))
            return NotFound();
        var order = await db.GuestSupplierOrders.SingleOrDefaultAsync(x => x.Id == id && !x.HostOrigin, ct);
        if (order is null)
            return NotFound();
        return await GuestSupplierDecisions.ResolveAsync(db, orders, order, request, ct);
    }
}

public sealed record GuestSupplierDecision([property: Range(0, int.MaxValue)] int RefundCents,
    [property: Required, MaxLength(2000)] string Text);

internal static class GuestSupplierDecisions
{
    internal static async Task<IActionResult> ResolveAsync(AppDbContext db, GuestSupplierOrders orders, GuestSupplierOrder order,
        GuestSupplierDecision request, CancellationToken ct)
    {
        if (order.State != GuestSupplierOrderState.Disputed || request.RefundCents > order.SupplierPriceCents + order.CommissionCents)
            return new ConflictResult();

        order.RefundCents = request.RefundCents;
        order.Decision = request.Text.Trim();
        order.State = GuestSupplierOrderState.Resolved;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new ConflictResult(); }
        return new OkObjectResult(orders.View(order, true));
    }
}
