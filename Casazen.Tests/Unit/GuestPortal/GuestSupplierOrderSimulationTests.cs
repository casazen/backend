using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestSupplierOrderSimulationTests
{
    [Fact]
    public void CatalogLookupByOfferIdTranslatesOnPostgres()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=casazen_design;Username=postgres;Password=postgres").Options);
        var sql = new GuestSupplierOrders(db, TimeProvider.System)
            .Catalog(Guid.NewGuid(), Guid.NewGuid(), true, Guid.NewGuid())
            .Take(2).ToQueryString();

        Assert.Contains(".\"Id\" = @", sql, StringComparison.Ordinal);
        Assert.Contains(".\"HostOrgId\" = @", sql, StringComparison.Ordinal);
        Assert.Contains(".\"PropertyId\" = @", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateSupplierStaysWithItsHostAfterPartnershipAndRequiresAssignedProperty()
    {
        using var world = new GuestPortalTestWorld();
        var stay = await world.ContextAsync();
        var partner = new SupplierProfile
        {
            OrgId = Guid.NewGuid(),
            LegalName = "Local",
            GuestPortalMarketplaceEnabled = false,
            Status = SupplierStatus.Active
        };
        var listing = new SupplierServiceListing
        {
            OrgId = partner.OrgId,
            Category = "cleaning",
            Slug = "cleaning",
            Name = "Cleaning",
            Status = SupplierServiceListingStatus.Active
        };
        var trusted = new TrustedSupplier
        {
            OrgId = stay.OrgId,
            SupplierOrgId = partner.OrgId,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            AssignedPropertyIds = [stay.PropertyId]
        };
        world.Db.AddRange(partner, listing, trusted);
        await world.Db.SaveChangesAsync();
        var orders = new GuestSupplierOrders(world.Db, TimeProvider.System);
        var otherOrg = Guid.NewGuid();
        Assert.Empty(await orders.ManagementListings(otherOrg, stay.PropertyId, true).ToListAsync());
        Assert.Empty(await orders.ManagementListings(stay.OrgId, Guid.NewGuid(), false).ToListAsync());
        Assert.True((await orders.ManagementListings(stay.OrgId, stay.PropertyId, false).SingleAsync()).HostOrigin);
        partner.GuestPortalMarketplaceEnabled = true;
        partner.GuestPortalMarketplaceFirstPublishedAt = trusted.CreatedAt.AddHours(1);
        await world.Db.SaveChangesAsync();
        Assert.True((await orders.ManagementListings(stay.OrgId, stay.PropertyId, false).SingleAsync()).HostOrigin);
        Assert.False((await orders.ManagementListings(otherOrg, stay.PropertyId, true).SingleAsync()).HostOrigin);
        Assert.Empty(await orders.ManagementListings(otherOrg, stay.PropertyId, false).ToListAsync());
    }

    [Fact]
    public async Task Quote_Authorize_Accept_Complete_Dispute_AndRefundAreScopedToTheParticipants()
    {
        using var world = new GuestPortalTestWorld();
        var context = await world.ContextAsync();
        var supplierId = Guid.NewGuid();
        var listing = new SupplierServiceListing
        {
            OrgId = supplierId,
            Name = "Transfer",
            Category = "transport",
            Slug = "transfer",
            RequiresQuote = true,
            Status = SupplierServiceListingStatus.Active
        };
        world.Db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierId,
            LegalName = "Example Supplier",
            Email = "supplier@example.com",
            GuestPortalMarketplaceEnabled = true,
            Status = SupplierStatus.Active
        });
        world.Db.SupplierServiceListings.Add(listing);
        var offer = new GuestSupplierOffer
        {
            HostOrgId = context.OrgId,
            PropertyId = context.PropertyId,
            ServiceId = listing.Id,
            SupplierOrgId = supplierId,
            Enabled = true,
            CommissionBasisPoints = 1000
        };
        world.Db.GuestSupplierOffers.Add(offer);
        await world.Db.SaveChangesAsync(CancellationToken.None);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [GuestSupplierOrders.SimulationSetting] = "true"
        }).Build();
        var service = new GuestSupplierOrders(world.Db, world.Clock);
        var guest = new GuestPortalSupplierOrdersController(world.Db, service, config, [new DefaultGuestPortalPolicy()]);
        var accessor = new TestPortalAccessor(context);
        using var services = new ServiceCollection().AddSingleton<IGuestPortalContextAccessor>(accessor).BuildServiceProvider();
        guest.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        var ct = CancellationToken.None;
        var input = new GuestPortalSupplierOrdersController.CreateOrder(Guid.NewGuid(), offer.Id,
            new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), "At the station");
        Assert.IsType<NotFoundResult>(await guest.Create(input, ct));
        var supplier = new SupplierGuestOrdersController(world.Db, service, config, new SupplierOrg(supplierId));
        var consent = new SupplierGuestOrdersController.ConsentTerms(true, 1000, offer.Version);
        Assert.IsType<OkObjectResult>(await supplier.Consent(offer.Id, consent, ct));

        var property = await world.Db.Properties.FindAsync([context.PropertyId], ct);
        property!.GuestPortalSetupJson = new GuestPortalSetup { RegionalSuppliers = false }.Write();
        await world.Db.SaveChangesAsync(ct);
        Assert.IsType<NotFoundResult>(await guest.Create(input, ct));
        property.GuestPortalSetupJson = new GuestPortalSetup { RegionalSuppliers = true }.Write();
        await world.Db.SaveChangesAsync(ct);

        var order = View(await guest.Create(input, ct));
        Assert.Equal("PendingQuote", order.State);
        Assert.Equal(order.Id, View(await guest.Create(input, ct)).Id);
        var outsider = new SupplierGuestOrdersController(world.Db, service, config, new SupplierOrg(Guid.NewGuid()));
        var quote = new SupplierGuestOrdersController.SupplierTransition("quote", 12345, null);
        Assert.IsType<NotFoundResult>(await outsider.Transition(order.Id, quote, ct));
        order = View(await supplier.Transition(order.Id, quote, ct));
        Assert.Equal(13580, order.TotalCents);
        Assert.IsType<ConflictResult>(await guest.AuthorizeOrder(order.Id, new(order.TotalCents - 1), ct));

        accessor.Value = context with { BookingId = Guid.NewGuid() };
        Assert.IsType<NotFoundResult>(await guest.AuthorizeOrder(order.Id, new(order.TotalCents), ct));
        accessor.Value = context;
        Assert.Equal("Authorized", View(await guest.AuthorizeOrder(order.Id, new(order.TotalCents), ct)).State);
        Assert.Equal("Accepted", View(await supplier.Transition(order.Id, new("accept", null, null), ct)).State);
        Assert.Equal("Completed", View(await supplier.Transition(order.Id, new("complete", null, null), ct)).State);
        Assert.Equal("Disputed", View(await guest.Dispute(order.Id, new("Service did not match the offer"), ct)).State);
        Assert.Equal("Supplier evidence", View(await supplier.Transition(order.Id, new("reply", null, "Supplier evidence"), ct)).SupplierReply);

        var admin = new AdminGuestSupplierOrdersController(world.Db, service, config);
        Assert.IsType<ConflictResult>(await admin.Resolve(order.Id, new(order.TotalCents + 1, "Invalid refund"), ct));
        var decision = View(await admin.Resolve(order.Id, new(6790, "Partial refund"), ct));
        Assert.Equal("Resolved", decision.State);
        Assert.Equal(6790, decision.RefundCents);
        Assert.Equal(618, decision.CommissionRefundCents);
    }

    private static GuestSupplierOrders.GuestSupplierOrderView View(IActionResult result) =>
        Assert.IsType<GuestSupplierOrders.GuestSupplierOrderView>(Assert.IsType<OkObjectResult>(result).Value);

    private sealed class TestPortalAccessor(PortalContext value) : IGuestPortalContextAccessor
    {
        public PortalContext Value { get; set; } = value;
        public PortalContext Current => Value;
    }

    private sealed class SupplierOrg(Guid id) : ISupplierOrgContextResolver
    {
        public Task<Guid?> GetOrProvisionSupplierOrgIdAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public Task<Guid?> GetLinkedSupplierOrgIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(id);
    }
}
