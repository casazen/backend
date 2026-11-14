using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E25/F01/US01/T01, acceptance criterion 2: the tax step of the host check-in wizard confirms the tax of the stay, and the confirmation records no event, so the
/// collection is evaluated right after it (a confirmed amount lower than what the price included or the guest paid raises the host's «To do»). A tax that is not
/// confirmed evaluates nothing.
/// </summary>
public class CheckInWizardTouristTaxTests
{
    private static readonly Guid OrgId = Guid.Parse("4d1e0a3b-6c2f-4b7a-9e8d-2f3a4b5c6d71");

    /// <summary>The check-in day of the stay below, at 19:00 in Rome.</summary>
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.Zero));

    private static readonly DateTime CheckIn = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CheckInWizard_ConfirmingTheTax_EvaluatesTheCollectionRightAfter_Test()
    {
        await using var db = NewDb();
        var booking = await SeedStayAsync(db);
        var order = new List<string>();
        var tax = new Mock<IStayTouristTaxService>();
        tax.Setup(t => t.GetAsync(booking.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Calculated(booking.Id));
        tax.Setup(t => t.ConfirmAsync(booking.Id, "auth0|host", It.IsAny<CancellationToken>())).Callback(() => order.Add("confirm")).ReturnsAsync(Calculated(booking.Id));
        var collection = new Mock<ITouristTaxCollectionService>();
        collection
            .Setup(c => c.ReconcileAfterChangeAsync(booking.Id, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("reconcile"))
            .Returns(Task.CompletedTask);

        var confirmation = await Wizard(db, booking, tax.Object, collection.Object).ConfirmAsync(booking.Id, "auth0|host", CancellationToken.None);

        // The tax is confirmed, then the collection is evaluated (never before, once).
        Assert.True(confirmation.Tax.Saved);
        Assert.Equal(["confirm", "reconcile"], order);
    }

    [Fact]
    public async Task CheckInWizard_ATaxThatCannotBeCalculated_IsNotConfirmed_AndEvaluatesNothing()
    {
        await using var db = NewDb();
        var booking = await SeedStayAsync(db);
        var tax = new Mock<IStayTouristTaxService>();
        tax.Setup(t => t.GetAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(StayTouristTaxService.NotCalculableCode, "TouristTaxNotCalculable"));
        var collection = new Mock<ITouristTaxCollectionService>();

        var confirmation = await Wizard(db, booking, tax.Object, collection.Object).ConfirmAsync(booking.Id, "auth0|host", CancellationToken.None);

        Assert.False(confirmation.Tax.Saved);
        Assert.Equal(StayTouristTaxService.NotCalculableCode, confirmation.Tax.Reason);
        tax.Verify(t => t.ConfirmAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        collection.VerifyNoOtherCalls();
    }

    /// <summary>The tax of a stay of one guest, calculable: 15.00 to collect.</summary>
    private static StayTouristTaxView Calculated(Guid bookingId) => new(
        bookingId,
        TouristTaxQuoteStatus.Calculated,
        TouristTaxRateSource.Official,
        false,
        2,
        2,
        false,
        [],
        15m,
        15m,
        TouristTaxCollector.Host,
        TouristTaxCollectorSource.Default,
        false,
        [],
        [],
        null,
        false);

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"checkin-wizard-tax-{Guid.NewGuid():N}").Options);

    /// <summary>A confirmed stay of one guest whose check-in day is today in Rome.</summary>
    private static async Task<Booking> SeedStayAsync(AppDbContext db)
    {
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
            OwnerId = "auth0|host",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = "Roma",
            MaxGuests = 4,
        };
        var booker = new Guest { Id = Guid.NewGuid(), OrgId = OrgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = OrgId,
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

    /// <summary>A single guest with a complete Alloggiati record and no statistical field: nothing blocks the confirmation.</summary>
    private static StayGuest CompleteGuest(Booking booking) => new()
    {
        Id = Guid.NewGuid(),
        BookingId = booking.Id,
        OrgId = booking.OrgId,
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

    /// <summary>The wizard over <paramref name="db"/> for a stay with one complete guest, never sent to the channel manager.</summary>
    private static CheckInWizardService Wizard(AppDbContext db, Booking booking, IStayTouristTaxService tax, ITouristTaxCollectionService collection)
    {
        var stayGuests = new Mock<IStayGuestService>();
        stayGuests
            .Setup(s => s.GetForBookingAsync(It.IsAny<Booking>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CompleteGuest(booking) });
        var codeTables = new Mock<IAlloggiatiCodeTableService>();
        codeTables
            .Setup(c => c.LoadCodeBookAsync(It.IsAny<IEnumerable<StayGuest>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlloggiatiCodeBook.Empty);
        var octorate = new Mock<ICheckInOctorateSyncService>();
        octorate
            .Setup(o => o.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCheckInView(false, [], []));
        octorate
            .Setup(o => o.SendGuestsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateCheckInSendResult(OctorateCheckInSendState.NotMirrored, "no_reservation", false, [], false));
        var senders = new Mock<IPropertySenderStore>();
        senders
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid propertyId, CancellationToken _) => new PropertySenderInfo(
                propertyId, OrgId, ComplianceSenderType.Manual, ComplianceSenderType.Manual, SenderSource.DefaultNotConnected, true, null, null));
        var alloggiati = new Mock<IAlloggiatiWebService>();
        alloggiati
            .Setup(a => a.GetStatusAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new AlloggiatiStatusInfo(
                id, AlloggiatiWebStatus.DaInviare, null, null, null, CheckIn.AddDays(1), false, 24, false, false));

        return new CheckInWizardService(
            db,
            stayGuests.Object,
            codeTables.Object,
            tax,
            octorate.Object,
            senders.Object,
            alloggiati.Object,
            Mock.Of<IAlloggiatiReportScheduler>(),
            new Mock<IFeatureFlags>().Object,
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions()),
            NullLogger<CheckInWizardService>.Instance,
            Clock,
            collection);
    }
}
