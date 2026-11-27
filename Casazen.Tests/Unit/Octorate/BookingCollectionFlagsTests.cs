using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Octorate;

public class BookingCollectionFlagsTests
{
    private static readonly BookingStateMapper Mapper = new();

    [Theory]
    [InlineData("""{ "id": "1", "companyCollect": "COMPANY" }""", "COMPANY")]
    [InlineData("""{ "id": "1", "companyCollect": " company " }""", "COMPANY")]
    [InlineData("""{ "id": "1", "companyCollect": "HOTEL" }""", "HOTEL")]
    [InlineData("""{ "id": "1", "companyCollect": "HOTEl" }""", "HOTEL")]
    [InlineData("""{ "id": "1", "companyCollect": "NONE" }""", "NONE")]
    [InlineData("""{ "id": "1", "companyCollect": "SOMEONE" }""", null)]
    [InlineData("""{ "id": "1", "companyCollect": "" }""", null)]
    [InlineData("""{ "id": "1", "companyCollect": 1 }""", null)]
    [InlineData("""{ "id": "1" }""", null)]
    public void Read_CompanyCollect_IsOneOfTheThreeWordsOfTheOpenApi_AndNullForAnythingElse(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        var reservation = OctorateReservationReader.Read(document.RootElement)!;

        Assert.Equal(expected, reservation.CompanyCollect);
        Assert.Null(reservation.CommissionIncludedInPayments);
    }

    [Theory]
    [InlineData("COMPANY", true)]
    [InlineData("HOTEL", false)]
    [InlineData("NONE", null)]
    [InlineData(null, null)]
    public void ToOtaCollection_TheChannelCollectsOrTheHostDoes_AndNoneOrNothingSaysNothing(string? companyCollect, bool? expected)
    {
        var reservation = Res("7001") with { CompanyCollect = companyCollect };

        Assert.Equal(expected, Mapper.ToOtaCollection(reservation).CompanyCollect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void ToOtaCollection_TheCommissionFlagIsTheOneTakenFromTheCatalog_Carried_AsItIs(bool? fromTheCatalog)
    {
        var reservation = Res("7001") with { CompanyCollect = "COMPANY", CommissionIncludedInPayments = fromTheCatalog };

        Assert.Equal(new OtaCollection(true, fromTheCatalog), Mapper.ToOtaCollection(reservation));
    }

    [Fact]
    public async Task BookingImport_ABookingOfAChannel_GetsWhoCollectsAndTheCatalogFlag_OnCreateAndOnANewerVersion()
    {
        var h = new BookingImportHarness();
        await SeedPortalAsync(h, "274", commissionIncludedInPayments: true);
        await using var db = h.NewDb();
        var service = h.Service(db);

        h.Serve(Res("7001") with { CompanyCollect = "COMPANY" });
        var created = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal(1, created.Created);
        var first = await h.ReadBookingAsync("7001");
        Assert.Equal((true, true), (first.OtaCompanyCollect, first.OtaCommissionIncludedInPayments));

        await SetPortalFlagAsync(h, "274", commissionIncludedInPayments: false);
        h.Serve(Res("7001") with { UpdatedAt = SecondVersion, CompanyCollect = "HOTEL" });
        var updated = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(1, updated.Updated);
        var second = await h.ReadBookingAsync("7001");
        Assert.Equal((false, false), (second.OtaCompanyCollect, second.OtaCommissionIncludedInPayments));
        Assert.Equal(SecondVersion, second.OctorateUpdatedAt);
        var events = await h.ReadEventsAsync();
        Assert.DoesNotContain(events, e => e.EventType == DomainEventType.BookingModified);
    }

    [Fact]
    public async Task BookingImport_WhatTheReservationAndTheCatalogDoNotSay_IsNull_NeverFalse()
    {
        var h = new BookingImportHarness();
        await using var db = h.NewDb();
        var service = h.Service(db);

        h.Serve(Res("7001"), Res("7002") with { CompanyCollect = "NONE" });
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        foreach (var id in new[] { "7001", "7002" })
        {
            var booking = await h.ReadBookingAsync(id);
            Assert.Null(booking.OtaCompanyCollect);
            Assert.Null(booking.OtaCommissionIncludedInPayments);
        }
    }

    [Fact]
    public async Task BookingImport_ABookingThatDoesNotComeFromAChannel_NeverGetsTheFlags()
    {
        var h = new BookingImportHarness();
        await SeedPortalAsync(h, "270", commissionIncludedInPayments: true);
        var manual = await h.SeedBookingAsync("7010", source: BookingSource.Manual);
        await using var db = h.NewDb();
        var service = h.Service(db);

        h.Serve(
            Res("7009") with { ChannelId = "270", ChannelName = "Octorate Booking Engine", CompanyCollect = "COMPANY" },
            Res("7010") with { UpdatedAt = SecondVersion, CompanyCollect = "COMPANY" });
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        var direct = await h.ReadBookingAsync("7009");
        Assert.Equal(BookingSource.Direct, direct.Source);
        Assert.Equal((null, null), (direct.OtaCompanyCollect, direct.OtaCommissionIncludedInPayments));

        await using var read = h.NewDb();
        var entered = await read.Bookings.AsNoTracking().SingleAsync(b => b.Id == manual);
        Assert.Equal(BookingSource.Manual, entered.Source);
        Assert.Equal((null, null), (entered.OtaCompanyCollect, entered.OtaCommissionIncludedInPayments));
    }

    [Fact]
    public async Task BookingImport_ABookingOfAChannelCasaZenOwns_KeepsItsFigures_ButGetsTheFlags()
    {
        var h = new BookingImportHarness();
        await SeedPortalAsync(h, "274", commissionIncludedInPayments: true);
        var id = await h.SeedBookingAsync("7001", octorateUpdatedAt: FirstVersion.AddHours(-1));
        await using (var seed = h.NewDb())
        {
            seed.Payments.Add(new Payment { BookingId = id, OrgId = OrgA, Amount = 50m, Status = PaymentStatus.Completed, Method = PaymentMethod.CashOnArrival });
            await seed.SaveChangesAsync();
        }

        await using var db = h.NewDb();
        h.Serve(Res("7001") with { CompanyCollect = "COMPANY" });
        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        var booking = await h.ReadBookingAsync("7001");
        Assert.Null(booking.OtaCommission);
        Assert.Equal(100m, booking.TotalPrice);
        Assert.Equal((true, true), (booking.OtaCompanyCollect, booking.OtaCommissionIncludedInPayments));
    }

    private static async Task SeedPortalAsync(BookingImportHarness h, string portalCode, bool commissionIncludedInPayments)
    {
        await using var db = h.NewDb();
        db.MarketplacePortals.Add(new MarketplacePortal
        {
            PortalCode = portalCode,
            Name = "Portale " + portalCode,
            CommissionIncludedInPayments = commissionIncludedInPayments,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetPortalFlagAsync(BookingImportHarness h, string portalCode, bool commissionIncludedInPayments)
    {
        await using var db = h.NewDb();
        var portal = await db.MarketplacePortals.SingleAsync(p => p.PortalCode == portalCode);
        portal.CommissionIncludedInPayments = commissionIncludedInPayments;
        await db.SaveChangesAsync();
    }
}
