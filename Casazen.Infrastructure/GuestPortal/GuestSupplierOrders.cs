using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
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

    public IQueryable<SupplierServiceListing> Listings(Guid orgId, Guid propertyId, bool allowsRegionalSuppliers) =>
        SupplierServiceCatalogService.GuestPortalListings(db, TrustedSupplierIds(orgId, propertyId), allowsRegionalSuppliers);

    public IQueryable<ManagementListing> ManagementListings(Guid hostOrgId, Guid propertyId, bool allowsRegional)
    {
        var assignedIds = TrustedSupplierIds(hostOrgId, propertyId);
        var hostOriginIds = SupplierServiceCatalogService.HostOriginSupplierIds(db, hostOrgId);
        return from item in Listings(hostOrgId, propertyId, allowsRegional)
               let hostOrigin = hostOriginIds.Contains(item.OrgId)
               where hostOrigin ? assignedIds.Contains(item.OrgId) : allowsRegional && item.SupplierProfile.GuestPortalMarketplaceEnabled
               select new ManagementListing
               {
                   Id = item.Id,
                   Name = item.Name,
                   Category = item.Category,
                   OrgId = item.OrgId,
                   Supplier = item.SupplierProfile.LegalName,
                   HostOrigin = hostOrigin
               };
    }

    public sealed class ManagementListing
    {
        public Guid Id { get; init; }
        public required string Name { get; init; }
        public required string Category { get; init; }
        public Guid OrgId { get; init; }
        public required string Supplier { get; init; }
        public bool HostOrigin { get; init; }
    }

    public IQueryable<OfferView> OfferViews(IQueryable<GuestSupplierOffer> offers) =>
        from o in offers.AsNoTracking()
        join service in SupplierServiceCatalogService.ListingsForOffers(db, offers.Select(x => x.SupplierOrgId))
            on new { o.ServiceId, o.SupplierOrgId } equals new { ServiceId = service.Id, SupplierOrgId = service.OrgId }
        join supplier in db.SupplierProfiles on o.SupplierOrgId equals supplier.OrgId
        join property in db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]) on o.PropertyId equals property.Id
        join host in db.Orgs on o.HostOrgId equals host.Id
        where property.OrgId == o.HostOrgId
        select new OfferView(o.Id, o.HostOrgId, o.PropertyId, o.ServiceId, o.SupplierOrgId,
            o.HostOrigin, o.CommissionBasisPoints, o.Accepted, o.Enabled, o.Version,
            service.Name, supplier.LegalName, property.Name, host.Name);

    public sealed record OfferView(Guid Id, Guid HostOrgId, Guid PropertyId, Guid ServiceId, Guid SupplierOrgId,
        bool HostOrigin, int CommissionBasisPoints, bool Accepted, bool Enabled, uint Version, string ServiceName,
        string SupplierName, string PropertyName, string HostName);

    public IQueryable<CatalogEntry> Catalog(Guid orgId, Guid propertyId, bool allowsRegionalSuppliers, Guid? offerId = null, bool fixedPriceOnly = false)
    {
        var trusted = TrustedSupplierIds(orgId, propertyId);
        return
        from offer in db.GuestSupplierOffers.AsNoTracking()
        join listing in Listings(orgId, propertyId, allowsRegionalSuppliers) on offer.ServiceId equals listing.Id
        join supplier in db.SupplierProfiles.IgnoreQueryFilters() on offer.SupplierOrgId equals supplier.OrgId
        where (offerId == null || offer.Id == offerId) && offer.HostOrgId == orgId && offer.PropertyId == propertyId && offer.Accepted && offer.Enabled
              && (offer.HostOrigin ? trusted.Contains(offer.SupplierOrgId) : allowsRegionalSuppliers && supplier.GuestPortalMarketplaceEnabled)
              && (!fixedPriceOnly || !listing.RequiresQuote && listing.PriceUnit == SupplierServicePriceUnit.PerJob &&
                  listing.PricesIncludeVat && listing.PriceFromCents > 0 && listing.PriceFromCents <= MaxPriceCents &&
                  (listing.PriceFromCents * (10_000L + offer.CommissionBasisPoints) + 5_000) / 10_000 >= GuestSupplierCheckout.MinTotalCents)
              && listing.OrgId == supplier.OrgId
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
        if (!order.StripeSandbox && state == GuestSupplierOrderState.Completed && Now >= order.CompletedAt!.Value + DisputeWindow)
            state = GuestSupplierOrderState.Released;

        var total = order.SupplierPriceCents + order.CommissionCents;
        var commissionRefund = total == 0
            ? 0
            : (int)Math.Round(order.RefundCents * (decimal)order.CommissionCents / total, MidpointRounding.AwayFromZero);

        return new GuestSupplierOrderView(order.Id, order.ServiceName, order.SupplierName, state.ToString(),
            order.RequestedFor, order.CompletedAt, order.SupplierPriceCents, order.CommissionCents,
            order.HostOrigin, total, order.RefundCents, commissionRefund, order.RequestNote, order.Complaint,
            includeInternal ? order.SupplierReply : null, order.Decision, order.StripeSandbox, order.RefundStatus == "retrying" ? "processing" : order.RefundStatus, order.RefundRequestedCents,
            includeInternal ? order.BankDisputeStatus : null)
        {
            Payout = includeInternal && order.StripeSandbox && (order.StripePayoutClaimedAt != null || order.StripePayoutFailureCode != null)
                ? new(order.StripeCardFeeCents, order.StripeSupplierTransferCents, order.StripeHostTransferCents,
                    order.StripeSupplierTransferId != null, order.StripeHostTransferId != null, order.StripeReleasedAt, order.StripePayoutFailureCode)
                : null
        };
    }

    public sealed record CatalogEntry(Guid Id, string ServiceName, string SupplierName, string Category,
        string? Summary, int? PriceCents, int CommissionBasisPoints, bool HostOrigin);

    public sealed record GuestSupplierOrderView(Guid Id, string ServiceName, string SupplierName, string State,
        DateTime RequestedFor, DateTime? CompletedAt, int SupplierPriceCents, int CommissionCents,
        bool HostOrigin, int TotalCents, int RefundCents, int CommissionRefundCents, string? RequestNote,
        string? Complaint, string? SupplierReply, string? Decision, bool StripeSandbox, string? RefundStatus, int RefundRequestedCents, string? BankDisputeStatus)
    {
        public GuestSupplierPayoutView? Payout { get; init; }
    }

    public sealed record GuestSupplierPayoutView(int? CardFeeCents, int SupplierTransferCents, int HostTransferCents,
        bool SupplierTransferred, bool HostTransferred, DateTime? ReleasedAt, string? HoldCode);
}
