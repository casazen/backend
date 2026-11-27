using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SwissComplianceStayLifecycleTests
{
    private static readonly DateTime Day = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly ArrivalDay = DateOnly.FromDateTime(Day);
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task PoliceDay_HoldsTheStaysArrivingOnTheDay_ConfirmedCheckedInOrCheckedOut_AndNoOther()
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
        AddStay(db, property, BookingStatus.CheckedIn, from: Day.AddDays(-1));
        AddStay(db, property, BookingStatus.Confirmed, from: Day.AddDays(1));
        var otherProperty = await SeedPropertyAsync(db);
        AddStay(db, otherProperty, BookingStatus.CheckedIn);
        // A malformed cross-tenant child must not leak through a background context with its tenant filter disabled.
        var foreign = AddStay(db, property, BookingStatus.CheckedIn);
        foreign.OrgId = Guid.NewGuid();
        await db.SaveChangesAsync();

        var day = await new TicinoNotificationService(db, Clock).GetDayAsync(property.Id, ArrivalDay);

        Assert.Equal(3, day.StayCount);
        // The bookers have no check-in data yet: every stay is reported, none is in the file.
        Assert.Empty(day.Plan.Registrations);
        Assert.Equal(expected.Select(b => b.Id).Order(), day.Plan.Issues.Select(i => i.BookingId).Distinct().Order());
        Assert.Equal(0, day.ReceiptCount);
    }

    [Fact]
    public async Task PoliceFile_IsTheOfficialCsvOfTheCompleteGuests_AndTheDayIsNotifiedOnceItsReceiptIsRecorded()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, structureId: "NL-12345678");
        var stay = AddStay(db, property, BookingStatus.CheckedIn, guests: 1);
        db.StayGuests.Add(new StayGuest
        {
            BookingId = stay.Id,
            OrgId = property.OrgId,
            Position = 0,
            FirstName = "Anna",
            LastName = "Test",
            DateOfBirth = new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            CitizenshipCode = "100000241",
            CitizenshipName = "SVIZZERA",
            DocumentTypeCode = "PASOR",
            DocumentNumber = "X1234567",
        });
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        var file = await service.GenerateFileAsync(property.Id, ArrivalDay, TicinoPoliceFileFormat.Csv);
        var pendingBefore = await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay);
        db.SwissComplianceReceipts.Add(new SwissComplianceReceipt
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Type = "Police",
            ReportDate = Day,
            Reference = file.Reference,
            UploadedFileHash = file.FileHash,
        });
        await db.SaveChangesAsync();

        var lines = Encoding.Latin1.GetString(file.Content).Split("\r\n");
        Assert.Equal(string.Join(';', TicinoPoliceFile.CsvColumns), lines[0]);
        Assert.StartsWith("NL-12345678;Test;Anna;04.01.1984;CHE;18.11.2026;20.11.2026;", lines[1], StringComparison.Ordinal);
        Assert.Equal(1, file.Rows);
        Assert.Equal("notifiche-ti-2026-11-18.csv", file.FileName);
        Assert.Equal(64, file.FileHash.Length);
        Assert.Equal(ArrivalDay, Assert.Single(pendingBefore).Day);
        Assert.Empty(await service.ListPendingDaysAsync(property.OrgId, null, ArrivalDay));
        Assert.Equal(1, (await service.GetDayAsync(property.Id, ArrivalDay)).ReceiptCount);
    }

    [Fact]
    public async Task PoliceFile_IsRefused_WithoutTheStructureIdentifier_OrWithoutACompleteGuest()
    {
        await using var db = NewDb();
        var withoutId = await SeedPropertyAsync(db);
        var withId = await SeedPropertyAsync(db, structureId: "XNA9999");
        AddStay(db, withId, BookingStatus.Confirmed);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        var missingId = await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateFileAsync(withoutId.Id, ArrivalDay, TicinoPoliceFileFormat.Csv));
        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateFileAsync(withId.Id, ArrivalDay, TicinoPoliceFileFormat.Xml));

        Assert.Equal("ticino_structure_id_missing", missingId.Code);
        Assert.Equal("ticino_notification_empty", empty.Code);
    }

    [Fact]
    public async Task GroupSheet_IsTheSheetOfAGroupOfTheProperty_NeverOfABookingOfAnotherOrg()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, structureId: "NL-12345678");
        var group = AddStay(db, property, BookingStatus.CheckedIn, guests: 6);
        for (var position = 0; position < 6; position++)
        {
            db.StayGuests.Add(new StayGuest
            {
                BookingId = group.Id,
                OrgId = property.OrgId,
                Position = position,
                Type = position == 0 ? StayGuestType.HeadOfGroup : StayGuestType.GroupMember,
                FirstName = $"Test{position}",
                LastName = "Gruppo",
                DateOfBirth = new DateTime(1990, 1, position + 1, 0, 0, 0, DateTimeKind.Utc),
                CitizenshipCode = "100000241",
                CitizenshipName = "SVIZZERA",
                DocumentTypeCode = position == 0 ? "PASOR" : null,
                DocumentNumber = position == 0 ? "X1234567" : string.Empty,
            });
        }

        var notAGroup = AddStay(db, property, BookingStatus.CheckedIn, guests: 2);
        var otherOrgProperty = await SeedPropertyAsync(db);
        var foreign = AddStay(db, otherOrgProperty, BookingStatus.CheckedIn);
        await db.SaveChangesAsync();
        var service = new TicinoNotificationService(db, Clock);

        var sheet = await service.GetGroupSheetAsync(property.Id, group.Id);
        var day = await service.GetDayAsync(property.Id, ArrivalDay);
        var foreignError = await Assert.ThrowsAsync<NotFoundException>(() => service.GetGroupSheetAsync(property.Id, foreign.Id));
        var notAGroupError = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetGroupSheetAsync(property.Id, notAGroup.Id));

        // The six persons, the leader first; the file of the day has the leader's row only, with the five members counted on it.
        Assert.Equal((6, "text/csv; charset=utf-8"), (sheet.Members, sheet.ContentType));
        Assert.StartsWith("scheda-gruppo-ti-2026-11-18-", sheet.FileName, StringComparison.Ordinal);
        var lines = Encoding.UTF8.GetString(sheet.Content.AsSpan(Encoding.UTF8.Preamble.Length)).Split("\r\n");
        Assert.Equal(["Nome;Cognome;Data di nascita;Nazionalità", "Test0;Gruppo;01.01.1990;CHE"], lines.Take(2));
        Assert.Equal(8, lines.Length); // the header, the six persons and the empty text after the last CRLF
        var leader = Assert.Single(day.Plan.Registrations, r => r.BookingId == group.Id);
        Assert.Equal((TicinoRowKind.Group, 6, 5, 0), (leader.Kind, leader.GroupSize, leader.PersonsOver14, leader.PersonsUnder14));

        // A booking of another org is never read through this property, and a stay that is not a group has no sheet.
        Assert.Equal("booking_not_found", foreignError.Code);
        Assert.Equal("ticino_not_a_group", notAGroupError.Code);
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
        var police = new TicinoNotificationService(db, Clock);
        Assert.False(await police.IsPropertyInTicinoAsync(property.Id));
        var policeError = await Assert.ThrowsAsync<DomainRuleException>(() => police.GetDayAsync(property.Id, ArrivalDay));
        Assert.Equal("property_not_in_ticino", policeError.Code);
        Assert.Empty(await police.ListPendingDaysAsync(property.OrgId, null, ArrivalDay));
        var otrError = await Assert.ThrowsAsync<DomainRuleException>(() => new OtrTaxService(db).GeneratePeriodReportAsync(property.Id, Day, Day.AddDays(1)));
        Assert.Equal("property_not_in_ticino", otrError.Code);
    }

    private static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Property> SeedPropertyAsync(AppDbContext db, string country = "CH", string canton = "TI", string? structureId = null)
    {
        var municipalityCode = canton == "TI" ? "5192" : "261";
        if (!await db.SwissMunicipalities.AnyAsync(m => m.MunicipalityCode == municipalityCode))
            db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = municipalityCode, CantonCode = canton, OfficialName = "TEST" });
        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = "test@example.com" };
        db.Orgs.Add(org);
        var property = new Property
        {
            OrgId = org.Id,
            Name = "TEST",
            Country = country,
            SwissMunicipalityCode = municipalityCode,
            TicinoStructureId = structureId,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private static Booking AddStay(
        AppDbContext db, Property property, BookingStatus status, DateTime? from = null, DateTime? to = null, int guests = 2)
    {
        var guest = new Guest { OrgId = property.OrgId, FirstName = "TEST", LastName = "Guest" };
        db.Guests.Add(guest);
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            CheckInDate = from ?? Day,
            CheckOutDate = to ?? Day.AddDays(2),
            NumberOfGuests = guests,
        };
        db.Bookings.Add(booking);
        return booking;
    }
}
