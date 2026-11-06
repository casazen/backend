using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The tourist tax of a stay guest by guest (E03/F01/US07/T02, NF-46, ADR-037): the proofs of its money, permission and privacy rules, on the
/// real services and the in-memory database (the seeded exemption reasons, the official rate of Como).
/// </summary>
public class StayTouristTaxServiceTests
{
    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-11-06T09:00:00Z", CultureInfo.InvariantCulture));
    private static readonly DateTime CheckIn = new(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc);

    private const string ComoIstat = "013075";
    private const string SevesoIstat = "108040";

    [Fact]
    public async Task StayTouristTax_ChannelCollected_NoDoubleCharge_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = await CreateContextAsync(databaseName);
        db.TouristTaxRates.Add(TouristTaxRateSeed.BuildRates().Single(r => r.City == "Como"));
        db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
        {
            Channel = BookingSource.Airbnb,
            IstatCode = ComoIstat,
            Collector = TouristTaxCollector.Channel,
            SourceUrl = "https://example.com/como-airbnb-riscuote",
        });
        var (_, booking) = await SeedStayAsync(db, Guid.NewGuid(), "Como", ComoIstat, BookingSource.Airbnb, (40, null), (38, null));
        var service = Service(db);

        var view = await service.GetAsync(booking.Id);

        // The channel collects and pays the comune: the host must collect nothing, the amounts per guest are kept (3 nights x 3,00 each).
        Assert.Equal(TouristTaxCollector.Channel, view.Collector);
        Assert.Equal(TouristTaxCollectorSource.ChannelRule, view.CollectorSource);
        Assert.Equal(0m, view.AmountDue);
        Assert.Equal(18.00m, view.Total);
        Assert.All(view.Guests, g => Assert.Equal(9.00m, g.Amount));

        var first = await service.ConfirmAsync(booking.Id, "auth0|host");
        var second = await service.ConfirmAsync(booking.Id, "auth0|host");

        // A second confirmation of the same calculation adds no second line and no second record: one line per guest of the booking.
        var record = await db.StayTouristTaxes.Include(t => t.Guests).SingleAsync(t => t.BookingId == booking.Id);
        Assert.Equal(2, record.Guests.Count);
        Assert.Equal(new[] { 0, 1 }, record.Guests.Select(g => g.Position).Order());
        Assert.Equal(0m, record.AmountDue);
        Assert.Equal(18.00m, record.Total);
        Assert.Equal(TouristTaxCollector.Channel, record.Collector);
        Assert.NotNull(record.ChannelRuleId);
        Assert.Equal(first.ConfirmedAt, second.ConfirmedAt);
        Assert.False(second.Stale);
        Assert.Equal(new DateOnly(2036, 12, 13), record.RetainUntil);
    }

    [Fact]
    public async Task StayTouristTax_NoRate_NeverZero_Test()
    {
        await using var db = await CreateContextAsync(Guid.NewGuid().ToString());
        var (_, booking) = await SeedStayAsync(db, Guid.NewGuid(), "Seveso", SevesoIstat, BookingSource.Direct, (40, null), (9, null));
        var service = Service(db);

        var view = await service.GetAsync(booking.Id);

        // No official rate and no rate of the org: the tax is unknown, never 0.
        Assert.True(view.MissingRate);
        Assert.Equal(TouristTaxRateSource.None, view.RateSource);
        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, view.Status);
        Assert.Null(view.Total);
        Assert.Null(view.AmountDue);
        Assert.All(view.Guests, g => Assert.Null(g.Amount));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.ConfirmAsync(booking.Id, "auth0|host"));
        Assert.Equal(StayTouristTaxService.NotCalculableCode, refused.Code);
        Assert.False(await db.StayTouristTaxes.AnyAsync(t => t.BookingId == booking.Id));
    }

    [Fact]
    public async Task StayTouristTax_HostRate_PrivateToOrganization_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = await CreateContextAsync(databaseName);
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var (propertyA, bookingA) = await SeedStayAsync(db, orgA, "Seveso", SevesoIstat, BookingSource.Direct, (40, null));
        var (propertyB, bookingB) = await SeedStayAsync(db, orgB, "Seveso", SevesoIstat, BookingSource.Direct, (40, null));
        var service = Service(db);

        await service.SaveMunicipalityRateAsync(
            propertyA.Id,
            new MunicipalityRateInput(2.00m, MaxNights: null, MinimumAge: 14, null, null, null, "Delibera di test del comune"),
            "auth0|host-a");

        // Org A calculates with its own rate; org B, same comune, neither sees it nor is taxed with it.
        var forA = await service.GetAsync(bookingA.Id);
        Assert.Equal(TouristTaxRateSource.HostDeclared, forA.RateSource);
        Assert.Equal(6.00m, forA.Total);
        Assert.Null((await service.GetMunicipalityRateAsync(propertyB.Id)).Rate);
        var forB = await service.GetAsync(bookingB.Id);
        Assert.True(forB.MissingRate);
        Assert.Null(forB.Total);

        // Seen by org B (the tenant filter of its requests), org A's booking and property do not exist: 404.
        await using var asOrgB = new AppDbContext(Options(databaseName), new FixedTenant(orgB));
        var serviceOfB = Service(asOrgB);
        await Assert.ThrowsAsync<NotFoundException>(() => serviceOfB.GetAsync(bookingA.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => serviceOfB.ConfirmAsync(bookingA.Id, "auth0|host-b"));
        await Assert.ThrowsAsync<NotFoundException>(() => serviceOfB.GetMunicipalityRateAsync(propertyA.Id));
    }

    [Fact]
    public async Task StayTouristTax_ExemptionReasonClearedOnErasure_Test()
    {
        await using var db = await CreateContextAsync(Guid.NewGuid().ToString());
        db.TouristTaxRates.Add(TouristTaxRateSeed.BuildRates().Single(r => r.City == "Como"));
        var (_, booking) = await SeedStayAsync(
            db, Guid.NewGuid(), "Como", ComoIstat, BookingSource.Direct, (40, null), (45, TouristTaxExemptionReason.HealthCareCode));
        var service = Service(db);
        await service.ConfirmAsync(booking.Id, "auth0|host");

        var eraser = new GuestDataEraser(db, Mock.Of<IFileStorage>(), NullLogger<GuestDataEraser>.Instance);
        await eraser.AnonymizeStayGuestsOfBookingsAsync([booking.Id], Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        await db.SaveChangesAsync();

        // The reason (it can reveal a health condition) goes with the guest; the kind and the amounts stay for the fiscal retention.
        var lines = (await db.StayTouristTaxes.Include(t => t.Guests).SingleAsync(t => t.BookingId == booking.Id))
            .Guests.OrderBy(g => g.Position).ToList();
        Assert.All(lines, line => Assert.Null(line.ExemptionCode));
        Assert.Equal(TouristTaxExemptionKind.None, lines[0].ExemptionKind);
        Assert.Equal(9.00m, lines[0].Amount);
        Assert.Equal(TouristTaxExemptionKind.Host, lines[1].ExemptionKind);
        Assert.Equal(0m, lines[1].Amount);
        Assert.All(await db.StayGuests.Where(s => s.BookingId == booking.Id).ToListAsync(), g => Assert.Null(g.TaxExemptionCode));
    }

    private static StayTouristTaxService Service(AppDbContext db) =>
        new(
            db,
            new StayGuestService(db, new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance)),
            new TouristTaxQuoteService(new TouristTaxRateRepository(db), NullLogger<TouristTaxQuoteService>.Instance, db),
            NullLogger<StayTouristTaxService>.Instance,
            Clock);

    private static DbContextOptions<AppDbContext> Options(string databaseName) =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options;

    private static async Task<AppDbContext> CreateContextAsync(string databaseName)
    {
        var db = new AppDbContext(Options(databaseName));
        await db.Database.EnsureCreatedAsync(); // the seeded exemption reasons
        return db;
    }

    /// <summary>
    /// A confirmed stay of three nights from <see cref="CheckIn"/> in the comune, with its registered guests (age at check-in and the host's
    /// exemption), the first one the head of the family.
    /// </summary>
    private static async Task<(Property Property, Booking Booking)> SeedStayAsync(
        AppDbContext db,
        Guid orgId,
        string city,
        string istatCode,
        BookingSource source,
        params (int Age, string? Exemption)[] guests)
    {
        if (!await db.Orgs.AnyAsync(o => o.Id == orgId))
            db.Orgs.Add(new OrgEntity { Id = orgId, Name = "Org", Slug = $"org-{orgId:N}", DisplayName = "Org" });
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = city,
            ComuneIstatCode = istatCode,
            PostalCode = "20000",
            NightlyRate = 100m,
            MaxGuests = 4,
        };
        var booker = new Guest { Id = Guid.NewGuid(), OrgId = orgId, FirstName = "Anna", LastName = "Bianchi", Email = "anna@example.com" };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Property = property,
            PropertyId = property.Id,
            Guest = booker,
            GuestId = booker.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckIn.AddDays(3),
            Status = BookingStatus.Confirmed,
            Source = source,
            NumberOfGuests = guests.Length,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.AddRange(property, booker, booking);
        for (var position = 0; position < guests.Length; position++)
        {
            db.StayGuests.Add(new StayGuest
            {
                BookingId = booking.Id,
                OrgId = orgId,
                Position = position,
                Type = guests.Length == 1
                    ? StayGuestType.SingleGuest
                    : position == 0 ? StayGuestType.HeadOfFamily : StayGuestType.FamilyMember,
                FirstName = "Ospite",
                LastName = "Test",
                Gender = Gender.Female,
                DateOfBirth = CheckIn.AddYears(-guests[position].Age).AddDays(-30),
                TaxExemptionCode = guests[position].Exemption,
            });
        }

        await db.SaveChangesAsync();
        return (property, booking);
    }

    private sealed class FixedTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
