using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The amounts of the channel on the booking API (E02/F02/US01/T01, ADR-033-R3, R8, R9): the booking detail gives the commission, net, paid and
/// balance of an imported booking as stored, to whoever may read payments and to nobody else (permission rule); the channel is there for
/// everyone, so the "OTA" badge works for the collaborator too; and a request to change an amount is refused with 422 (data rule), whatever
/// else it carried.
/// </summary>
public class BookingsControllerOtaAmountsTests
{
    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BookingDetail_OtaAmounts_AreShownOnlyToWhoReadsPayments_Test(bool canReadPayments)
    {
        var booking = ImportedBooking();
        var bookings = new Mock<IBookingService>();
        bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        var authorization = new Mock<IPropertyAuthorizationService>();
        authorization.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>())).ReturnsAsync(true);
        var controller = Controller(bookings.Object, authorization.Object);

        var result = await controller.GetById(
            booking.Id, HostAuthorizationTestHarness.Create(OrgId, (_, permission) => permission != "payment.read" || canReadPayments));

        var dto = Assert.IsType<BookingDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        // The channel tells the imported booking apart for everyone: the badge is not money.
        Assert.Equal("Airbnb", dto.OtaSource);
        Assert.Equal("Airbnb", dto.Source);
        if (!canReadPayments)
        {
            Assert.Null(dto.OtaAmounts);
            Assert.Null(dto.PaidAmount);
            return;
        }

        // As stored, to the cent, in the currency of the property; what Octorate did not give stays unknown, never zero.
        var amounts = Assert.IsType<OtaAmountsDto>(dto.OtaAmounts);
        Assert.Equal(18.15m, amounts.Commission);
        Assert.Equal(123.45m, amounts.Paid);
        Assert.Null(amounts.Net);
        Assert.Null(amounts.Balance);
        Assert.Equal("CHF", amounts.Currency);
    }

    [Fact]
    public async Task BookingDetail_ABookingThatWasNotImported_HasNoChannelAndNoOtaAmounts()
    {
        var booking = ImportedBooking();
        booking.OctorateReservationId = null;
        booking.Source = BookingSource.Manual;
        booking.OtaCommission = booking.OtaPaidAmount = null;
        var bookings = new Mock<IBookingService>();
        bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        var authorization = new Mock<IPropertyAuthorizationService>();
        authorization.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>())).ReturnsAsync(true);

        var result = await Controller(bookings.Object, authorization.Object).GetById(booking.Id, HostAuthorizationTestHarness.Create(OrgId));

        var dto = Assert.IsType<BookingDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Null(dto.OtaSource);
        Assert.Null(dto.OtaAmounts);
        Assert.Equal("Manual", dto.Source);
    }

    [Fact]
    public void BookingMapper_TheChannelOfAnImportedBookingIsItsOtaSource()
    {
        var booking = ImportedBooking();
        Assert.Equal("Airbnb", BookingMapper.ToResponse(booking).OtaSource);

        booking.Source = BookingSource.OtherChannel;
        booking.ChannelLabel = "Hostelworld";
        var other = BookingMapper.ToResponse(booking);
        Assert.Equal("OtherChannel", other.OtaSource);
        Assert.Equal("Hostelworld", other.ChannelLabel);

        booking.OctorateReservationId = null;
        Assert.Null(BookingMapper.ToResponse(booking).OtaSource);
    }

    [Fact]
    public async Task UpdateBooking_AChangedOtaAmount_IsRefusedWith422_AndNothingIsChanged_Test()
    {
        var database = $"ota-readonly-{Guid.NewGuid():N}";
        await using var db = NewDb(database);
        var booking = ImportedBooking();
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var service = HostBookings(db);

        // A different commission, net, paid or balance is an edit of the channel's figures: refused, with the code the clients translate.
        foreach (var attempt in new[]
                 {
                     new OtaAmountsInput(99m, null, null, null),
                     new OtaAmountsInput(null, 10m, null, null),
                     new OtaAmountsInput(null, null, 200m, null),
                     new OtaAmountsInput(null, null, null, 0m),
                     new OtaAmountsInput(18.15m, null, 123.46m, null),
                 })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.UpdateAsync(Update(booking, attempt), CancellationToken.None));
            Assert.Equal("ota_amounts_readonly", refused.Code);
            Assert.Equal("BookingOtaAmountsReadonly", refused.MessageKey);
        }

        await using var read = NewDb(database);
        var stored = await read.Bookings.AsNoTracking().SingleAsync();
        Assert.Equal((18.15m, null, 123.45m, null), (stored.OtaCommission, stored.OtaNetAmount, stored.OtaPaidAmount, stored.OtaBalanceAmount));
        Assert.Equal(booking.SpecialRequests, stored.SpecialRequests);
    }

    [Fact]
    public async Task UpdateBooking_AnAmountOnABookingThatHasNone_IsRefusedToo()
    {
        await using var db = NewDb($"ota-readonly-{Guid.NewGuid():N}");
        var booking = ImportedBooking();
        booking.OctorateReservationId = null;
        booking.OtaCommission = booking.OtaPaidAmount = null;
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => HostBookings(db).UpdateAsync(Update(booking, new OtaAmountsInput(5m, null, null, null)), CancellationToken.None));

        Assert.Equal("ota_amounts_readonly", refused.Code);
    }

    [Fact]
    public void OtaAmountsInput_OnlyAFigureThatDiffersFromTheStoredOneIsAnEdit()
    {
        var stored = new OtaAmountsInput(18.15m, null, 123.45m, null);

        // Sending back what is stored (a client that returns the whole answer) edits nothing, nor does sending nothing.
        Assert.False(new OtaAmountsInput(null, null, null, null).Edits(stored));
        Assert.False(new OtaAmountsInput(18.15m, null, 123.450m, null).Edits(stored));
        Assert.False(stored.Edits(stored));

        Assert.True(new OtaAmountsInput(18.16m, null, null, null).Edits(stored));
        Assert.True(new OtaAmountsInput(null, 1m, null, null).Edits(stored));
        Assert.True(new OtaAmountsInput(null, null, null, 0m).Edits(stored));
    }

    private static HostBookingUpdate Update(Booking booking, OtaAmountsInput amounts) =>
        new(
            booking.Id,
            booking.CheckInDate,
            booking.CheckOutDate,
            2,
            0,
            null,
            "Una nota nuova",
            amounts);

    private static HostBookingService HostBookings(AppDbContext db) =>
        new(
            db,
            Mock.Of<IBookingService>(),
            Mock.Of<IBookingRepository>(),
            Mock.Of<ICheckoutHoldExpiryService>(),
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), new ConfigurationBuilder().Build()),
            Mock.Of<IOnSiteBookingRequestService>(),
            NullLogger<HostBookingService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero)));

    private static AppDbContext NewDb(string database) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options);

    private static BookingsController Controller(IBookingService bookings, IPropertyAuthorizationService authorization)
    {
        var db = NewDb(Guid.NewGuid().ToString());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" })
            .Build();
        return new BookingsController(
            bookings,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IPropertyService>(),
            authorization,
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration),
            Mock.Of<IOtaStayService>(),
            Mock.Of<ILogger<BookingsController>>(),
            Options.Create(new BookingsOptions { DefaultPageSize = 10 }),
            Mock.Of<IBookingOctorateSyncReader>(),
            OtaChannelActionPolicy.Closed)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(OwnerId),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    /// <summary>A booking of Airbnb imported from Octorate, in a property in francs, with a commission and a paid amount but no net and no balance.</summary>
    private static Booking ImportedBooking()
    {
        var property = new Property
        {
            Id = PropertyId,
            OrgId = OrgId,
            OwnerId = OwnerId,
            Name = "Chalet Lugano",
            Currency = "CHF",
            MaxGuests = 4,
        };
        var guest = new Guest { OrgId = OrgId, FirstName = "Giulia", LastName = "Rossi", Email = string.Empty };
        return new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            PropertyId = PropertyId,
            Property = property,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Airbnb,
            OctorateReservationId = "7001",
            OctorateUpdatedAt = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc),
            OtaCommission = 18.15m,
            OtaPaidAmount = 123.45m,
            TotalPrice = 330.55m,
            SpecialRequests = "Arriviamo tardi",
        };
    }
}
