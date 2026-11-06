using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Octorate;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Configuration;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The booking answer that feeds the booking screens (E08/F03/US03/T02, NF-74, NF-77, ADR-033-R8, R9): the amounts of the channel are the stored ones to
/// the cent and nothing else (money rule), whoever may not read payments gets no amount, flag or source (permission rule), a booking CasaZen made is
/// never taken for a booking of a channel, the mirror on Octorate is read for the booking and the org it belongs to (data rule), and every row of the
/// lists says its channel.
/// </summary>
public class BookingsControllerReadModelTests
{
    private const string OwnerId = "auth0|owner_123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private static readonly DateTime SyncedAt = new(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ─── Money ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingResponse_OtaAmounts_MatchStoredToTheCent_Test()
    {
        // Every figure Octorate gave, with cents, in the francs of the property.
        var full = ChannelBooking();
        full.OtaCommission = 61.80m;
        full.OtaNetAmount = 453.20m;
        full.OtaPaidAmount = 515.00m;
        full.OtaBalanceAmount = 0.01m;

        var dto = await DetailAsync(full);

        var amounts = Assert.IsType<OtaAmountsDto>(dto.OtaAmounts);
        Assert.Equal((61.80m, 453.20m, 515.00m, 0.01m, "CHF"), (amounts.Commission, amounts.Net, amounts.Paid, amounts.Balance, amounts.Currency));
        Assert.Equal("""{"commission":61.80,"net":453.20,"paid":515.00,"balance":0.01,"currency":"CHF"}""", JsonSerializer.Serialize(amounts, Web));

        // What Octorate did not give stays unknown on the wire: null, never a zero a screen would read as a figure.
        var sparse = ChannelBooking();
        sparse.OtaCommission = null;
        sparse.OtaNetAmount = null;
        sparse.OtaPaidAmount = 123.45m;
        sparse.OtaBalanceAmount = null;

        var unknown = Assert.IsType<OtaAmountsDto>((await DetailAsync(sparse)).OtaAmounts);
        Assert.Equal((null, null, 123.45m, null), (unknown.Commission, unknown.Net, unknown.Paid, unknown.Balance));
        Assert.Equal("""{"commission":null,"net":null,"paid":123.45,"balance":null,"currency":"CHF"}""", JsonSerializer.Serialize(unknown, Web));
    }

    // ─── Permission ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingResponse_Amounts_HiddenWithoutPaymentRead_Test()
    {
        var booking = ChannelBooking();
        booking.OtaCommission = 18.15m;
        booking.OtaNetAmount = 105.30m;
        booking.OtaPaidAmount = 123.45m;
        booking.OtaBalanceAmount = 7.77m;
        booking.OtaCompanyCollect = true;
        booking.OtaCommissionIncludedInPayments = true;

        // Who may read payments gets all of it (the control: the test is not vacuous)...
        var reader = await DetailAsync(booking);
        Assert.NotNull(reader.OtaAmounts);
        Assert.NotNull(reader.Collection);
        Assert.NotNull(reader.AmountSources);
        Assert.NotNull(reader.PaidAmount);

        // ...a caller who may not gets the state of the payment and nothing of the money: no amount, no flag, no source.
        var collaborator = await DetailAsync(booking, canReadPayments: false);

        Assert.Null(collaborator.PaidAmount);
        Assert.Null(collaborator.OtaAmounts);
        Assert.Null(collaborator.Collection);
        Assert.Null(collaborator.AmountSources);
        Assert.Equal("None", collaborator.PaymentState);
        // The channel and what the host may do are not money: the "OTA" badge and the buttons work for the collaborator too.
        Assert.Equal("Airbnb", collaborator.OtaSource);
        Assert.NotNull(collaborator.ChangeOptions);

        var json = JsonSerializer.Serialize(collaborator, Web);
        Assert.DoesNotContain("18.15", json, StringComparison.Ordinal);
        Assert.DoesNotContain("105.30", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123.45", json, StringComparison.Ordinal);
        Assert.DoesNotContain("7.77", json, StringComparison.Ordinal);
        Assert.Contains("\"collection\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"amountSources\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BookingDetail_CanRetryNeedsToWriteTheBooking_NotOnlyToReadIt()
    {
        var booking = ChannelBooking(BookingSource.Manual, reservationId: "7002");
        var reader = Mirror(booking, OctorateSyncState.Failed, "octorate_unavailable");

        var writer = await DetailAsync(booking, reader: reader);
        var readOnly = await DetailAsync(booking, canWrite: false, reader: reader);

        Assert.True(writer.OctorateSync!.CanRetry);
        // The state is the same for the one who only reads: the retry button is the one that is not.
        Assert.False(readOnly.OctorateSync!.CanRetry);
        Assert.Equal("Failed", readOnly.OctorateSync.State);
        Assert.Equal("octorate_unavailable", readOnly.OctorateSync.ErrorCode);
    }

    // ─── What is a booking of a channel ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingDetail_ABookingOfAChannel_HasItsAmountsItsFlagsAndTheChannelManagerAsTheSource()
    {
        var booking = ChannelBooking();
        booking.OtaCommission = 18.15m;
        booking.OtaPaidAmount = 123.45m;
        booking.OtaCompanyCollect = null;
        booking.OtaCommissionIncludedInPayments = true;

        var dto = await DetailAsync(booking);

        Assert.Equal("Airbnb", dto.OtaSource);
        // Who collects is not known: null, not «no»; the portal pays net of its commission.
        var collection = Assert.IsType<BookingCollectionDto>(dto.Collection);
        Assert.Null(collection.CompanyCollect);
        Assert.True(collection.CommissionIncludedInPayments);
        // Every figure Octorate gave comes from the channel manager; net and balance are not available and have no source.
        var sources = Assert.IsType<BookingAmountSourcesDto>(dto.AmountSources);
        Assert.Equal(("ChannelManager", "ChannelManager", "ChannelManager"), (sources.Total, sources.Paid, sources.Commission));
        Assert.Null(sources.Net);
        Assert.Null(sources.Balance);
        Assert.Equal("""{"total":"ChannelManager","paid":"ChannelManager","commission":"ChannelManager"}""", JsonSerializer.Serialize(sources, Web));
    }

    [Fact]
    public async Task BookingDetail_ABookingOfTheBookingSite_SaysStripe_AndACashPaymentTheHostRecordedSaysManual()
    {
        var booking = ChannelBooking(BookingSource.Direct, reservationId: null);
        booking.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = OrgId,
            Amount = 200m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            StripePaymentIntentId = "pi_demo_1",
        });

        var byCard = await DetailAsync(booking);

        Assert.Null(byCard.OtaSource);
        Assert.Null(byCard.OtaAmounts);
        Assert.Null(byCard.Collection);
        Assert.Equal(("Stripe", "Stripe"), (byCard.AmountSources!.Total, byCard.AmountSources.Paid));
        Assert.Equal(200m, byCard.PaidAmount);

        booking.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = OrgId,
            Amount = 50m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CashOnArrival,
        });

        var withCash = await DetailAsync(booking);

        // A number that holds something the host typed is not presented as Stripe's.
        Assert.Equal("Manual", withCash.AmountSources!.Paid);
        Assert.Equal(250m, withCash.PaidAmount);
        Assert.Null(withCash.AmountSources.Commission);
        Assert.Null(withCash.AmountSources.Balance);
    }

    [Fact]
    public async Task BookingDetail_ABookingCasaZenMadeAndSentToOctorate_IsNotABookingOfAChannel()
    {
        // The push gives it a reservation on Octorate, as the import gives one to a booking of a channel; it is still the booking site's.
        var booking = ChannelBooking(BookingSource.Direct, reservationId: "7003");
        booking.OtaPaidAmount = 99m;

        var dto = await DetailAsync(booking);

        Assert.Null(dto.OtaSource);
        Assert.Null(dto.OtaAmounts);
        Assert.Null(dto.Collection);
        Assert.Equal("Stripe", dto.AmountSources!.Total);
        Assert.Null(dto.ChangeOptions.Channel);
        Assert.Null(dto.ChangeOptions.GuidanceKey);
        Assert.Null(BookingMapper.ToResponse(booking).OtaSource);
    }

    // ─── What the host may do ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingDetail_ChangeOptions_FollowTheRuleOfTheChannel_ClosedUnlessTheConfigurationOpensIt()
    {
        var booking = ChannelBooking();

        var closed = (await DetailAsync(booking)).ChangeOptions;
        var open = (await DetailAsync(booking, policy: new OtaChannelActionPolicy(new OctorateChannelActionsOptions
        {
            Airbnb = new OctorateChannelActionOptions { CanCancel = true },
        }))).ChangeOptions;

        Assert.False(closed.CanEdit);
        Assert.False(closed.CanCancel);
        Assert.Equal("Airbnb", closed.Channel);
        Assert.Equal("ota_booking_change_not_allowed", closed.ReasonCode);
        Assert.Equal(OtaChannelGuidanceKeys.ModifyOf(BookingSource.Airbnb), closed.GuidanceKey);
        Assert.Equal("ota_booking_cancel_not_allowed", closed.CancelReasonCode);
        Assert.Equal(OtaChannelGuidanceKeys.CancelOf(BookingSource.Airbnb), closed.CancelGuidanceKey);

        Assert.True(open.CanCancel);
        Assert.Null(open.CancelReasonCode);
        Assert.False(open.CanEdit);
    }

    [Fact]
    public void BookingMapper_EveryRowOfTheLists_SaysItsChannel_AndWhatTheHostMayDo()
    {
        var now = new DateTime(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);
        var airbnb = ChannelBooking();
        var sentByCasaZen = ChannelBooking(BookingSource.Direct, reservationId: "7003");
        var entered = ChannelBooking(BookingSource.Manual, reservationId: null);
        var ical = ChannelBooking(BookingSource.Airbnb, reservationId: null);

        Assert.Equal("Airbnb", BookingMapper.ToListItem(airbnb, null, now).OtaSource);
        Assert.Null(BookingMapper.ToListItem(sentByCasaZen, null, now).OtaSource);
        Assert.Null(BookingMapper.ToListItem(entered, null, now).OtaSource);
        // A stay made from the iCal feed of a channel is CasaZen's own record: no reservation on the channel manager, no "OTA" badge.
        Assert.Null(BookingMapper.ToListItem(ical, null, now).OtaSource);

        Assert.True(BookingMapper.ToListItem(entered, null, now).ChangeOptions.CanEdit);
        Assert.False(BookingMapper.ToListItem(airbnb, null, now).ChangeOptions.CanEdit);
    }

    [Fact]
    public async Task Search_EveryItemOfThePage_CarriesTheChannelOfTheBooking()
    {
        var airbnb = ChannelBooking();
        var direct = ChannelBooking(BookingSource.Direct, reservationId: null);
        var search = new Mock<IBookingSearchService>();
        search
            .Setup(s => s.SearchAsync(It.IsAny<Casazen.Core.Authorization.HostScope>(), It.IsAny<BookingSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookingSearchPage([airbnb, direct], 2, 1, 10));
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var controller = Controller(Mock.Of<IBookingService>(), Mock.Of<IPropertyAuthorizationService>());

        var result = await controller.Search(search.Object, resolver.Object, Authorization(), HostAuthorizationTestHarness.ScopeResolver());

        var page = Assert.IsType<PagedResultDto<BookingListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["Airbnb", null], page.Items.Select(i => i.OtaSource));
    }

    // ─── The mirror on Octorate ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BookingDetail_ABookingThatIsNotMirrored_HasNoOctorateSync()
    {
        var booking = ChannelBooking(BookingSource.Manual, reservationId: null);

        Assert.Null((await DetailAsync(booking, reader: Mirror(booking, null))).OctorateSync);
        // Without a reader at all (the connector is not part of this host) the answer is the same.
        Assert.Null((await DetailAsync(booking)).OctorateSync);
    }

    [Fact]
    public async Task BookingDetail_ASyncedMirror_SaysSoAndCannotBeRetried()
    {
        var booking = ChannelBooking(BookingSource.Direct, reservationId: "7003");

        var sync = (await DetailAsync(booking, reader: Mirror(booking, OctorateSyncState.Synced, lastOperation: OctorateSyncOperation.Create))).OctorateSync!;

        Assert.Equal(("Synced", "Create", SyncedAt, null, false), (sync.State, sync.LastOperation, sync.LastSyncedAt, sync.ErrorCode, sync.CanRetry));
        Assert.Null(sync.OctorateVersion);
    }

    [Fact]
    public async Task BookingDetail_AConflict_CarriesWhatOctorateHolds_AndOnlyAConflictDoes()
    {
        var booking = ChannelBooking(BookingSource.Manual, reservationId: "7004");
        var held = new OctorateVersionSnapshot(new DateOnly(2026, 11, 21), new DateOnly(2026, 11, 23), 1).ToJson();

        var conflict = (await DetailAsync(
            booking,
            reader: Mirror(booking, OctorateSyncState.Conflict, OctorateSyncErrorCodes.ReservationChanged, OctorateSyncOperation.Update, held))).OctorateSync!;

        Assert.Equal(("Conflict", "Update", OctorateSyncErrorCodes.ReservationChanged, true), (conflict.State, conflict.LastOperation, conflict.ErrorCode, conflict.CanRetry));
        var version = Assert.IsType<OctorateVersionDto>(conflict.OctorateVersion);
        // The days are written as the booking's own are (midnight UTC of the stay day), so a screen puts the two side by side.
        Assert.Equal(new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), version.CheckIn);
        Assert.Equal(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc), version.CheckOut);
        Assert.Equal(1, version.Guests);

        // The dates stored in another state are stale: a failed push shows none, and a screen that compared with them would show a difference that is not there.
        var failed = (await DetailAsync(
            booking, reader: Mirror(booking, OctorateSyncState.Failed, "octorate_unavailable", OctorateSyncOperation.Update, held))).OctorateSync!;
        Assert.Null(failed.OctorateVersion);

        // A conflict whose dates were not kept, or cannot be read, has none to show.
        var unreadable = (await DetailAsync(
            booking, reader: Mirror(booking, OctorateSyncState.Conflict, OctorateSyncErrorCodes.ReservationChanged, OctorateSyncOperation.Update, "not json"))).OctorateSync!;
        Assert.Equal("Conflict", unreadable.State);
        Assert.Null(unreadable.OctorateVersion);
    }

    [Theory]
    [InlineData(OctorateSyncState.Pending, false)]
    [InlineData(OctorateSyncState.Synced, false)]
    [InlineData(OctorateSyncState.Failed, true)]
    [InlineData(OctorateSyncState.Conflict, true)]
    [InlineData(OctorateSyncState.NotAllowedByChannel, false)]
    public async Task BookingDetail_CanRetry_IsTheRuleOfTheRetryEndpoint(OctorateSyncState state, bool expected)
    {
        var booking = ChannelBooking(BookingSource.Manual, reservationId: "7005");

        var sync = (await DetailAsync(booking, reader: Mirror(booking, state))).OctorateSync!;

        Assert.Equal(expected, sync.CanRetry);
        Assert.Equal(expected, OctorateBookingSync.IsRetryable(state));
    }

    [Fact]
    public async Task BookingDetail_WhileTheConnectorIsOff_TheRetryEndpointIs404_SoNoRetryIsOffered()
    {
        var booking = ChannelBooking(BookingSource.Manual, reservationId: "7006");

        var sync = (await DetailAsync(booking, reader: Mirror(booking, OctorateSyncState.Failed, connectorOn: false))).OctorateSync!;

        Assert.Equal("Failed", sync.State);
        Assert.False(sync.CanRetry);
    }

    [Fact]
    public async Task BookingDetail_ARowOfAnotherOrg_IsNeverTheStateOfTheBooking()
    {
        var booking = ChannelBooking(BookingSource.Manual, reservationId: "7007");
        // A row for the very same booking id that belongs to another org (a wrong write, a forged id): the org is part of the question.
        var reader = Mirror(booking, OctorateSyncState.Conflict, OctorateSyncErrorCodes.ReservationChanged, rowOrgId: OtherOrgId);

        Assert.Null((await DetailAsync(booking, reader: reader)).OctorateSync);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<BookingDetailDto> DetailAsync(
        Booking booking,
        bool canReadPayments = true,
        bool canWrite = true,
        IBookingOctorateSyncReader? reader = null,
        OtaChannelActionPolicy? policy = null)
    {
        var bookings = new Mock<IBookingService>();
        bookings.Setup(b => b.GetBookingAsync(booking.Id)).ReturnsAsync(booking);
        var access = new Mock<IPropertyAuthorizationService>();
        access.Setup(a => a.CanAccessPropertyAsync(OwnerId, PropertyId, It.IsAny<IEnumerable<string>>())).ReturnsAsync(true);

        var result = await Controller(bookings.Object, access.Object, reader, policy)
            .GetById(booking.Id, Authorization(canReadPayments, canWrite));

        return Assert.IsType<BookingDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static IAuthorizationService Authorization(bool canReadPayments = true, bool canWrite = true) =>
        HostAuthorizationTestHarness.Create(
            OrgId,
            (_, permission) => permission switch
            {
                "payment.read" => canReadPayments,
                "booking.write" => canWrite,
                _ => true,
            });

    private static BookingsController Controller(
        IBookingService bookings,
        IPropertyAuthorizationService access,
        IBookingOctorateSyncReader? reader = null,
        OtaChannelActionPolicy? policy = null)
    {
        var db = NewDb(Guid.NewGuid().ToString());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" })
            .Build();
        return new BookingsController(
            bookings,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IPropertyService>(),
            access,
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), configuration),
            Mock.Of<IOtaStayService>(),
            Mock.Of<ILogger<BookingsController>>(),
            Options.Create(new BookingsOptions { DefaultPageSize = 10 }),
            reader,
            policy)
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

    private static AppDbContext NewDb(string database) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options);

    /// <summary>
    /// The real reader over a database that holds the mirror of <paramref name="booking"/> in <paramref name="state"/> (none when <c>null</c>); a Synced
    /// row has been confirmed at <see cref="SyncedAt"/>.
    /// </summary>
    private static BookingOctorateSyncReader Mirror(
        Booking booking,
        OctorateSyncState? state,
        string? errorCode = null,
        OctorateSyncOperation lastOperation = OctorateSyncOperation.Create,
        string? versionJson = null,
        bool connectorOn = true,
        Guid? rowOrgId = null)
    {
        var db = NewDb($"booking-read-model-{Guid.NewGuid():N}");
        if (state is { } value)
        {
            db.OctorateBookingSyncs.Add(new OctorateBookingSync
            {
                OrgId = rowOrgId ?? OrgId,
                BookingId = booking.Id,
                PropertyId = PropertyId,
                State = value,
                LastOperation = lastOperation,
                Attempts = 1,
                LastSyncedAt = value == OctorateSyncState.Synced ? SyncedAt : null,
                ErrorCode = errorCode,
                OctorateVersionJson = versionJson,
            });
            db.SaveChanges();
        }

        return new BookingOctorateSyncReader(db, connectorOn ? new TestFeatureFlags(FeatureFlags.OctorateConnector) : new TestFeatureFlags());
    }

    /// <summary>A confirmed booking of Airbnb (or <paramref name="source"/>) with a reservation on Octorate, in a property in francs, nothing paid.</summary>
    private static Booking ChannelBooking(BookingSource source = BookingSource.Airbnb, string? reservationId = "7001")
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
            Source = source,
            OctorateReservationId = reservationId,
            TotalPrice = 330.55m,
        };
    }
}
