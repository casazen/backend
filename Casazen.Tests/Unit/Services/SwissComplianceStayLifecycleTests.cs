using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SwissComplianceStayLifecycleTests
{
    private static readonly DateTime Day = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PoliceSummary_KeepsCheckedInAndCheckedOutStays_AndExcludesCancelledPendingAndOtherProperty()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var expected = new[]
        {
            AddStay(db, property, BookingStatus.Confirmed),
            AddStay(db, property, BookingStatus.CheckedIn),
            AddStay(db, property, BookingStatus.CheckedOut),
        };
        AddStay(db, property, BookingStatus.Pending);
        AddStay(db, property, BookingStatus.Cancelled);
        AddStay(db, property, BookingStatus.CheckedOut, from: Day.AddDays(-3), to: Day);
        AddStay(db, property, BookingStatus.Confirmed, from: Day.AddDays(1));
        var otherProperty = await SeedPropertyAsync(db);
        AddStay(db, otherProperty, BookingStatus.CheckedIn);
        // A malformed cross-tenant child must not leak through a background context with its tenant filter disabled.
        var foreign = AddStay(db, property, BookingStatus.CheckedIn);
        foreign.OrgId = Guid.NewGuid();
        await db.SaveChangesAsync();

        var result = await new TicinoNotificationService(db).GenerateDailyNotificationAsync(property.Id, Day);
        var ids = XDocument.Parse(result.XmlContent).Descendants("BookingId").Select(x => Guid.Parse(x.Value)).ToHashSet();
        Assert.Equal(expected.Select(b => b.Id).Order(), ids.Order());
        Assert.Equal(64, result.FileHash.Length);
    }

    [Fact]
    public async Task OtrSummary_KeepsCompletedStays_ClipsNightsToTheRequestedPeriod_AndExcludesCancelled()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var first = AddStay(db, property, BookingStatus.CheckedOut, Day.AddDays(-2), Day.AddDays(1));
        var second = AddStay(db, property, BookingStatus.CheckedIn, Day.AddDays(1), Day.AddDays(4));
        var confirmed = AddStay(db, property, BookingStatus.Confirmed, Day.AddDays(1), Day.AddDays(2));
        var cancelled = AddStay(db, property, BookingStatus.Cancelled);
        AddStay(db, property, BookingStatus.Pending);
        AddStay(db, property, BookingStatus.CheckedOut, Day.AddDays(-3), Day);
        AddStay(db, property, BookingStatus.Confirmed, Day.AddDays(2), Day.AddDays(3));
        var otherProperty = await SeedPropertyAsync(db);
        var other = AddStay(db, otherProperty, BookingStatus.CheckedOut);
        await db.SaveChangesAsync();

        var result = await new OtrTaxService(db).GeneratePeriodReportAsync(property.Id, Day, Day.AddDays(2));
        var rows = result.CsvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Contains(rows, row => row.Contains(first.Id.ToString()) && row.TrimEnd('\r').EndsWith(",1,2", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(second.Id.ToString()) && row.TrimEnd('\r').EndsWith(",1,2", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(confirmed.Id.ToString()));
        Assert.DoesNotContain(cancelled.Id.ToString(), result.CsvContent);
        Assert.DoesNotContain(other.Id.ToString(), result.CsvContent);
    }

    [Theory]
    [InlineData("IT")]
    [InlineData("CH", "ZH")]
    public async Task SwissExports_RejectNonTicinoJurisdictions_EvenWithAStaleSwissMunicipality(string country, string canton = "TI")
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, country, canton);
        var police = new TicinoNotificationService(db);
        Assert.False(await police.IsPropertyInTicinoAsync(property.Id));
        var policeError = await Assert.ThrowsAsync<DomainRuleException>(() => police.GenerateDailyNotificationAsync(property.Id, Day));
        Assert.Equal("property_not_in_ticino", policeError.Code);
        var otrError = await Assert.ThrowsAsync<DomainRuleException>(() => new OtrTaxService(db).GeneratePeriodReportAsync(property.Id, Day, Day.AddDays(1)));
        Assert.Equal("property_not_in_ticino", otrError.Code);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Property> SeedPropertyAsync(AppDbContext db, string country = "CH", string canton = "TI")
    {
        var municipalityCode = canton == "TI" ? "5113" : "261";
        if (!await db.SwissMunicipalities.AnyAsync(m => m.MunicipalityCode == municipalityCode))
            db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = municipalityCode, CantonCode = canton, OfficialName = "TEST" });
        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = "test@example.com" };
        db.Orgs.Add(org);
        var property = new Property { OrgId = org.Id, Name = "TEST", Country = country, SwissMunicipalityCode = municipalityCode };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private static Booking AddStay(AppDbContext db, Property property, BookingStatus status, DateTime? from = null, DateTime? to = null)
    {
        var guest = new Guest { OrgId = property.OrgId, FirstName = "TEST", LastName = "Guest" };
        db.Guests.Add(guest);
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            CheckInDate = from ?? Day.AddDays(-1),
            CheckOutDate = to ?? Day.AddDays(2),
            NumberOfGuests = 2,
        };
        db.Bookings.Add(booking);
        return booking;
    }
}
