using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The rules of the channels for changing and cancelling a booking that came from them (E08/F02/US02/T01, D8, NF-75, NF-76): closed by default,
/// a channel that is not listed is closed, every booking says what the host may do with it and, when not, the one sentence that tells how to ask
/// the guest or the channel; and the two services that change a booking refuse a booking of a closed channel and change nothing.
/// </summary>
public class OtaChannelActionPolicyTests
{
    private static readonly Guid OrgId = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a81");

    /// <summary>2026-11-06 09:00 in Rome.</summary>
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public void For_WithoutConfiguration_EverySourceIsClosedAndHasItsSentence()
    {
        foreach (var source in Enum.GetValues<BookingSource>())
        {
            var rule = OtaChannelActionPolicy.Closed.For(source);

            Assert.False(rule.CanModify, source.ToString());
            Assert.False(rule.CanCancel, source.ToString());
            Assert.False(string.IsNullOrWhiteSpace(rule.ModifyGuidanceKey), source.ToString());
            Assert.False(string.IsNullOrWhiteSpace(rule.CancelGuidanceKey), source.ToString());
        }
    }

    [Fact]
    public void For_TheCommittedAppsettings_EveryChannelIsClosed_WithItsOwnSentence()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        var options = configuration.GetSection(OctorateChannelActionsOptions.SectionName).Get<OctorateChannelActionsOptions>();

        Assert.NotNull(options);
        var policy = new OtaChannelActionPolicy(options!);
        foreach (var source in OtaChannelGuidanceKeys.Channels)
        {
            var rule = policy.For(source);
            Assert.False(rule.CanModify, source.ToString());
            Assert.False(rule.CanCancel, source.ToString());
            Assert.Equal(OtaChannelGuidanceKeys.ModifyOf(source), rule.ModifyGuidanceKey);
            Assert.Equal(OtaChannelGuidanceKeys.CancelOf(source), rule.CancelGuidanceKey);
        }
    }

    [Fact]
    public void For_AChannelTheConfigurationOpens_IsTheOnlyOneThatIsOpen()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Octorate:ChannelActions:BookingCom:CanCancel"] = "true",
                ["Octorate:ChannelActions:BookingCom:CancelGuidanceKey"] = "UnaltraChiave",
            })
            .Build();
        var options = configuration.GetSection(OctorateChannelActionsOptions.SectionName).Get<OctorateChannelActionsOptions>();

        Assert.NotNull(options);
        var policy = new OtaChannelActionPolicy(options!);

        var opened = policy.For(BookingSource.BookingCom);
        Assert.True(opened.CanCancel);
        Assert.False(opened.CanModify);
        Assert.Equal("UnaltraChiave", opened.CancelGuidanceKey);
        Assert.Equal(OtaChannelGuidanceKeys.ModifyOf(BookingSource.BookingCom), opened.ModifyGuidanceKey);
        foreach (var source in Enum.GetValues<BookingSource>().Where(s => s != BookingSource.BookingCom))
        {
            Assert.False(policy.For(source).CanCancel, source.ToString());
            Assert.False(policy.For(source).CanModify, source.ToString());
        }
    }

    [Fact]
    public void Guidance_TheSentenceOfEveryChannelAndAction_IsInBothResourceFiles()
    {
        var italian = ResourceTexts("SharedResources.resx");
        var english = ResourceTexts("SharedResources.en.resx");

        foreach (var source in OtaChannelGuidanceKeys.Channels.Append(BookingSource.OtherChannel))
        {
            foreach (var key in new[] { OtaChannelGuidanceKeys.ModifyOf(source), OtaChannelGuidanceKeys.CancelOf(source) })
            {
                Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"{key} is missing from SharedResources.resx");
                Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"{key} is missing from SharedResources.en.resx");
            }
        }
    }

    [Fact]
    public void ChangeOptionsOf_TheBookingsCasaZenHolds_FollowTheExistingRules()
    {
        // Entered by the host: changed and cancelled while it is neither started nor over.
        var manual = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Manual, BookingStatus.Confirmed));
        Assert.True(manual.CanEdit);
        Assert.True(manual.CanCancel);
        Assert.Null(manual.ReasonCode);
        Assert.Null(manual.GuidanceKey);
        Assert.Null(manual.Channel);

        // A confirmed direct booking, even one mirrored on Octorate: its dates and guests are changed here, with the money settled (E08/F02/US01/T01).
        var direct = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Direct, BookingStatus.Confirmed, "5001"));
        Assert.True(direct.CanEdit);
        Assert.True(direct.CanCancel);
        Assert.Null(direct.ReasonCode);
        Assert.Null(direct.GuidanceKey);
        Assert.Null(direct.Channel);

        // A direct booking that is still a request (a hold being paid, a «pay at the property» request) is not a booking yet: it keeps its terms.
        var request = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Direct, BookingStatus.Pending));
        Assert.False(request.CanEdit);
        Assert.Equal(BookingErrorCodes.SourceLocked, request.ReasonCode);

        var inTheHouse = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Manual, BookingStatus.CheckedIn));
        Assert.False(inTheHouse.CanEdit);
        Assert.True(inTheHouse.CanCancel);
        Assert.Equal(BookingErrorCodes.StayLocked, inTheHouse.ReasonCode);

        var cancelled = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Manual, BookingStatus.Cancelled));
        Assert.False(cancelled.CanEdit);
        Assert.False(cancelled.CanCancel);
        Assert.Equal(BookingErrorCodes.NotEditable, cancelled.ReasonCode);
        Assert.Equal(BookingErrorCodes.AlreadyCancelled, cancelled.CancelReasonCode);

        var over = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Manual, BookingStatus.CheckedOut));
        Assert.False(over.CanEdit);
        Assert.False(over.CanCancel);
        Assert.Equal(BookingErrorCodes.NotCancellable, over.CancelReasonCode);

        // A stay made from the iCal feed of a channel has no reservation on Octorate: it is CasaZen's own record, cancelled as always.
        var ical = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.Airbnb, BookingStatus.Confirmed, reservationId: null));
        Assert.True(ical.CanCancel);
        Assert.Null(ical.Channel);
        Assert.Null(ical.GuidanceKey);
    }

    [Fact]
    public void ChangeOptionsOf_ABookingOfAChannel_FollowsTheRuleOfItsChannel()
    {
        var booking = NewBooking(BookingSource.BookingCom, BookingStatus.Confirmed, "7001");

        // Without a policy every channel is closed: nothing can be done from CasaZen and the sentence of the channel says how to ask.
        var closed = BookingEditRules.ChangeOptionsOf(booking);
        Assert.False(closed.CanEdit);
        Assert.False(closed.CanCancel);
        Assert.Equal(BookingErrorCodes.OtaBookingChangeNotAllowed, closed.ReasonCode);
        Assert.Equal("OtaChannelModifyGuidanceBookingCom", closed.GuidanceKey);
        Assert.Equal("BookingCom", closed.Channel);
        Assert.Equal(BookingErrorCodes.OtaBookingCancelNotAllowed, closed.CancelReasonCode);
        Assert.Equal("OtaChannelCancelGuidanceBookingCom", closed.CancelGuidanceKey);

        // The cancellation opened: the booking can be cancelled, through Octorate. It still cannot be changed: its change is not built.
        var cancelOpen = BookingEditRules.ChangeOptionsOf(booking, Policy(BookingSource.BookingCom, canCancel: true));
        Assert.True(cancelOpen.CanCancel);
        Assert.Null(cancelOpen.CancelReasonCode);
        Assert.Null(cancelOpen.CancelGuidanceKey);
        Assert.False(cancelOpen.CanEdit);
        Assert.Equal(BookingErrorCodes.OtaBookingChangeNotAllowed, cancelOpen.ReasonCode);

        // The change opened: the booking stays locked where its price and payment came from, and says so.
        var modifyOpen = BookingEditRules.ChangeOptionsOf(booking, Policy(BookingSource.BookingCom, canModify: true));
        Assert.False(modifyOpen.CanEdit);
        Assert.Equal(BookingErrorCodes.SourceLocked, modifyOpen.ReasonCode);
        Assert.False(modifyOpen.CanCancel);

        // Once cancelled or over, the channel has nothing left to be asked.
        var cancelled = BookingEditRules.ChangeOptionsOf(NewBooking(BookingSource.BookingCom, BookingStatus.Cancelled, "7001"));
        Assert.False(cancelled.CanEdit);
        Assert.False(cancelled.CanCancel);
        Assert.Equal(BookingErrorCodes.NotEditable, cancelled.ReasonCode);
        Assert.Null(cancelled.GuidanceKey);
        Assert.Equal(BookingErrorCodes.AlreadyCancelled, cancelled.CancelReasonCode);
        Assert.Null(cancelled.CancelGuidanceKey);
        Assert.Equal("BookingCom", cancelled.Channel);
    }

    /// <summary>
    /// PROOF TEST: data rule (D8, NF-75, NF-76). A booking that came from a channel whose channel is closed is changed and cancelled from
    /// CasaZen: both services refuse it with 422 and the sentence of the channel (<c>ota_booking_change_not_allowed</c>,
    /// <c>ota_booking_cancel_not_allowed</c>), and the booking is exactly as it was: not a date, a note, a status or a cancellation time
    /// differs, no event is recorded, nothing is refunded or sent to Stripe. A cancellation of a channel that the configuration opens is not made
    /// by this service either: it goes through Octorate first.
    /// </summary>
    [Fact]
    public async Task OtaBookingChange_ClosedChannel_ChangesNothing_Test()
    {
        var database = $"ota-closed-{Guid.NewGuid():N}";
        var tenant = new RequestTenant(OrgId);
        var bookingId = await SeedAsync(database, BookingSource.Airbnb, "7001");
        Booking before;
        await using (var read = NewDb(database, tenant))
            before = await read.Bookings.AsNoTracking().SingleAsync();

        await using var db = NewDb(database, tenant);
        var recorder = new DomainEventRecorder(db, tenant, Clock);
        var stripe = new Mock<IStripeService>(MockBehavior.Strict);
        var hostBookings = HostBookings(db, recorder);
        var cancellations = Cancellations(db, stripe.Object, recorder, null);

        // The change: other dates, another number of guests, a new note. Refused, with the sentence of the channel.
        var update = new HostBookingUpdate(
            bookingId,
            before.CheckInDate.AddDays(1),
            before.CheckOutDate.AddDays(2),
            3,
            0,
            null,
            "Una nota nuova");
        var change = await Assert.ThrowsAsync<DomainRuleException>(() => hostBookings.UpdateAsync(update, CancellationToken.None));
        Assert.Equal(BookingErrorCodes.OtaBookingChangeNotAllowed, change.Code);
        Assert.Equal("OtaChannelModifyGuidanceAirbnb", change.MessageKey);
        Assert.Equal("OtaChannelModifyGuidanceAirbnb", change.Extensions[OtaChannelActionPolicy.GuidanceKeyExtension]);
        Assert.Equal("Airbnb", change.Extensions[OtaChannelActionPolicy.ChannelExtension]);

        // The cancellation, with a refund asked for: refused, with the sentence of the channel. Stripe is strict: any call would fail the test.
        var cancel = await Assert.ThrowsAsync<DomainRuleException>(
            () => cancellations.CancelAsync(new BookingCancellationRequest(bookingId, 50m, "Disdetta", "auth0|host"), CancellationToken.None));
        Assert.Equal(BookingErrorCodes.OtaBookingCancelNotAllowed, cancel.Code);
        Assert.Equal("OtaChannelCancelGuidanceAirbnb", cancel.MessageKey);
        Assert.Equal("Airbnb", cancel.Extensions[OtaChannelActionPolicy.ChannelExtension]);

        // The quote says it is not cancellable; a channel the configuration opens says it is, and its cancellation is still not made here.
        Assert.False((await cancellations.GetQuoteAsync(bookingId)).Cancellable);
        var openCancellations = Cancellations(db, stripe.Object, recorder, Policy(BookingSource.Airbnb, canCancel: true));
        Assert.True((await openCancellations.GetQuoteAsync(bookingId)).Cancellable);
        var stillRefused = await Assert.ThrowsAsync<DomainRuleException>(
            () => openCancellations.CancelAsync(new BookingCancellationRequest(bookingId, null, null, "auth0|host"), CancellationToken.None));
        Assert.Equal(BookingErrorCodes.OtaBookingCancelNotAllowed, stillRefused.Code);

        // Exactly as it was.
        await using var after = NewDb(database, tenant);
        var stored = await after.Bookings.AsNoTracking().SingleAsync();
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Null(stored.CancelledAt);
        Assert.Null(stored.CancellationNote);
        Assert.Equal(before.CheckInDate, stored.CheckInDate);
        Assert.Equal(before.CheckOutDate, stored.CheckOutDate);
        Assert.Equal(before.NumberOfGuests, stored.NumberOfGuests);
        Assert.Equal(before.SpecialRequests, stored.SpecialRequests);
        Assert.Equal(before.TotalPrice, stored.TotalPrice);
        Assert.Equal(before.UpdatedAt, stored.UpdatedAt);
        Assert.Equal("7001", stored.OctorateReservationId);
        Assert.Empty(await after.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await after.PaymentRefunds.AsNoTracking().ToListAsync());
        stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateAsync_TheAmountsOfTheChannel_AreStillReadOnly_BeforeTheRuleOfTheChannel()
    {
        var database = $"ota-amounts-{Guid.NewGuid():N}";
        var tenant = new RequestTenant(OrgId);
        var bookingId = await SeedAsync(database, BookingSource.Airbnb, "7001");
        await using var db = NewDb(database, tenant);
        var read = await db.Bookings.AsNoTracking().SingleAsync();

        var update = new HostBookingUpdate(
            bookingId, read.CheckInDate, read.CheckOutDate, 2, 0, null, "Nota", new OtaAmountsInput(99m, null, null, null));
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => HostBookings(db, null).UpdateAsync(update, CancellationToken.None));

        // ADR-033-R3 comes first: the figures of the channel are never edited, whatever the rule of the channel is.
        Assert.Equal(BookingErrorCodes.OtaAmountsReadonly, refused.Code);
    }

    private static HostBookingService HostBookings(AppDbContext db, DomainEventRecorder? recorder) =>
        new(
            db,
            Mock.Of<IBookingService>(),
            Mock.Of<IBookingRepository>(),
            Mock.Of<ICheckoutHoldExpiryService>(),
            ICalTestServices.PropertySync(db, Mock.Of<ISafeExternalHttpClient>(), new ConfigurationBuilder().Build()),
            Mock.Of<IOnSiteBookingRequestService>(),
            NullLogger<HostBookingService>.Instance,
            Clock,
            recorder);

    private static BookingCancellationService Cancellations(AppDbContext db, IStripeService stripe, DomainEventRecorder recorder, OtaChannelActionPolicy? policy)
    {
        var emails = new Mock<IEmailQueue>();
        var refunds = new PaymentRefundService(
            db, stripe, Mock.Of<IPaymentRefundRetryScheduler>(), emails.Object, NullLogger<PaymentRefundService>.Instance, Clock);
        var notifier = new BookingNotifier(
            db, emails.Object, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance);
        return new BookingCancellationService(
            db, refunds, stripe, notifier, NullLogger<BookingCancellationService>.Instance, Clock, recorder, policy);
    }

    private static OtaChannelActionPolicy Policy(BookingSource source, bool canModify = false, bool canCancel = false)
    {
        var entry = new OctorateChannelActionOptions { CanModify = canModify, CanCancel = canCancel };
        var options = new OctorateChannelActionsOptions();
        switch (source)
        {
            case BookingSource.BookingCom:
                options.BookingCom = entry;
                break;
            case BookingSource.Airbnb:
                options.Airbnb = entry;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Not a channel of this test.");
        }

        return new OtaChannelActionPolicy(options);
    }

    private static Booking NewBooking(BookingSource source, BookingStatus status, string? reservationId = null) =>
        new() { Source = source, Status = status, OctorateReservationId = reservationId };

    /// <summary>A confirmed booking of 10 to 13 December 2026 that came from a channel, with its property and guest.</summary>
    private static async Task<Guid> SeedAsync(string database, BookingSource source, string? reservationId)
    {
        await using var db = NewDb(database, null);
        var property = new Property
        {
            OrgId = OrgId,
            OwnerId = "auth0|host",
            Name = "Casa del Lago",
            Address = "Via del Lago 1",
            City = "Como",
            MaxGuests = 4,
            Currency = "EUR",
        };
        var guest = new Guest { OrgId = OrgId, FirstName = "Giulia", LastName = "Rossi", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 13, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = BookingStatus.Confirmed,
            Source = source,
            OctorateReservationId = reservationId,
            SpecialRequests = "Arriviamo tardi",
            BasePrice = 300m,
            TouristTax = 6m,
            TotalPrice = 306m,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static AppDbContext NewDb(string database, RequestTenant? tenant) =>
        tenant is null
            ? new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options)
            : new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, tenant);

    private static Dictionary<string, string> ResourceTexts(string fileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Resources", fileName);
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
