using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The proofs of the host wizard "Guest check-in" and of the no-show (E03/F01/US01/T04, NF-40, NF-45): a statistical field is asked only where
/// the region requires it, another org's booking is not found by any operation, and a stay whose police communication went cannot be marked
/// as a no-show.
/// </summary>
public class CheckInWizardServiceTests
{
    private static readonly Guid OrgA = Guid.Parse("6a1f0c2e-3b4d-4e5f-8a9b-0c1d2e3f4a51");
    private static readonly Guid OrgB = Guid.Parse("8c3e2a4b-5d6f-4a7b-9c8d-1e2f3a4b5c62");

    /// <summary>The check-in day of the stays below, at 19:00 in Rome.</summary>
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    private static readonly DateTime CheckIn = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CheckInWizard_RegionFieldsAskedOnlyWhenRequired_Test()
    {
        var guest = CompleteGuest();
        Assert.Null(guest.TourismType);
        Assert.Null(guest.TravelWay);

        // No region requires a statistical field: an empty one is never an issue (data minimization).
        Assert.DoesNotContain(Issues(guest, []), i => i.Code == CheckInIssueCodes.GuestFieldRequired);

        // The region requires the kind of tourism: the empty field is a blocking issue, and only that field.
        var required = Issues(guest, [IstatGuestFieldLists.TourismTypeField]);
        var issue = Assert.Single(required, i => i.Code == CheckInIssueCodes.GuestFieldRequired);
        Assert.Equal("tourismType", issue.Field);
        Assert.Equal(guest.Position, issue.Position);
        Assert.True(issue.Blocking);
        Assert.DoesNotContain(required, i => i.Field == "travelWay");

        // Filled in, it is no issue any more.
        guest.TourismType = "holiday";
        Assert.DoesNotContain(Issues(guest, [IstatGuestFieldLists.TourismTypeField]), i => i.Code == CheckInIssueCodes.GuestFieldRequired);

        // An expired document is a warning, never a block.
        guest.DocumentExpiresOn = DateOnly.FromDateTime(CheckIn).AddDays(-1);
        var expired = Assert.Single(Issues(guest, []), i => i.Code == CheckInIssueCodes.DocumentExpired);
        Assert.False(expired.Blocking);
    }

    [Fact]
    public async Task CheckInWizard_OtherOrganizationBooking_NotFound_Test()
    {
        var databaseName = $"checkin-wizard-{Guid.NewGuid():N}";
        Guid bookingId;
        await using (var seed = NewDb(databaseName, null))
            bookingId = (await SeedStayAsync(seed, OrgA)).Id;

        // Org B: the state, the guests, the confirmation and the no-show of org A's booking are all not found (404).
        await using (var dbB = NewDb(databaseName, new FixedTenant(OrgB)))
        {
            var wizard = Wizard(dbB);
            var lifecycle = Lifecycle(dbB);
            await Assert.ThrowsAsync<NotFoundException>(() => wizard.GetStateAsync(bookingId, CancellationToken.None));
            await Assert.ThrowsAsync<NotFoundException>(() => wizard.SaveGuestsAsync(bookingId, [], "auth0|host-b", CancellationToken.None));
            await Assert.ThrowsAsync<NotFoundException>(() => wizard.ConfirmAsync(bookingId, "auth0|host-b", CancellationToken.None));
            await Assert.ThrowsAsync<NotFoundException>(() => lifecycle.MarkNoShowAsync(bookingId, CancellationToken.None));
        }

        await using (var check = NewDb(databaseName, null))
            Assert.Null((await check.Bookings.SingleAsync(b => b.Id == bookingId)).NoShowAt);

        // Org A, the control: the same calls find the booking.
        await using var dbA = NewDb(databaseName, new FixedTenant(OrgA));
        var state = await Wizard(dbA).GetStateAsync(bookingId, CancellationToken.None);
        Assert.Equal(bookingId, state.Booking.BookingId);
        Assert.Equal(StayAttendance.Expected, state.Attendance);
        var marked = await Lifecycle(dbA).MarkNoShowAsync(bookingId, CancellationToken.None);
        Assert.NotNull(marked.NoShowAt);
    }

    [Fact]
    public async Task StayLifecycle_NoShowRefusedAfterAlloggiatiSent_Test()
    {
        // The communication sent with a receipt, declared sent by the host, or an attempt sending or accepted: refused, nothing changes.
        var sentCases = new Func<Booking, object>[]
        {
            b => Report(b, AlloggiatiWebStatus.Inviato),
            b => Report(b, AlloggiatiWebStatus.InviatoManualmente),
            b => Attempt(b, AlloggiatiAttemptStatus.Sending),
            b => Attempt(b, AlloggiatiAttemptStatus.Accepted),
            b => Attempt(b, AlloggiatiAttemptStatus.PartiallyAccepted),
        };
        foreach (var sent in sentCases)
        {
            await using var db = NewDb($"no-show-{Guid.NewGuid():N}", null);
            var booking = await SeedStayAsync(db, OrgA);
            db.Add(sent(booking));
            await db.SaveChangesAsync();

            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => Lifecycle(db).MarkNoShowAsync(booking.Id, CancellationToken.None));

            Assert.Equal(CheckInReadinessRules.NoShowAlloggiatiSentCode, refused.Code);
            Assert.Null((await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).NoShowAt);
        }

        // A communication still to send (or refused): the no-show is marked, once.
        await using var open = NewDb($"no-show-{Guid.NewGuid():N}", null);
        var stay = await SeedStayAsync(open, OrgA);
        open.Add(Report(stay, AlloggiatiWebStatus.DaInviareManualmente));
        open.Add(Attempt(stay, AlloggiatiAttemptStatus.Rejected));
        await open.SaveChangesAsync();

        var first = await Lifecycle(open).MarkNoShowAsync(stay.Id, CancellationToken.None);
        var markedAt = first.NoShowAt;
        var again = await Lifecycle(open).MarkNoShowAsync(stay.Id, CancellationToken.None);
        Assert.NotNull(markedAt);
        Assert.Equal(markedAt, again.NoShowAt);
        Assert.Equal(BookingStatus.Confirmed, again.Status);
    }

    [Fact]
    public void CheckInWizard_AlloggiatiNext_NoShowOrInactiveStay_SendsNobodyToThePortal_Test()
    {
        var sender = new PropertySenderInfo(
            Guid.NewGuid(), OrgA, ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.DefaultNotConnected, true, null, null);

        AlloggiatiStatusInfo Status(AlloggiatiWebStatus status) =>
            new(Guid.NewGuid(), status, null, null, null, CheckIn.AddDays(1), false, 24, false, false);

        // The control: an expected stay whose communication is still to send is sent by the host on the portal, with the guide.
        Assert.Equal(
            CheckInAlloggiatiNextStep.Manual,
            CheckInWizardService.AlloggiatiNext(sender, Status(AlloggiatiWebStatus.DaInviareManualmente), StayAttendance.Expected).Step);

        // A no-show, a request still waiting and a cancelled booking owe no communication: nothing to do on the portal, whatever the status.
        foreach (var attendance in new[] { StayAttendance.NoShow, StayAttendance.Inactive })
        {
            Assert.Equal(
                CheckInAlloggiatiNextStep.NotApplicable,
                CheckInWizardService.AlloggiatiNext(sender, Status(AlloggiatiWebStatus.DaInviareManualmente), attendance).Step);
        }

        Assert.Equal(
            CheckInAlloggiatiNextStep.NotApplicable,
            CheckInWizardService.AlloggiatiNext(sender, Status(AlloggiatiWebStatus.NotDue), StayAttendance.Expected).Step);
    }

    [Fact]
    public async Task StayLifecycle_LateArrivalAfterANoShow_ClearsTheMark_AndTheStayOwesItsCommunicationAgain_Test()
    {
        await using var db = NewDb($"late-arrival-{Guid.NewGuid():N}", null);
        var booking = await SeedStayAsync(db, OrgA);
        var lifecycle = Lifecycle(db);

        var marked = await lifecycle.MarkNoShowAsync(booking.Id, CancellationToken.None);
        Assert.NotNull(marked.NoShowAt);
        Assert.Equal(StayAttendance.NoShow, CheckInAttendance.Of(marked));
        Assert.False(AlloggiatiStatusRules.IsDue(marked));

        var arrival = await lifecycle.RegisterArrivalAsync(booking.Id, CancellationToken.None);

        // The guests did come: the mark goes, the stay is checked in and owes its communication like any other.
        Assert.Null(arrival.Booking.NoShowAt);
        Assert.Equal(BookingStatus.CheckedIn, arrival.Booking.Status);
        Assert.Equal(StayAttendance.Arrived, CheckInAttendance.Of(arrival.Booking));
        Assert.True(AlloggiatiStatusRules.IsDue(arrival.Booking));
        Assert.Null((await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).NoShowAt);
    }

    private static IReadOnlyList<CheckInIssue> Issues(StayGuest guest, IReadOnlyCollection<string> requiredFields) =>
        CheckInReadinessRules.Evaluate(new CheckInReadinessInput(
            [guest], null, DateOnly.FromDateTime(CheckIn), DeclaredGuests: 1, OctorateReceivesGuests: false, requiredFields));

    /// <summary>A single guest with a complete Alloggiati record and no statistical field.</summary>
    private static StayGuest CompleteGuest() => new()
    {
        Position = 0,
        Type = StayGuestType.SingleGuest,
        FirstName = "Ospite",
        LastName = "Di Prova",
        Gender = Gender.Female,
        DateOfBirth = new DateTime(1988, 4, 12, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Roma",
        BirthProvince = "RM",
        CitizenshipName = "Italia",
        ResidenceInItaly = true,
        ResidenceComuneName = "Roma",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "CATEST001",
        DocumentIssuePlaceName = "Roma",
        DocumentExpiresOn = new DateOnly(2031, 1, 1),
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    /// <summary>A confirmed stay of <paramref name="orgId"/> whose check-in day is today in Rome.</summary>
    private static async Task<Booking> SeedStayAsync(AppDbContext db, Guid orgId)
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|host-a",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            MaxGuests = 4,
        };
        var booker = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = booker.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckIn.AddDays(2),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            BasePrice = 200m,
            TotalPrice = 200m,
        };
        db.AddRange(property, booker, booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static AlloggiatiWebReport Report(Booking booking, AlloggiatiWebStatus status) => new()
    {
        BookingId = booking.Id,
        OrgId = booking.OrgId,
        GuestId = booking.GuestId,
        Status = status,
    };

    private static AlloggiatiSendAttempt Attempt(Booking booking, AlloggiatiAttemptStatus status) => new()
    {
        OrgId = booking.OrgId,
        BookingId = booking.Id,
        PropertyId = booking.PropertyId,
        AccountKey = Guid.NewGuid(),
        Mode = AlloggiatiMode.Live,
        Status = status,
        Trigger = "test",
        SnapshotHash = new string('0', AlloggiatiSendAttempt.SnapshotHashLength),
        GuestCount = 1,
        LeaseExpiresAt = CheckIn.AddHours(1),
        ReservedAt = CheckIn,
    };

    private static StayLifecycleService Lifecycle(AppDbContext db) =>
        new(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            NullLogger<StayLifecycleService>.Instance,
            Clock);

    /// <summary>The wizard over <paramref name="db"/>: the services it composes answer for a stay with no registered guest, never sent.</summary>
    private static CheckInWizardService Wizard(AppDbContext db)
    {
        var stayGuests = new Mock<IStayGuestService>();
        stayGuests
            .Setup(s => s.GetForBookingAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StayGuest>());
        var codeTables = new Mock<IAlloggiatiCodeTableService>();
        codeTables
            .Setup(c => c.LoadCodeBookAsync(It.IsAny<IEnumerable<StayGuest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlloggiatiCodeBook.Empty);
        var tax = new Mock<IStayTouristTaxService>();
        tax
            .Setup(t => t.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(StayTouristTaxService.NotCalculableCode, "TouristTaxNotCalculable"));
        var octorate = new Mock<ICheckInOctorateSyncService>();
        octorate
            .Setup(o => o.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCheckInView(false, [], []));
        var senders = new Mock<IPropertySenderStore>();
        senders
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid propertyId, CancellationToken _) => new PropertySenderInfo(
                propertyId, OrgA, ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.DefaultNotConnected, true, null, null));
        var alloggiati = new Mock<IAlloggiatiWebService>();
        alloggiati
            .Setup(a => a.GetStatusAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new AlloggiatiStatusInfo(
                id, AlloggiatiWebStatus.DaInviare, null, null, null, CheckIn.AddDays(1), false, 24, false, false));
        var flags = new Mock<IFeatureFlags>();

        return new CheckInWizardService(
            db,
            stayGuests.Object,
            codeTables.Object,
            tax.Object,
            octorate.Object,
            senders.Object,
            alloggiati.Object,
            Mock.Of<IAlloggiatiReportScheduler>(),
            flags.Object,
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions()),
            NullLogger<CheckInWizardService>.Instance,
            Clock);
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
