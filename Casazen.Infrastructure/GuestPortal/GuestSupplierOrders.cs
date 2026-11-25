using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.GuestPortal;

/// <summary>Catalog and ledger rules for the guest portal supplier sandbox.</summary>
public sealed class GuestSupplierOrders(AppDbContext db, TimeProvider clock)
{
    public const string SimulationSetting = "GuestPortal:SupplierOrderSimulation";
    public const int MaxPriceCents = 10_000_000;
    public static readonly TimeSpan DisputeWindow = TimeSpan.FromHours(24);

    public async Task<bool> AllowsRegionalSuppliersAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var json = await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(x => x.Id == propertyId && x.OrgId == orgId)
            .Select(x => x.GuestPortalSetupJson).SingleOrDefaultAsync(ct);
        return GuestPortalSetup.Read(json).RegionalSuppliers == true;
    }

    public IQueryable<Guid> TrustedSupplierIds(Guid orgId, Guid propertyId) =>
        db.TrustedSuppliers.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(x => x.OrgId == orgId && (x.AssignedPropertyIds.Count == 0 || x.AssignedPropertyIds.Contains(propertyId)))
            .Select(x => x.SupplierOrgId);

    public IQueryable<CatalogEntry> Catalog(Guid orgId, Guid propertyId, bool allowsRegionalSuppliers)
    {
        var trusted = TrustedSupplierIds(orgId, propertyId);
        return
        from offer in db.GuestSupplierOffers.AsNoTracking()
        join listing in db.SupplierServiceListings.IgnoreQueryFilters() on offer.ServiceId equals listing.Id
        join supplier in db.SupplierProfiles.IgnoreQueryFilters() on offer.SupplierOrgId equals supplier.OrgId
        where offer.HostOrgId == orgId && offer.PropertyId == propertyId && offer.Accepted && offer.Enabled
              && (trusted.Contains(offer.SupplierOrgId) ||
                  allowsRegionalSuppliers && supplier.GuestPortalMarketplaceEnabled)
              && listing.OrgId == supplier.OrgId && supplier.Status == SupplierStatus.Active
              && listing.Status == SupplierServiceListingStatus.Active && listing.DeletedAt == null
              && (listing.RequiresQuote || listing.PriceFromCents > 0)
        orderby listing.SortOrder, listing.Name
        select new CatalogEntry(offer.Id, listing.Name, supplier.LegalName, listing.Category,
            listing.Summary, listing.RequiresQuote || listing.PriceUnit != SupplierServicePriceUnit.PerJob || !listing.PricesIncludeVat ? null : listing.PriceFromCents, offer.CommissionBasisPoints, offer.HostOrigin);
    }

    public static int Commission(int price, int basisPoints) =>
        (int)Math.Round(price * (decimal)basisPoints / 10_000, MidpointRounding.AwayFromZero);

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public GuestSupplierOrderView View(GuestSupplierOrder order, bool includeInternal = false)
    {
        var state = order.State;
        if (state == GuestSupplierOrderState.Completed && Now >= order.CompletedAt!.Value + DisputeWindow)
            state = GuestSupplierOrderState.Released;

        var total = order.SupplierPriceCents + order.CommissionCents;
        var commissionRefund = total == 0
            ? 0
            : (int)Math.Round(order.RefundCents * (decimal)order.CommissionCents / total, MidpointRounding.AwayFromZero);

        return new GuestSupplierOrderView(order.Id, order.ServiceName, order.SupplierName, state.ToString(),
            order.RequestedFor, order.CompletedAt, order.SupplierPriceCents, order.CommissionCents,
            order.HostOrigin, total, order.RefundCents, commissionRefund, order.RequestNote, order.Complaint,
            includeInternal ? order.SupplierReply : null, order.Decision);
    }

    public sealed record CatalogEntry(Guid Id, string ServiceName, string SupplierName, string Category,
        string? Summary, int? PriceCents, int CommissionBasisPoints, bool HostOrigin);

    public sealed record GuestSupplierOrderView(Guid Id, string ServiceName, string SupplierName, string State,
        DateTime RequestedFor, DateTime? CompletedAt, int SupplierPriceCents, int CommissionCents,
        bool HostOrigin, int TotalCents, int RefundCents, int CommissionRefundCents, string? RequestNote,
        string? Complaint, string? SupplierReply, string? Decision);
}
