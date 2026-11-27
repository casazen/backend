using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.SwissCompliance;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E31/F01/US01/T01 (NF-130, casazen/backend#979): the guest register of a Swiss property for a period, on the data of the check-in. It lists every guest of the stays that arrived in the
/// period (by arrival day, both ends counted) and only the stays that owe a police communication; it is made for a property of any canton, Ticino included, and never for an Italian one; the
/// retention hint is the one of the regime; and every file leaves one audit row with no personal data. The rules that cost if they fail are the three proofs: another organization never
/// reads a register (<c>SwissComplianceControllerTests.GuestRegister_OtherOrganizationForbidden_Test</c> for the web layer, <see cref="GuestRegister_OtherOrganization_IsNotFoundAndNothingIsAudited_Test"/>
/// for the data), a register of people who did not stay would be false (<see cref="GuestRegister_ExcludesCancelledAndNoShow_Test"/>) and the audit row without personal data
/// (<see cref="GuestRegister_Audit_NoPersonalData_Test"/>).
/// </summary>
public class GuestRegisterServiceTests
{
    private const string Anna = "auth0|anna";
    private static readonly DateOnly Day = new(2026, 11, 19);
    private static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));

    // ─── Which guests ───

    [Fact]
    public async Task Register_ListsEveryGuestOfTheStaysThatArrived_ByArrivalAndStay_AndTheInfoCountsTheSameLines()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var early = AddStay(db, property, BookingStatus.CheckedOut, Day.AddDays(-1));
        AddGuest(db, early, 0, "Elena", "Bianchi", StayGuestType.SingleGuest, new DateTime(1980, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        var family = AddStay(db, property, BookingStatus.CheckedIn, Day, guests: 3);
        AddGuest(db, family, 1, "Luca", "Verdi", StayGuestType.FamilyMember, new DateTime(2015, 5, 6, 0, 0, 0, DateTimeKind.Utc));
        AddGuest(db, family, 0, "Marco", "Verdi", StayGuestType.HeadOfFamily, new DateTime(1982, 7, 8, 0, 0, 0, DateTimeKind.Utc));
        AddGuest(db, family, 2, "Sara", "Verdi", StayGuestType.FamilyMember, new DateTime(1984, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
        var service = NewService(db);

        var info = await service.GetInfoAsync(property.Id, Day.AddDays(-1), Day);
        var register = await service.GenerateAsync(property.Id, Day.AddDays(-1), Day, new GuestRegisterRequest(Anna, "it"));

        // Four guests: the stay of the day before first, then the family in the order of its check-in; the adults and the minor alike.
        var lines = Lines(register.Content);
        Assert.Equal(["Bianchi", "Verdi", "Verdi", "Verdi"], lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(';')[2]));
        Assert.Equal(["Elena", "Marco", "Luca", "Sara"], lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(';')[3]));
        Assert.Equal(["No", "No", "Sì", "No"], lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(';')[14]));
        Assert.Equal((4, 4), (register.Rows, info.RowCount));
        Assert.Equal((property.Id, Day.AddDays(-1), Day), (info.PropertyId, info.From, info.To));
        Assert.Equal("registro-ospiti-ch-2026-11-18-2026-11-19.csv", register.FileName);
        Assert.Equal("text/csv; charset=utf-8", register.ContentType);
    }

    [Fact]
    public async Task GuestRegister_ExcludesCancelledAndNoShow_Test()
    {
        // PROOF TEST (data): a register of people who did not stay would be false. A cancelled booking, a request or hold that was never confirmed and a no-show are not listed; a
        // confirmed, a checked-in and a checked-out stay are (the one definition of a stay that owes a police communication, AlloggiatiStatusRules).
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        foreach (var (name, status, noShow) in new (string, BookingStatus, bool)[]
                 {
                     ("Confermato", BookingStatus.Confirmed, false),
                     ("Arrivato", BookingStatus.CheckedIn, false),
                     ("Partito", BookingStatus.CheckedOut, false),
                     ("Annullato", BookingStatus.Cancelled, false),
                     ("Richiesta", BookingStatus.Pending, false),
                     ("Mancato", BookingStatus.Confirmed, true),
                 })
        {
            var stay = AddStay(db, property, status, Day);
            stay.NoShowAt = noShow ? Day.ToDateTime(TimeOnly.MinValue).AddHours(23) : null;
            AddGuest(db, stay, 0, name, "Ospite", StayGuestType.SingleGuest, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        await db.SaveChangesAsync();
        var service = NewService(db);

        var info = await service.GetInfoAsync(property.Id, Day, Day);
        var register = await service.GenerateAsync(property.Id, Day, Day, new GuestRegisterRequest(Anna, "it"));

        var text = Encoding.UTF8.GetString(register.Content);
        Assert.Equal(3, info.RowCount);
        Assert.Equal(3, register.Rows);
        foreach (var listed in new[] { "Confermato", "Arrivato", "Partito" })
            Assert.Contains($";{listed};", text, StringComparison.Ordinal);
        foreach (var notListed in new[] { "Annullato", "Richiesta", "Mancato" })
            Assert.DoesNotContain(notListed, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_ThePeriodIsByArrivalDay_BothEndsCounted_TheDaysAroundAreNot_AndAStayThatOnlyOverlapsIsNot()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var first = new DateOnly(2026, 4, 1);
        var last = new DateOnly(2026, 4, 30);
        foreach (var (name, arrival) in new[]
                 {
                     ("Prima", first.AddDays(-1)),
                     ("Inizio", first),
                     ("Fine", last),
                     ("Dopo", last.AddDays(1)),
                 })
        {
            var stay = AddStay(db, property, BookingStatus.CheckedOut, arrival, nights: 5);
            AddGuest(db, stay, 0, name, "Ospite", StayGuestType.SingleGuest, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        await db.SaveChangesAsync();
        var service = NewService(db);

        // «Prima» arrived on 31 March and leaves on 5 April, inside the period, but it did not arrive in it.
        var register = await service.GenerateAsync(property.Id, first, last, new GuestRegisterRequest(Anna, "it"));

        var lines = Lines(register.Content).Skip(1).Where(l => l.Length > 0).ToList();
        Assert.Equal(["Inizio", "Fine"], lines.Select(l => l.Split(';')[3]));
        Assert.Equal(["01.04.2026", "30.04.2026"], lines.Select(l => l.Split(';')[0]));
        Assert.Equal(["06.04.2026", "05.05.2026"], lines.Select(l => l.Split(';')[1]));
    }

    [Fact]
    public async Task Register_AStayWithoutCheckInRows_ListsTheBooker_AsTheTicinoFileDoes()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var stay = AddStay(db, property, BookingStatus.Confirmed, Day, guests: 1, plate: "TI 112233");
        var booker = db.Guests.Local.Single();
        booker.FirstName = "Hanna";
        booker.LastName = "Keller";
        booker.DateOfBirth = new DateTime(1990, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        booker.Nationality = "GERMANIA";
        booker.DocumentType = GuestDocumentType.IdentityCard;
        booker.DocumentNumber = "L01X00T47";
        booker.Address = "Seestrasse 12";
        booker.PostalCode = "8002";
        booker.City = "Zürich";
        stay.BookingCode = "7Q3KZ8M2XB";
        await db.SaveChangesAsync();
        var service = NewService(db);

        var register = await service.GenerateAsync(property.Id, Day, Day, new GuestRegisterRequest(Anna, "it"));

        var line = Assert.Single(Lines(register.Content).Skip(1), l => l.Length > 0);
        Assert.Equal("19.11.2026;21.11.2026;Keller;Hanna;02.03.1990;DEU;Carta d'identità;L01X00T47;Seestrasse 12;8002;Zürich;;TI 112233;7Q3KZ-8M2XB;No;", line);
    }

    [Fact]
    public async Task Register_ThePeriodWithNoGuest_IsTheHeaderAlone_AndStillAuditedOnce()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        await db.SaveChangesAsync();
        var service = NewService(db);

        var info = await service.GetInfoAsync(property.Id, Day, Day);
        var register = await service.GenerateAsync(property.Id, Day, Day, new GuestRegisterRequest(Anna, "en"));

        Assert.Equal(0, info.RowCount);
        Assert.Equal(0, register.Rows);
        Assert.Equal(
            ["Arrival;Departure;Last name;First name;Date of birth;Nationality;Document type;Document number;Street;Postal code;City;Country;Vehicle plate;Booking code;Minor;Signature", string.Empty],
            Lines(register.Content));
        var row = Assert.Single(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
        Assert.Equal(("register", 0, register.FileHash), (row.Format, row.RowCount, row.Sha256));
    }

    // ─── The language of the words ───

    [Theory]
    [InlineData("it", "Arrivo;Partenza;Cognome;Nome;Data di nascita;Nazionalità;")]
    [InlineData(null, "Arrivo;Partenza;Cognome;Nome;Data di nascita;Nazionalità;")]
    [InlineData("de", "Arrivo;Partenza;Cognome;Nome;Data di nascita;Nazionalità;")]
    [InlineData("en", "Arrival;Departure;Last name;First name;Date of birth;Nationality;")]
    [InlineData("en-GB", "Arrival;Departure;Last name;First name;Date of birth;Nationality;")]
    public async Task Register_TheWordsAreItalianOrEnglish_Italian_ForAnyOtherLanguage(string? language, string header)
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        await db.SaveChangesAsync();
        var service = NewService(db);

        var register = await service.GenerateAsync(property.Id, Day, Day, new GuestRegisterRequest(Anna, language));

        Assert.StartsWith(header, Lines(register.Content)[0], StringComparison.Ordinal);
    }

    // ─── The period ───

    [Fact]
    public async Task Register_AYearIsTheMostAPeriodMayBe_ADayMoreIsRefused_AndSoIsAnEndBeforeTheStart_AndNothingIsAudited()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        await db.SaveChangesAsync();
        var service = NewService(db);
        var from = new DateOnly(2028, 1, 1);

        // 2028 is a leap year: 366 days, both ends counted, is allowed.
        var year = await service.GenerateAsync(property.Id, from, new DateOnly(2028, 12, 31), new GuestRegisterRequest(Anna, "it"));
        var info = await service.GetInfoAsync(property.Id, from, new DateOnly(2028, 12, 31));
        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.GenerateAsync(property.Id, from, new DateOnly(2029, 1, 1), new GuestRegisterRequest(Anna, "it")));
        var tooLongInfo = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetInfoAsync(property.Id, from, new DateOnly(2029, 1, 1)));
        var inverted = await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateAsync(property.Id, from, from.AddDays(-1)));
        var invertedInfo = await Assert.ThrowsAsync<DomainRuleException>(() => service.GetInfoAsync(property.Id, from, from.AddDays(-1)));

        Assert.Equal(("registro-ospiti-ch-2028-01-01-2028-12-31.csv", 0, 0), (year.FileName, year.Rows, info.RowCount));
        Assert.Equal(["swiss_period_too_long", "swiss_period_too_long", "swiss_period_invalid", "swiss_period_invalid"], new[] { tooLong.Code, tooLongInfo.Code, inverted.Code, invertedInfo.Code });
        Assert.Equal([366], tooLong.MessageArgs.Select(a => (int)a));

        // Only the register that was made left a row.
        Assert.Single(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
    }

    // ─── Which properties, which regime ───

    [Fact]
    public async Task Register_IsMadeForAnyCanton_TicinoIncluded_AndTheRetentionHintIsTheOneOfTheRegime_AbsentWhenTheSourcesDoNotSayIt()
    {
        await using var db = NewDb();
        var ticino = await SeedPropertyAsync(db, canton: "TI");
        var zurich = await SeedPropertyAsync(db, canton: "ZH");
        var unknown = await SeedPropertyAsync(db, municipalityCode: null);
        await db.SaveChangesAsync();
        var service = NewService(db);

        var inTicino = await service.GetInfoAsync(ticino.Id, Day, Day);
        var inZurich = await service.GetInfoAsync(zurich.Id, Day, Day);
        var nowhere = await service.GetInfoAsync(unknown.Id, Day, Day);
        var file = await service.GenerateAsync(zurich.Id, Day, Day, new GuestRegisterRequest(Anna));

        // Ticino: five years, from the verified entry; Zurich: the only number the research has is how long a police system keeps the data, not the host's, so there is none;
        // a Swiss property whose municipality is not known has the register too, with no canton and no number.
        Assert.Equal(("CH-TI", (int?)5), (inTicino.RegimeCode, inTicino.RetentionYears));
        Assert.Equal(("CH-ZH", (int?)null), (inZurich.RegimeCode, inZurich.RetentionYears));
        Assert.Equal(("CH-UNKNOWN", (int?)null), (nowhere.RegimeCode, nowhere.RetentionYears));
        Assert.Equal(("CH-ZH", (int?)null), (file.RegimeCode, file.RetentionYears));
        Assert.Equal(GuestRegistrationRegimeCatalog.ForCanton("TI").RetentionYears, inTicino.RetentionYears);
    }

    [Theory]
    [InlineData("IT")]
    [InlineData(" it ")]
    [InlineData("")]
    [InlineData("DE")]
    public async Task Register_AnItalianPropertyHasNone_ItsRegisterIsTheAlloggiatiFile_AndNeitherDoesAnotherCountry(string country)
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db, country: country);
        AddGuest(db, AddStay(db, property, BookingStatus.CheckedIn, Day), 0, "Mario", "Rossi", StayGuestType.SingleGuest, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
        var service = NewService(db);

        var file = await Assert.ThrowsAsync<NotFoundException>(() => service.GenerateAsync(property.Id, Day, Day, new GuestRegisterRequest(Anna)));
        var info = await Assert.ThrowsAsync<NotFoundException>(() => service.GetInfoAsync(property.Id, Day, Day));

        Assert.Equal("guest_register_not_available", file.Code);
        Assert.Equal("guest_register_not_available", info.Code);
        Assert.Equal("GuestRegisterNotAvailable", file.MessageKey);
        Assert.Empty(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GuestRegister_OtherOrganization_IsNotFoundAndNothingIsAudited_Test()
    {
        // PROOF TEST (data, tenant): another organization's property is not found, for the register and for the info, and nothing is audited for it; the stays of another org that
        // point at the property (a row no screen can make) are never listed, because every read names the org of the property.
        var store = Guid.NewGuid().ToString();
        Property mine;
        Property theirs;
        await using (var setup = NewDb(store))
        {
            mine = await SeedPropertyAsync(setup);
            theirs = await SeedPropertyAsync(setup);
            AddGuest(setup, AddStay(setup, mine, BookingStatus.CheckedIn, Day), 0, "Mio", "Ospite", StayGuestType.SingleGuest, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            AddGuest(setup, AddStay(setup, theirs, BookingStatus.CheckedIn, Day), 0, "Loro", "Ospite", StayGuestType.SingleGuest, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // A stay of the other org that carries the id of my property.
            var foreign = AddStay(setup, theirs, BookingStatus.CheckedIn, Day);
            foreign.PropertyId = mine.Id;
            AddGuest(setup, foreign, 0, "Intruso", "Ospite", StayGuestType.SingleGuest, new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            await setup.SaveChangesAsync();
        }

        await using var asTheirOrg = NewDb(store, new FixedTenantContext(theirs.OrgId));
        await using var asMyOrg = NewDb(store, new FixedTenantContext(mine.OrgId));
        await using var asSystem = NewDb(store);

        var refusedFile = await Assert.ThrowsAsync<NotFoundException>(() => NewService(asTheirOrg).GenerateAsync(mine.Id, Day, Day, new GuestRegisterRequest(Anna)));
        var refusedInfo = await Assert.ThrowsAsync<NotFoundException>(() => NewService(asTheirOrg).GetInfoAsync(mine.Id, Day, Day));
        var served = await NewService(asMyOrg).GenerateAsync(mine.Id, Day, Day, new GuestRegisterRequest(Anna, "it"));
        var withNoFilter = await NewService(asSystem).GenerateAsync(mine.Id, Day, Day, new GuestRegisterRequest(Anna, "it"));

        Assert.Equal("property_not_found", refusedFile.Code);
        Assert.Equal("property_not_found", refusedInfo.Code);
        foreach (var register in new[] { served, withNoFilter })
        {
            var text = Encoding.UTF8.GetString(register.Content);
            Assert.Equal(1, register.Rows);
            Assert.Contains(";Mio;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Loro", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Intruso", text, StringComparison.Ordinal);
        }

        // The two registers that were made are audited under the org of the property; the refused ones left nothing.
        var events = await asSystem.SwissNotificationFileEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((mine.OrgId, mine.Id), (e.OrgId, e.PropertyId)));
    }

    // ─── The audit ───

    [Fact]
    public async Task GuestRegister_Audit_NoPersonalData_Test()
    {
        // PROOF TEST (privacy): the audit row of a register names the first day of the period, the format, the number of lines, the hash and the person who asked (the Auth0 subject):
        // never a name, a document, an address, a plate, a booking code or the name of the person who asked. The register itself holds all of them, so the check is not empty.
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        db.Users.Add(new User { Id = Anna, Email = "anna@example.com", FirstName = "Anna", LastName = "Rossi" });
        var stay = AddStay(db, property, BookingStatus.CheckedIn, Day, guests: 1, plate: "TI4455667");
        stay.BookingCode = "7Q3KZ8M2XB";
        db.StayGuests.Add(new StayGuest
        {
            BookingId = stay.Id,
            OrgId = property.OrgId,
            Position = 0,
            FirstName = "Zxqvgfhj",
            LastName = "Plmnbvcx",
            DateOfBirth = new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            CitizenshipCode = "100000241",
            CitizenshipName = "SVIZZERA",
            DocumentTypeCode = "PASOR",
            DocumentNumber = "QWERTY123456",
            ResidenceStreet = "Via Segretissima 99",
            ResidencePostalCode = "6900",
            ResidenceCityName = "Lugano",
        });
        await db.SaveChangesAsync();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 20, 9, 30, 15, 123, TimeSpan.Zero).AddTicks(4567));
        var service = NewService(db, time);

        var register = await service.GenerateAsync(property.Id, Day.AddDays(-3), Day, new GuestRegisterRequest(Anna, "it"));

        string[] personal =
        [
            "Zxqvgfhj", "Plmnbvcx", "QWERTY123456", "7Q3KZ8M2XB", BookingCodes.Format("7Q3KZ8M2XB"), "TI4455667", "Via Segretissima", "Lugano", "Anna Rossi", "anna@example.com", "04.01.1984",
        ];
        var text = Encoding.UTF8.GetString(register.Content);
        Assert.Contains("Zxqvgfhj", text, StringComparison.Ordinal);
        Assert.Contains("QWERTY123456", text, StringComparison.Ordinal);
        Assert.Contains("TI4455667", text, StringComparison.Ordinal);

        var row = Assert.Single(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
        var dump = string.Join(
            '|',
            typeof(SwissNotificationFileEvent).GetProperties().Select(p => Convert.ToString(p.GetValue(row), CultureInfo.InvariantCulture)));
        foreach (var value in personal)
            Assert.DoesNotContain(value, dump, StringComparison.OrdinalIgnoreCase);

        // What it does hold: the org and the property, the first day of the period (the way of the other formats: the day the file starts at), the format, the lines, the hash of the
        // bytes of the file (the file is never stored, so the hash is how it is recognized later), the person who asked and the instant.
        Assert.Equal((property.OrgId, property.Id, Day.AddDays(-3)), (row.OrgId, row.PropertyId, row.ArrivalDay));
        Assert.Equal(("register", 1, Anna), (row.Format, row.RowCount, row.CreatedByUserId));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(register.Content)).ToLowerInvariant(), row.Sha256);
        Assert.Equal(register.FileHash, row.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", row.Sha256);
        Assert.Equal(new DateTime(2026, 11, 20, 9, 30, 15, 123, DateTimeKind.Utc).AddTicks(4560), row.CreatedAt);
    }

    [Fact]
    public async Task Register_EveryFileIsOneAuditRow_TheInfoWritesNone_AndTheRowsAreTheCountsOfTheFile()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        AddGuest(db, AddStay(db, property, BookingStatus.CheckedIn, Day), 0, "Anna", "Test", StayGuestType.SingleGuest, new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc));
        AddGuest(db, AddStay(db, property, BookingStatus.CheckedIn, Day.AddDays(1)), 0, "Bruno", "Test", StayGuestType.SingleGuest, new DateTime(1985, 2, 5, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
        var service = NewService(db);

        await service.GetInfoAsync(property.Id, Day, Day);
        Assert.Empty(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());

        var first = await service.GenerateAsync(property.Id, Day, Day, new GuestRegisterRequest(Anna, "it"));
        var second = await service.GenerateAsync(property.Id, Day, Day.AddDays(2), new GuestRegisterRequest("auth0|bruno", "it"));
        var third = await service.GenerateAsync(property.Id, Day, Day);

        // Three files, three rows: each one with its own lines and its own hash (the same content, the same hash); the first day of the period is the day of the row.
        var rows = (await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync()).OrderBy(e => e.RowCount).ThenBy(e => e.CreatedByUserId).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, e => Assert.Equal(("register", Day), (e.Format, e.ArrivalDay)));
        Assert.Equal([1, 1, 2], rows.Select(e => e.RowCount));
        Assert.Equal([first.Rows, third.Rows, second.Rows], rows.Select(e => e.RowCount));
        Assert.Equal(first.FileHash, third.FileHash);
        Assert.NotEqual(first.FileHash, second.FileHash);
        Assert.Equal(new[] { first.FileHash, third.FileHash, second.FileHash }.Order(StringComparer.Ordinal), rows.Select(e => e.Sha256).Order(StringComparer.Ordinal));
        Assert.Contains(rows, e => e.CreatedByUserId == "auth0|bruno");
        Assert.Equal(2, rows.Count(e => e.CreatedByUserId == Anna || e.CreatedByUserId is null));

        // The day of a notification counts the CSV and the XML of the portal only: a register is not one.
        Assert.DoesNotContain(SwissNotificationFileFormats.Register, SwissNotificationFileFormats.PoliceFile);
    }

    [Fact]
    public async Task Register_AFileThatCannotBeMade_LeavesNoAuditRow()
    {
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        await db.SaveChangesAsync();
        var service = NewService(db);

        // An unknown property and two periods that are refused: each one answers an error, none leaves a row.
        await Assert.ThrowsAsync<NotFoundException>(() => service.GenerateAsync(Guid.NewGuid(), Day, Day));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateAsync(property.Id, Day, Day.AddDays(-1)));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.GenerateAsync(property.Id, Day, Day.AddDays(366)));

        Assert.Empty(await db.SwissNotificationFileEvents.AsNoTracking().ToListAsync());
    }

    // ─── The endpoint over the real service: what it answers and what it logs ───

    [Fact]
    public async Task Endpoint_AnswersTheFileWithItsHeaders_AndLogsNoPersonalData()
    {
        // The whole way, with the real service on the data: the file with its name, its type and the retention hint of the regime in X-Retention-Years, never cached; the info with its three
        // numbers; and nothing of the guests in what the endpoint or the service writes in the log (ids, dates, counts and the regime only).
        await using var db = NewDb();
        var property = await SeedPropertyAsync(db);
        var stay = AddStay(db, property, BookingStatus.CheckedIn, Day, guests: 1, plate: "TI4455667");
        stay.BookingCode = "7Q3KZ8M2XB";
        AddGuest(db, stay, 0, "Zxqvgfhj", "Plmnbvcx", StayGuestType.SingleGuest, new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc), document: "QWERTY123456");
        await db.SaveChangesAsync();
        var logs = new CapturingLogger<SwissComplianceController>();
        var controller = NewController(db, property.OrgId, logs);

        var info = await controller.GetGuestRegisterInfo(property.Id, new GuestRegisterQuery { From = Day, To = Day }, CancellationToken.None);
        var file = Assert.IsType<FileContentResult>(
            await controller.DownloadGuestRegister(property.Id, new GuestRegisterQuery { From = Day, To = Day, Format = "CSV" }, CancellationToken.None));

        var answer = Assert.IsType<GuestRegisterInfoResponse>(Assert.IsType<OkObjectResult>(info.Result).Value);
        Assert.Equal((property.Id, Day, Day, 1, (int?)5, "CH-TI"), (answer.PropertyId, answer.From, answer.To, answer.RowCount, answer.RetentionYears, answer.RegimeCode));
        Assert.Equal("registro-ospiti-ch-2026-11-19-2026-11-19.csv", file.FileDownloadName);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal("5", controller.Response.Headers[SwissComplianceController.RetentionYearsHeader].ToString());
        Assert.Equal("X-Retention-Years", SwissComplianceController.RetentionYearsHeader);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Contains("Zxqvgfhj", Encoding.UTF8.GetString(file.FileContents), StringComparison.Ordinal);

        Assert.NotEmpty(logs.Entries);
        var output = logs.AllOutput;
        foreach (var value in new[] { "Zxqvgfhj", "Plmnbvcx", "QWERTY123456", "7Q3KZ8M2XB", "7Q3KZ-8M2XB", "TI4455667", "04.01.1984", "Bahnhofstrasse" })
            Assert.DoesNotContain(value, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(property.Id.ToString(), output, StringComparison.Ordinal);
    }

    // ─── Helpers ───

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    private static AppDbContext NewDb(string? store = null, ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store ?? Guid.NewGuid().ToString()).Options, tenant);

    private static GuestRegisterService NewService(AppDbContext db, TimeProvider? clock = null)
    {
        var resolver = new GuestRegistrationRegimeResolver(db, new PropertySwissPlaceResolver(db), new TestFeatureFlags(FeatureFlags.GuestRegistrationRegimes));
        return new GuestRegisterService(db, resolver, new ResxDocumentTexts(), clock ?? Clock);
    }

    /// <summary>The lines of a file, without its byte order mark; the last one is empty because every line, the last too, ends with CRLF.</summary>
    private static string[] Lines(byte[] file)
    {
        Assert.True(file.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        return Encoding.UTF8.GetString(file, Encoding.UTF8.Preamble.Length, file.Length - Encoding.UTF8.Preamble.Length).Split("\r\n");
    }

    /// <summary>A property of a new org: in Switzerland, in the municipality of <paramref name="canton"/> (Lugano or Zurich) unless <paramref name="municipalityCode"/> says none.</summary>
    private static async Task<Property> SeedPropertyAsync(
        AppDbContext db, string country = "CH", string canton = "TI", string? municipalityCode = "-", string name = "TEST")
    {
        var code = municipalityCode == "-" ? (canton == "TI" ? "5192" : "261") : municipalityCode;
        if (code is not null && !await db.SwissMunicipalities.AnyAsync(m => m.MunicipalityCode == code))
            db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = code, CantonCode = canton, OfficialName = "TEST" });

        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = "test@example.com" };
        db.Orgs.Add(org);
        var property = new Property { OrgId = org.Id, Name = name, Country = country, SwissMunicipalityCode = code };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    /// <summary>A stay that arrives on <paramref name="arrival"/>, with its booker (Zxy Booker), and leaves <paramref name="nights"/> later.</summary>
    private static Booking AddStay(AppDbContext db, Property property, BookingStatus status, DateOnly arrival, int guests = 1, int nights = 2, string? plate = null)
    {
        var booker = new Guest { OrgId = property.OrgId, FirstName = "Booker", LastName = "Person" };
        db.Guests.Add(booker);
        var checkIn = DateTime.SpecifyKind(arrival.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = booker.Id,
            Status = status,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = guests,
            VehiclePlate = plate,
        };
        db.Bookings.Add(booking);
        return booking;
    }

    private static void AddGuest(AppDbContext db, Booking stay, int position, string first, string last, StayGuestType type, DateTime born, string? document = null) =>
        db.StayGuests.Add(new StayGuest
        {
            BookingId = stay.Id,
            OrgId = stay.OrgId,
            Position = position,
            Type = type,
            FirstName = first,
            LastName = last,
            DateOfBirth = born,
            CitizenshipCode = "100000241",
            CitizenshipName = "SVIZZERA",
            DocumentTypeCode = document is null ? null : "PASOR",
            DocumentNumber = document ?? string.Empty,
            ResidenceStreet = "Bahnhofstrasse 5",
            ResidencePostalCode = "8001",
            ResidenceCityName = "Zürich",
        });

    private SwissComplianceController NewController(AppDbContext db, Guid orgId, CapturingLogger<SwissComplianceController> logs)
    {
        var resources = new Mock<IHostResourceLookup>();
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new HostResource(orgId, "auth0|creator", id));
        var snapshot = new UserAuthorizationSnapshot(
            Exists: true,
            IsActive: true,
            Role: UserRole.None,
            SupplierOrgId: null,
            Memberships: [],
            OrgMember: new OrgMemberSnapshot(orgId, OrgRole.Admin, OrgMemberStatus.Active, PropertyScope.All, new HashSet<Guid>()));
        var authorization = HostAuthorizationTestHarness.Create(orgId, scopeResolver: HostAuthorizationTestHarness.ScopeResolver(snapshot));

        return new SwissComplianceController(
            Mock.Of<ITicinoNotificationService>(),
            Mock.Of<IOtrTaxService>(),
            Mock.Of<ISwissComplianceReceiptService>(),
            NewService(db),
            resources.Object,
            authorization,
            logs)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = HostAuthorizationTestHarness.User(Anna),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }
}
