using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Octorate.BookingImportTestKit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the import of the bookings of the channel manager (E02/F02/US01/T01, ADR-031, ADR-033): the amounts are the ones Octorate gives,
/// to the cent, and never invented (money rule); a reservation is one booking per org whoever reads it and however many times, and an older
/// version never undoes a newer one (data rule); one org never reads, writes or sees another's (data rule); the events carry ids, enums and
/// dates only (privacy rule). The Octorate client is a mock: what is proven is what CasaZen does with the answers.
/// </summary>
public class BookingImportProofTests
{
    [Fact]
    public async Task BookingImport_PaymentAmounts_Accurate_Test()
    {
        var h = new BookingImportHarness();
        var withCommission = Res("7001") with
        {
            RoomGross = 300.10m,
            TotalGross = 330.55m,
            TouristTax = 6.50m,
            TotalPaid = 123.45m,
            PriceBreakdown =
            [
                new OctoratePriceComponent("ROOM_NET", 250m),
                new OctoratePriceComponent("COMMISSION", 15.10m),
                new OctoratePriceComponent("COMMISSION", 3.05m),
                new OctoratePriceComponent("VAT", 30.5m),
            ],
        };
        var withoutCommission = Res("7002") with
        {
            RoomGross = 250m,
            TotalGross = 275m,
            TouristTax = null,
            TotalPaid = null,
            PriceBreakdown = [new OctoratePriceComponent("ROOM_NET", 225m), new OctoratePriceComponent("VAT", 25m)],
        };
        h.Serve(withCommission, withoutCommission);
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(2, result.Created);
        await using var read = h.NewDb();
        var first = await read.Bookings.AsNoTracking().SingleAsync(b => b.OctorateReservationId == "7001");
        Assert.Equal(18.15m, first.OtaCommission);
        Assert.Equal(123.45m, first.OtaPaidAmount);
        Assert.Equal(300.10m, first.BasePrice);
        Assert.Equal(330.55m, first.TotalPrice);
        Assert.Equal(6.50m, first.TouristTax);
        Assert.Null(first.OtaNetAmount);
        Assert.Null(first.OtaBalanceAmount);

        var second = await read.Bookings.AsNoTracking().SingleAsync(b => b.OctorateReservationId == "7002");
        Assert.Null(second.OtaCommission);
        Assert.Null(second.OtaNetAmount);
        Assert.Null(second.OtaBalanceAmount);
        Assert.Null(second.OtaPaidAmount);
        Assert.Equal(250m, second.BasePrice);
        Assert.Equal(275m, second.TotalPrice);
    }

    [Fact]
    public async Task BookingImport_AReservationInAnotherCurrencyThanThePropertyOrInNone_WritesNoAmount_AndTheFiguresHeldStay()
    {
        var h = new BookingImportHarness();
        await using var db = h.NewDb();
        var service = h.Service(db);

        h.Serve(Res("7001"));
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        var held = await h.ReadBookingAsync("7001");
        Assert.Equal((300m, 330m, 6m, 45m, 100m), (held.BasePrice, held.TotalPrice, held.TouristTax, held.OtaCommission, held.OtaPaidAmount));

        var inFrancs = Res("7001") with
        {
            UpdatedAt = SecondVersion,
            Currency = "CHF",
            TotalGross = 500m,
            RoomGross = 480m,
            TouristTax = 9m,
            TotalPaid = 10m,
            PriceBreakdown = [new OctoratePriceComponent("COMMISSION", 50m)],
        };
        var withoutCurrency = inFrancs with { UpdatedAt = SecondVersion.AddHours(1), Currency = null };
        foreach (var version in new[] { inFrancs, withoutCurrency })
        {
            h.Serve(version);
            var result = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

            Assert.Equal((0, 0), (result.Created, result.Updated));
            var booking = await h.ReadBookingAsync("7001");
            Assert.Equal((300m, 330m, 6m, 45m, 100m), (booking.BasePrice, booking.TotalPrice, booking.TouristTax, booking.OtaCommission, booking.OtaPaidAmount));
            Assert.Equal(version.UpdatedAt, booking.OctorateUpdatedAt);
        }

        h.Serve(Res("7002") with { Currency = "CHF" }, Res("7003") with { Currency = null });
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        foreach (var id in new[] { "7002", "7003" })
        {
            var booking = await h.ReadBookingAsync(id);
            Assert.Null(booking.OtaCommission);
            Assert.Null(booking.OtaPaidAmount);
            Assert.NotEqual(330m, booking.TotalPrice);
            Assert.NotEqual(300m, booking.BasePrice);
            Assert.NotEqual(6m, booking.TouristTax);
        }
    }

    [Fact]
    public async Task BookingImport_APriceOctorateDoesNotGive_IsNotWrittenAsZero_ThePreviousOneStays()
    {
        var h = new BookingImportHarness();
        await using var db = h.NewDb();
        var service = h.Service(db);
        h.Serve(Res("7001"));
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        h.Serve(Res("7001") with
        {
            UpdatedAt = SecondVersion,
            CheckIn = "2026-11-24",
            CheckOut = "2026-11-27",
            RoomGross = null,
            TotalGross = null,
            TouristTax = null,
            TotalPaid = null,
            PriceBreakdown = [],
        });
        var result = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(1, result.Updated);
        var booking = await h.ReadBookingAsync("7001");
        Assert.Equal(new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), booking.CheckInDate);
        Assert.Equal((300m, 330m, 6m), (booking.BasePrice, booking.TotalPrice, booking.TouristTax));
        Assert.Null(booking.OtaCommission);
        Assert.Null(booking.OtaPaidAmount);
    }

    [Fact]
    public async Task BookingImport_TheFiguresOfABookingCasaZenOwns_AreNotOverwrittenByTheRead()
    {
        var h = new BookingImportHarness();
        var bookingId = await h.SeedBookingAsync(
            "7003", source: BookingSource.Manual, status: BookingStatus.Confirmed, total: 500m, updatedAt: FirstVersion.AddDays(-1));
        h.Serve(Res("7003") with { TotalGross = 999m, RoomGross = 900m, TotalPaid = 5m, UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        await using var read = h.NewDb();
        var booking = await read.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(500m, booking.TotalPrice);
        Assert.Null(booking.OtaPaidAmount);
        Assert.Equal(BookingSource.Manual, booking.Source);
        Assert.Equal(SecondVersion, booking.OctorateUpdatedAt);
    }

    [Fact]
    public async Task BookingImport_ABookingCasaZenCancelled_IsNotBroughtBackByAnOlderRead()
    {
        var h = new BookingImportHarness();
        var bookingId = await h.SeedBookingAsync(
            "7004", source: BookingSource.Manual, status: BookingStatus.Cancelled, total: 100m, updatedAt: FirstVersion.AddDays(-1));
        h.Serve(Res("7004", OctorateBookingStatus.Confirmed) with { UpdatedAt = SecondVersion });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        await using var read = h.NewDb();
        Assert.Equal(BookingStatus.Cancelled, (await read.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).Status);
    }

    [Fact]
    public async Task BookingImport_UniqueKey_NoDuplicates_Test()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var service = h.Service(db);

        var first = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        var second = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        var third = await service.ImportIncrementalAsync(h.PropertyId, Now.UtcDateTime.AddDays(-2), CancellationToken.None);

        Assert.Equal(1, first.Created);
        Assert.Equal((0, 0, 1), (second.Created, second.Updated, second.Skipped));
        Assert.Equal((0, 0, 1), (third.Created, third.Updated, third.Skipped));
        await using var read = h.NewDb();
        Assert.Equal(1, await read.Bookings.CountAsync(b => b.OctorateReservationId == "7001"));

        var index = read.Model.FindEntityType(typeof(Booking))!.GetIndexes().Single(i => i.GetDatabaseName() == Booking.OctorateReservationIndexName);
        Assert.True(index.IsUnique);
        Assert.Equal([nameof(Booking.OrgId), nameof(Booking.OctorateReservationId)], index.Properties.Select(p => p.Name));
        Assert.Equal("\"OctorateReservationId\" IS NOT NULL", index.GetFilter());
        Assert.Equal(Booking.OctorateReservationIdMaxLength, read.Model.FindEntityType(typeof(Booking))!.FindProperty(nameof(Booking.OctorateReservationId))!.GetMaxLength());
    }

    [Fact]
    public async Task BookingImport_AnOlderVersionNeverUndoesANewerOne_WhateverTheOrderOfArrival()
    {
        var h = new BookingImportHarness();
        var newer = Res("7001") with { UpdatedAt = SecondVersion, CheckIn = "2026-11-24", CheckOut = "2026-11-27", TotalGuests = 3 };
        var older = Res("7001") with { UpdatedAt = FirstVersion };
        await using var db = h.NewDb();
        var service = h.Service(db);

        h.Serve(newer);
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        h.Serve(older);
        var late = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal((0, 0, 1), (late.Created, late.Updated, late.Skipped));
        await using var read = h.NewDb();
        var booking = await read.Bookings.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), booking.CheckInDate);
        Assert.Equal(3, booking.NumberOfGuests);
        Assert.Equal(SecondVersion, booking.OctorateUpdatedAt);

        var h2 = new BookingImportHarness();
        await using var db2 = h2.NewDb();
        var service2 = h2.Service(db2);
        h2.Serve(older);
        await service2.ImportAllAsync(h2.PropertyId, CancellationToken.None);
        h2.Serve(newer);
        var update = await service2.ImportAllAsync(h2.PropertyId, CancellationToken.None);
        Assert.Equal((0, 1, 0), (update.Created, update.Updated, update.Skipped));
        await using var read2 = h2.NewDb();
        Assert.Equal(new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc), (await read2.Bookings.AsNoTracking().SingleAsync()).CheckInDate);
    }

    [Fact]
    public async Task BookingImport_AReservationThatChangedNothingIsNotReadAgain()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var service = h.Service(db);

        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        h.Client.Verify(c => c.GetReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BookingImport_TenantIsolation_Test()
    {
        var h = new BookingImportHarness();
        var foreignId = await h.SeedBookingAsync(
            "7001", orgId: OrgB, propertyId: Guid.NewGuid(), source: BookingSource.Airbnb, status: BookingStatus.Confirmed, total: 77m, updatedAt: FirstVersion);
        h.Serve(Res("7001") with { TotalGross = 330m });
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(1, result.Created);
        await using var read = h.NewDb();
        var all = await read.Bookings.AsNoTracking().Where(b => b.OctorateReservationId == "7001").ToListAsync();
        Assert.Equal(2, all.Count);
        var mine = Assert.Single(all, b => b.OrgId == OrgA);
        Assert.Equal(h.PropertyId, mine.PropertyId);
        Assert.Equal(330m, mine.TotalPrice);
        var theirs = Assert.Single(all, b => b.OrgId == OrgB);
        Assert.Equal(foreignId, theirs.Id);
        Assert.Equal(77m, theirs.TotalPrice);
        Assert.Equal(FirstVersion, theirs.UpdatedAt);

        await using var asB = h.NewDb(new RequestTenant(OrgB));
        Assert.Equal(foreignId, Assert.Single(await asB.Bookings.ToListAsync()).Id);
        await using var asA = h.NewDb(new RequestTenant(OrgA));
        Assert.Equal(mine.Id, Assert.Single(await asA.Bookings.ToListAsync()).Id);
    }

    [Fact]
    public async Task BookingImport_ARequestOfAnotherOrgCannotImportThePropertyOfThisOne()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));

        await using var asB = h.NewDb(new RequestTenant(OrgB));
        var asOrgB = h.Service(asB, tenant: new RequestTenant(OrgB));
        await Assert.ThrowsAsync<InvalidOperationException>(() => asOrgB.ImportAllAsync(h.PropertyId, CancellationToken.None));
        h.Client.Verify(c => c.GetReservationsAsync(It.IsAny<string>(), It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()), Times.Never);

        await using var asA = h.NewDb(new RequestTenant(OrgA));
        var result = await h.Service(asA, tenant: new RequestTenant(OrgA)).ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal(1, result.Created);
    }

    [Fact]
    public async Task BookingImport_OnlyTheRoomOfThePropertyIsImported_TheOthersAreReported()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"), Res("7002", product: "111111"), Res("7003", product: "222222"));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(
            [("7002", "111111"), ("7003", "222222")],
            result.Unmapped.Select(u => (u.ReservationId, u.ProductId)).OrderBy(u => u.ReservationId));
        Assert.All(result.Unmapped, u => Assert.Equal(Accommodation, u.AccommodationId));
        await using var read = h.NewDb();
        Assert.Equal(["7001"], await read.Bookings.Select(b => b.OctorateReservationId!).ToListAsync());
        h.Client.Verify(c => c.GetReservationAsync(Accommodation, "7002", It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(1, await read.Properties.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task BookingImport_AReservationMovedToTheRoomOfAnotherPropertyOfTheOrg_TakesItsBookingWithIt()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var service = h.Service(db);
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        var before = await h.ReadBookingAsync("7001");
        Assert.Equal(h.PropertyId, before.PropertyId);

        var other = await h.AddListingAsync("111111", "222222");
        h.Clock.Advance(TimeSpan.FromHours(1));
        h.ServeMore("111111", Res("7001", product: "222222") with { UpdatedAt = SecondVersion });

        var result = await service.ImportAllAsync(other, CancellationToken.None);

        Assert.Equal((0, 1), (result.Created, result.Updated));
        var after = await h.ReadBookingAsync("7001");
        Assert.Equal((before.Id, other, SecondVersion), (after.Id, after.PropertyId, after.OctorateUpdatedAt));
        await using var read = h.NewDb();
        Assert.Equal(1, await read.Bookings.CountAsync());
        var modified = Assert.Single(await h.ReadEventsAsync(), e => e.EventType == DomainEventType.BookingModified);
        using var payload = JsonDocument.Parse(modified.PayloadJson);
        Assert.Equal((before.Id, other), (modified.EntityId, payload.RootElement.GetProperty("PropertyId").GetGuid()));
    }

    [Theory]
    [InlineData(OctorateMappingStatus.Proposed)]
    [InlineData(OctorateMappingStatus.Ambiguous)]
    [InlineData(OctorateMappingStatus.Rejected)]
    public async Task BookingImport_ANotConfirmedListingIsNeverRead(OctorateMappingStatus status)
    {
        var h = new BookingImportHarness(status: status);
        h.Serve(Res("7001"));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(ImportOutcome.NotMapped, result.Outcome);
        Assert.Equal((0, 0, 0), (result.Created, result.Updated, result.Skipped));
        h.Client.VerifyNoOtherCalls();
        await using var read = h.NewDb();
        Assert.Empty(await read.Bookings.ToListAsync());
    }

    [Fact]
    public async Task BookingImport_AListingWithoutAChosenRoomIsNotRead_AndNothingIsGuessed()
    {
        var h = new BookingImportHarness(room: null);
        h.Serve(Res("7001"));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(ImportOutcome.RoomNotChosen, result.Outcome);
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BookingImport_TheStayDatesAreTheLocalDatesOfTheProperty()
    {
        var rome = new BookingImportHarness();
        var swiss = new BookingImportHarness(timezone: "Europe/Zurich", currency: "CHF");
        foreach (var h in new[] { rome, swiss })
        {
            h.Serve(Res("7001") with { CheckIn = "2026-11-19T23:00:00Z", CheckOut = "2026-11-22T09:30:00Z" });
            await using var db = h.NewDb();
            await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);
        }

        foreach (var h in new[] { rome, swiss })
        {
            await using var read = h.NewDb();
            var booking = await read.Bookings.AsNoTracking().SingleAsync();
            Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), booking.CheckInDate);
            Assert.Equal(new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc), booking.CheckOutDate);
        }
    }

    [Fact]
    public async Task BookingImport_TheBookingTakesTheChannelAndTheStateOfTheReservation()
    {
        var h = new BookingImportHarness();
        h.Serve(
            Res("7001"),
            Res("7002", OctorateBookingStatus.Waiting) with { ChannelId = "142", ChannelName = "Booking.com" },
            Res("7003", OctorateBookingStatus.Cancelled) with { ChannelId = "270", ChannelName = "Octorate Booking Engine" },
            Res("7004") with { ChannelId = "77", ChannelName = "Hostelworld" });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        await using var read = h.NewDb();
        var byId = await read.Bookings.AsNoTracking().ToDictionaryAsync(b => b.OctorateReservationId!);
        Assert.Equal((BookingSource.Airbnb, BookingStatus.Confirmed), (byId["7001"].Source, byId["7001"].Status));
        Assert.Equal((BookingSource.BookingCom, BookingStatus.Pending), (byId["7002"].Source, byId["7002"].Status));
        Assert.Equal((BookingSource.Direct, BookingStatus.Cancelled), (byId["7003"].Source, byId["7003"].Status));
        Assert.Equal(BookingSource.OtherChannel, byId["7004"].Source);
        Assert.Equal("Hostelworld", byId["7004"].ChannelLabel);
        Assert.Null(byId["7001"].ChannelLabel);
        Assert.NotNull(byId["7001"].ConfirmedAt);
        Assert.NotNull(byId["7003"].CancelledAt);
        Assert.All(byId.Values, b => Assert.Equal(string.Empty, b.ExternalId));
    }

    [Fact]
    public async Task BookingImport_AGuestIsMadeForTheBooking_WithTheStayContractAsItsBasis()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001") with { FirstName = "Giulia", LastName = "Rossi", TotalGuests = 3, Notes = "Arriviamo tardi" });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        await using var read = h.NewDb();
        var booking = await read.Bookings.AsNoTracking().SingleAsync();
        var guest = await read.Guests.AsNoTracking().SingleAsync(g => g.Id == booking.GuestId);
        Assert.Equal((OrgA, "Giulia", "Rossi"), (guest.OrgId, guest.FirstName, guest.LastName));
        Assert.Equal(string.Empty, guest.Email);
        Assert.Equal(BookingImportService.GuestPurpose, guest.DataProcessingPurpose);
        Assert.Null(guest.ConsentDate);
        Assert.Null(guest.DataProcessingConsentDate);
        Assert.False(guest.MarketingConsent);
        Assert.Equal("Arriviamo tardi", booking.SpecialRequests);

        var line = await read.StayGuests.AsNoTracking().SingleAsync();
        Assert.Equal((booking.Id, OrgA, 0, StayGuestType.HeadOfFamily, "Giulia"), (line.BookingId, line.OrgId, line.Position, line.Type, line.FirstName));
        Assert.Equal(booking.GuestId, line.GuestId);
    }

    [Fact]
    public async Task BookingImport_AGuestWithTheSameEmailAndNameIsTheSameGuest_AnotherNameIsNot()
    {
        var h = new BookingImportHarness();
        var existing = await h.SeedGuestAsync("Giulia", "Rossi", "giulia.rossi@example.com");
        await h.SeedGuestAsync("Marco", "Rossi", "giulia.rossi@example.com");
        h.Serve(
            Res("7001") with { FirstName = "GIULIA", LastName = "rossi", GuestEmail = "Giulia.Rossi@Example.com" },
            Res("7002") with { FirstName = "Paola", LastName = "Rossi", GuestEmail = "giulia.rossi@example.com" });
        await using var db = h.NewDb();

        await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        await using var read = h.NewDb();
        var first = await read.Bookings.AsNoTracking().SingleAsync(b => b.OctorateReservationId == "7001");
        var second = await read.Bookings.AsNoTracking().SingleAsync(b => b.OctorateReservationId == "7002");
        Assert.Equal(existing, first.GuestId);
        Assert.NotEqual(existing, second.GuestId);
        Assert.Equal(3, await read.Guests.CountAsync());
    }

    [Fact]
    public async Task BookingImport_RecordsTheEventsOfEachChangeOnce_WithIdsEnumsAndDatesOnly()
    {
        var h = new BookingImportHarness();
        await using var db = h.NewDb();
        var service = h.Service(db);

        h.Serve(Res("7001") with { Notes = "Allergica ai gatti", FirstName = "Giulietta", LastName = "Capuleti" });
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal(
            [DomainEventType.BookingCreated, DomainEventType.BookingConfirmed],
            (await h.ReadEventsAsync()).Select(e => e.EventType).Order());

        h.Serve(Res("7001") with { UpdatedAt = SecondVersion, CheckIn = "2026-11-24", CheckOut = "2026-11-27", Notes = "Allergica ai gatti" });
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal(1, (await h.ReadEventsAsync()).Count(e => e.EventType == DomainEventType.BookingModified));

        var thirdVersion = SecondVersion.AddHours(3);
        h.Serve(Res("7001", OctorateBookingStatus.Cancelled) with { UpdatedAt = thirdVersion, CheckIn = "2026-11-24", CheckOut = "2026-11-27" });
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        await service.ImportAllAsync(h.PropertyId, CancellationToken.None);

        var events = await h.ReadEventsAsync();
        Assert.Equal(1, events.Count(e => e.EventType == DomainEventType.BookingCancelled));
        Assert.Equal(1, events.Count(e => e.EventType == DomainEventType.BookingModified));
        Assert.Equal(4, events.Count);
        var booking = await h.ReadBookingAsync("7001");
        Assert.All(events, e =>
        {
            Assert.Equal((OrgA, booking.Id), (e.OrgId, e.EntityId));
            using var json = JsonDocument.Parse(e.PayloadJson);
            Assert.Equal("Octorate", json.RootElement.GetProperty("Origin").GetString());
            Assert.Equal(
                ["BookingId", "CheckIn", "CheckOut", "Origin", "PropertyId", "Source", "Status"],
                json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            foreach (var forbidden in new[] { "Giulietta", "Capuleti", "gatti" })
                Assert.DoesNotContain(forbidden, e.PayloadJson, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(events, e => e.DedupKey == $"booking-created:{booking.Id}");
        Assert.Contains(events, e => e.DedupKey == $"booking-confirmed:{booking.Id}");
        Assert.Contains(events, e => e.DedupKey == $"booking-cancelled:{booking.Id}");
    }

    [Fact]
    public async Task BookingImport_AStayThatIsOverAndAReservationFirstSeenCancelled_RaiseNoEvent()
    {
        var h = new BookingImportHarness();
        h.Serve(
            Res("7001") with { CheckIn = "2026-10-01", CheckOut = "2026-10-04" },
            Res("7002", OctorateBookingStatus.Cancelled));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(2, result.Created);
        Assert.Empty(await h.ReadEventsAsync());
    }

    [Fact]
    public async Task BookingImport_ACompleteFullReadMarksTheListing_AWindowMarksItOnlyIfItReachesBack()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        await using var db = h.NewDb();
        var service = h.Service(db);

        await service.ImportIncrementalAsync(h.PropertyId, Now.UtcDateTime.AddDays(-2), CancellationToken.None);
        Assert.Null(await h.ReadCheckpointAsync());

        var full = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.True(full.IsComplete);
        Assert.Equal(Now.UtcDateTime, await h.ReadCheckpointAsync());

        h.Clock.SetUtcNow(Now.AddDays(5));
        await service.ImportIncrementalAsync(h.PropertyId, Now.UtcDateTime.AddDays(3), CancellationToken.None);
        Assert.Equal(Now.UtcDateTime, await h.ReadCheckpointAsync());

        await service.ImportIncrementalAsync(h.PropertyId, Now.UtcDateTime.AddDays(-1), CancellationToken.None);
        Assert.Equal(Now.AddDays(5).UtcDateTime, await h.ReadCheckpointAsync());
    }

    [Fact]
    public async Task BookingImport_ARunThatFailsOnAReservationIsIncomplete_AndLeavesTheMarkAlone()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"), Res("7002"));
        h.Client.Setup(c => c.GetReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateServerException(500));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(ImportOutcome.Incomplete, result.Outcome);
        Assert.Equal((1, 1), (result.Created, result.Failed));
        Assert.Null(await h.ReadCheckpointAsync());
    }

    [Fact]
    public async Task BookingImport_AShortPageIsNotTheEnd_AndAnItemThatCannotBeReadMakesTheRunIncomplete()
    {
        var h = new BookingImportHarness();
        h.Client
            .Setup(c => c.GetReservationsAsync(Accommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, OctorateReservationQuery query, CancellationToken _) => query.Page switch
            {
                0 => new OctorateReservationPage([Res("7001")], ItemCount: 2),
                1 => OctorateReservationPage.Of(Res("7002")),
                _ => OctorateReservationPage.Empty,
            });
        h.Client.Setup(c => c.GetReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>())).ReturnsAsync(Res("7001"));
        h.Client.Setup(c => c.GetReservationAsync(Accommodation, "7002", It.IsAny<CancellationToken>())).ReturnsAsync(Res("7002"));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(2, result.Created);
        Assert.Equal(ImportOutcome.Incomplete, result.Outcome);
        Assert.Equal(1, result.Failed);
        Assert.Null(await h.ReadCheckpointAsync());
        h.Client.Verify(c => c.GetReservationsAsync(Accommodation, It.Is<OctorateReservationQuery>(q => q.Page == 2), It.IsAny<CancellationToken>()), Times.Once);
        h.Client.Verify(c => c.GetReservationsAsync(Accommodation, It.Is<OctorateReservationQuery>(q => q.Page == 3), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BookingImport_TheRateLimitStopsTheRun_WhatWasReadIsKept_AndTheMarkDoesNotMove()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"), Res("7002"));
        h.Client.Setup(c => c.GetReservationAsync(Accommodation, "7002", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateRateLimitException(429));
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal(ImportOutcome.Incomplete, result.Outcome);
        Assert.Equal(1, result.Created);
        Assert.Null(await h.ReadCheckpointAsync());
        await using var read = h.NewDb();
        Assert.Equal(["7001"], await read.Bookings.Select(b => b.OctorateReservationId!).ToListAsync());
    }

    [Fact]
    public async Task BookingImport_AReservationThatVanishedBetweenTheListAndTheReadIsSkipped()
    {
        var h = new BookingImportHarness();
        h.Serve(Res("7001"));
        h.Client.Setup(c => c.GetReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateNotFoundException());
        await using var db = h.NewDb();

        var result = await h.Service(db).ImportAllAsync(h.PropertyId, CancellationToken.None);

        Assert.Equal((0, 1), (result.Created, result.Skipped));
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task BookingImport_ABookingChangedInCasaZenWithinTheWindowIsLeftAlone_ByTheReconciliation()
    {
        var h = new BookingImportHarness();
        var recent = Now.UtcDateTime.AddMinutes(-3);
        var bookingId = await h.SeedBookingAsync("7001", source: BookingSource.Airbnb, status: BookingStatus.Confirmed, total: 100m, updatedAt: recent, octorateUpdatedAt: FirstVersion.AddDays(-1));
        h.Serve(Res("7001", OctorateBookingStatus.Cancelled) with { UpdatedAt = SecondVersion });
        await using var db = h.NewDb();
        var service = h.Service(db);

        var guarded = await service.ImportAllAsync(h.PropertyId, CancellationToken.None, new BookingImportOptions(TimeSpan.FromMinutes(5)));
        Assert.Equal((0, 0, 1), (guarded.Created, guarded.Updated, guarded.Skipped));
        Assert.Equal(BookingStatus.Confirmed, (await h.ReadBookingAsync("7001")).Status);

        var open = await service.ImportAllAsync(h.PropertyId, CancellationToken.None);
        Assert.Equal(1, open.Updated);
        Assert.Equal(BookingStatus.Cancelled, (await h.ReadBookingAsync("7001")).Status);
        Assert.Equal(bookingId, (await h.ReadBookingAsync("7001")).Id);
    }

    [Fact]
    public async Task SyncBookingState_ArrivalAndDeparture_UseTheCheckInStatusOperation()
    {
        var h = new BookingImportHarness();
        var bookingId = await h.SeedBookingAsync("7001", source: BookingSource.Direct, status: BookingStatus.CheckedIn, total: 100m, updatedAt: FirstVersion);
        await using var db = h.NewDb();
        var service = h.Service(db);

        await service.SyncBookingStateAsync(bookingId, BookingStatus.CheckedIn, CancellationToken.None);
        await service.SyncBookingStateAsync(bookingId, BookingStatus.CheckedOut, CancellationToken.None);
        await service.SyncBookingStateAsync(bookingId, BookingStatus.Confirmed, CancellationToken.None);
        await service.SyncBookingStateAsync(bookingId, BookingStatus.Pending, CancellationToken.None);

        h.Client.Verify(c => c.SetCheckInStatusAsync(Accommodation, "7001", OctorateCheckInStatus.CheckInEnd, It.IsAny<CancellationToken>()), Times.Once);
        h.Client.Verify(c => c.SetCheckInStatusAsync(Accommodation, "7001", OctorateCheckInStatus.CheckOutEnd, It.IsAny<CancellationToken>()), Times.Once);
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SyncBookingState_ACancellationIsADelete_ForAReservationMadeInCasaZen_NeverForAPortalsOne()
    {
        var h = new BookingImportHarness();
        var mine = await h.SeedBookingAsync("7001", source: BookingSource.Direct, status: BookingStatus.Cancelled, total: 100m, updatedAt: FirstVersion);
        var portals = await h.SeedBookingAsync("7002", source: BookingSource.Airbnb, status: BookingStatus.Cancelled, total: 100m, updatedAt: FirstVersion);
        await using var db = h.NewDb();
        var service = h.Service(db);

        await service.SyncBookingStateAsync(mine, BookingStatus.Cancelled, CancellationToken.None);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.SyncBookingStateAsync(portals, BookingStatus.Cancelled, CancellationToken.None));

        Assert.Equal(BookingErrorCodes.ChannelCancelNotAllowed, refused.Code);
        Assert.Equal("octorate_channel_cancel_not_allowed", refused.Code);
        h.Client.Verify(c => c.CancelReservationAsync(Accommodation, "7001", It.IsAny<CancellationToken>()), Times.Once);
        h.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SyncBookingState_NothingIsSentForABookingThatIsNotMirrored_OrWhoseMappingIsNotConfirmed()
    {
        var notMirrored = new BookingImportHarness();
        var local = await notMirrored.SeedBookingAsync(null, source: BookingSource.Manual, status: BookingStatus.CheckedOut, total: 10m, updatedAt: FirstVersion);
        await using (var db = notMirrored.NewDb())
            await notMirrored.Service(db).SyncBookingStateAsync(local, BookingStatus.CheckedOut, CancellationToken.None);
        notMirrored.Client.VerifyNoOtherCalls();

        var proposed = new BookingImportHarness(status: OctorateMappingStatus.Proposed);
        var booking = await proposed.SeedBookingAsync("7001", source: BookingSource.Direct, status: BookingStatus.CheckedOut, total: 10m, updatedAt: FirstVersion);
        await using (var db = proposed.NewDb())
            await proposed.Service(db).SyncBookingStateAsync(booking, BookingStatus.CheckedOut, CancellationToken.None);
        proposed.Client.VerifyNoOtherCalls();

        await using var any = proposed.NewDb();
        await Assert.ThrowsAsync<NotFoundException>(() => proposed.Service(any).SyncBookingStateAsync(Guid.NewGuid(), BookingStatus.CheckedOut, CancellationToken.None));
    }
}
