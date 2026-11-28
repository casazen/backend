using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

/// <summary>
/// casazen/backend#979, the demo rule (<c>.claude/rules/demo-seed.md</c>): the Swiss flat of the showcase (<see cref="DemoShowcaseCatalog.Scenario.SwissGuestRegistration"/>) is written by the real
/// seeding code and is valid for the real rules. It is a flat of country CH with the postal code and the city and no stored municipality, so the one resolver derives canton Ticino from them; it
/// is activated by the real status of a property (the structure number is the TEST one the file checks) and has none of what is Italian (no ISTAT comune, CIN, regional document, safety
/// checklist); its confirmed stay of yesterday has two adults with the data of the Swiss file and the cockpit lists the day to declare; no image of a document exists (decision D6); every row is
/// marked as showcase data. The seeding of the other flats is untouched: the two flats of Ticino of the agency scenario stay Italian-model properties.
/// </summary>
public class DemoSwissGuestRegistrationSeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    private static DemoPropertyTemplate Template => DemoShowcaseCatalog.Properties.Single(p => p.Key == DemoShowcaseCatalog.SwissFlatKey);

    [Fact]
    public async Task TheSwissFlat_IsWrittenAsTheProductStoresASwissFlat_AndTheRealActivationAcceptsIt()
    {
        var world = await SeedAsync();

        var flat = await world.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.FlatId);

        Assert.Equal(("CH", "CHF", "Europe/Zurich", "6900", "Lugano"), (flat.Country, flat.Currency, flat.Timezone, flat.PostalCode, flat.City));
        Assert.True(SwissPlace.IsSwissCountry(flat.Country));
        Assert.Null(flat.SwissMunicipalityCode);
        Assert.Equal(RentalMode.Short, flat.RentalMode);
        Assert.True(flat.IsTestData);

        Assert.Equal(DemoShowcaseCatalog.SwissFlatStructureId, flat.TicinoStructureId);
        Assert.True(TicinoStructureId.IsValid(flat.TicinoStructureId));
        Assert.Contains("TEST", flat.TicinoStructureId, StringComparison.Ordinal);

        Assert.Equal((PropertyComplianceStatus.Active, true), (flat.ComplianceStatus, flat.IsActive));
    }

    [Fact]
    public async Task TheSwissFlat_HasNoneOfTheItalianData_NoComuneNoCinNoRegionalDocumentNoSafetyChecklist()
    {
        var world = await SeedAsync();

        var flat = await world.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.FlatId);

        Assert.Null(flat.ComuneIstatCode);
        Assert.Null(flat.RegionCode);
        Assert.Null(flat.CinCode);
        Assert.Null(flat.CadastralSheet);
        Assert.Empty(await world.Db.PropertyDocuments.IgnoreQueryFilters().Where(d => d.PropertyId == world.FlatId).ToListAsync());
        Assert.Empty(await world.Db.PropertySafetyChecklists.IgnoreQueryFilters().Where(c => c.PropertyId == world.FlatId).ToListAsync());
        Assert.Empty(world.StoredFiles);
        Assert.Empty(await world.Db.AlloggiatiWebReports.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task TheSwissFlat_StaysAsItIs_WhenTheTestComplianceIsAppliedToTheOrg()
    {
        var world = await SeedAsync();
        world.Db.DemoEnvironmentSettings.Add(new DemoEnvironmentSetting { Id = DemoEnvironmentSetting.SingletonId, Enabled = true, TemplateVersion = DemoShowcaseCatalog.Version });
        await world.Db.SaveChangesAsync();

        // «Completa conformità di test» fills every short-stay flat of the org: the name of Lugano is in the list of the invented comuni of the agency scenario (999999, TIC), and a Swiss flat is
        // never given an Italian comune, a CIN or a regional document.
        var result = await world.Seeding.ApplyTestComplianceAsync(world.Org.Id);

        var flat = await world.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.FlatId);
        Assert.Equal(1, result.PropertiesSeen);
        Assert.Null(flat.ComuneIstatCode);
        Assert.Null(flat.RegionCode);
        Assert.Null(flat.CinCode);
        Assert.Empty(await world.Db.PropertyDocuments.IgnoreQueryFilters().Where(d => d.PropertyId == world.FlatId).ToListAsync());
        Assert.Empty(await world.Db.PropertySafetyChecklists.IgnoreQueryFilters().Where(c => c.PropertyId == world.FlatId).ToListAsync());
    }

    [Fact]
    public async Task TheResolver_DerivesCantonTicinoFromThePostalCodeAndTheCity_NothingIsStored()
    {
        var world = await SeedAsync();
        var flat = await world.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.FlatId);

        var place = await new PropertySwissPlaceResolver(world.Db).ResolveAsync(flat);

        Assert.NotNull(place);
        Assert.Equal(("5192", "TI", SwissPlaceSource.Derived, true), (place.MunicipalityCode, place.CantonCode, place.Source, place.IsInTicino));
        Assert.False(place.Ambiguous);
        Assert.True(await new TicinoNotificationService(world.Db, world.Clock).IsPropertyInTicinoAsync(world.FlatId));
        Assert.Null((await world.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.FlatId)).SwissMunicipalityCode);
    }

    [Fact]
    public async Task TheStayOfYesterday_IsConfirmed_WithTwoAdultsAndTheDocumentOfEach_AndTheCockpitListsTheDayToDeclare()
    {
        var world = await SeedAsync();
        var today = world.Clock.TodayInRomeAsDateOnly();
        var stay = await world.Db.Bookings.AsNoTracking().SingleAsync(b => b.PropertyId == world.FlatId);

        Assert.Equal((BookingStatus.Confirmed, BookingSource.Direct), (stay.Status, stay.Source));
        Assert.Equal(today.AddDays(-1), DateOnly.FromDateTime(stay.CheckInDate));
        Assert.Equal((2, 2, 0), (stay.NumberOfGuests, stay.NumberOfAdults, stay.NumberOfChildren));
        var booker = await world.Db.Guests.AsNoTracking().SingleAsync(g => g.Id == stay.GuestId);
        Assert.EndsWith("@example.com", booker.Email, StringComparison.Ordinal);
        Assert.Equal(("Zürich", "8001", "Svizzera"), (booker.City, booker.PostalCode, booker.Country));

        var guests = await world.Db.StayGuests.AsNoTracking().Where(g => g.BookingId == stay.Id).OrderBy(g => g.Position).ToListAsync();
        Assert.Equal([StayGuestType.HeadOfFamily, StayGuestType.FamilyMember], guests.Select(g => g.Type));
        Assert.All(guests, g =>
        {
            Assert.False(g.BornInItaly);
            Assert.False(g.ResidenceInItaly);
            Assert.Equal(("Bahnhofstrasse 12", "8001", "Zürich", "Svizzera"), (g.ResidenceStreet, g.ResidencePostalCode, g.ResidenceCityName, g.ResidenceCountryName));
            Assert.Contains("TEST", g.DocumentNumber, StringComparison.Ordinal);
            Assert.True(g.DocumentExpiresOn > today);
        });
        Assert.Equal((GuestDocumentType.IdentityCard, "Svizzera"), (guests[0].DocumentType, guests[0].CitizenshipName));
        Assert.Equal((GuestDocumentType.Passport, "Germania"), (guests[1].DocumentType, guests[1].CitizenshipName));

        var days = await new TicinoNotificationService(world.Db, world.Clock).ListPendingDaysAsync(world.Org.Id, null, today);
        var day = Assert.Single(days);
        Assert.Equal((world.FlatId, today.AddDays(-1), 1), (day.PropertyId, day.Day, day.StayCount));
    }

    [Fact]
    public async Task TheFileOfTheDay_HasTheTwoAdultsWithTheirDocumentsAndTheirSwissHome_AndNothingMissing()
    {
        var world = await SeedAsync();
        var today = world.Clock.TodayInRomeAsDateOnly();

        var detail = await new TicinoNotificationService(world.Db, world.Clock).GetDayAsync(world.FlatId, today.AddDays(-1));

        Assert.Empty(detail.Plan.Issues);
        Assert.Equal(DemoShowcaseCatalog.SwissFlatStructureId, detail.StructureId);
        Assert.Equal(2, detail.Plan.Registrations.Count);
        var head = detail.Plan.Registrations.Single(r => r.Position == 0);
        var member = detail.Plan.Registrations.Single(r => r.Position == 1);
        Assert.Equal((TicinoDocumentKind.IdentityCard, "TEST-CH-ID-0001", "CHE", "CHE"), (head.DocumentKind, head.DocumentNumber, head.Nationality, head.Nation));
        Assert.Equal((TicinoDocumentKind.Passport, "TEST-DE-PASS-0002", "DEU", "CHE"), (member.DocumentKind, member.DocumentNumber, member.Nationality, member.Nation));
        Assert.All(detail.Plan.Registrations, r => Assert.Equal(("Bahnhofstrasse 12", "8001", "Zürich", DemoShowcaseCatalog.SwissFlatStructureId), (r.Street, r.Zip, r.City, r.UserId)));
    }

    [Fact]
    public async Task NoImageOfADocumentIsKept_ForTheSwissFlat()
    {
        var world = await SeedAsync();

        Assert.Empty(await world.Db.GuestCheckInSessions.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await world.Db.BiometricVerificationLogs.IgnoreQueryFilters().ToListAsync());
        Assert.DoesNotContain(DemoShowcaseCatalog.CheckIns, c => c.PropertyKey == DemoShowcaseCatalog.SwissFlatKey);
        Assert.Empty(world.StoredFiles);
        Assert.All(await world.Db.Guests.AsNoTracking().ToListAsync(), g => Assert.Null(g.DocumentScanUrl));
    }

    [Fact]
    public async Task EveryRowTheSeedingWrote_IsMarkedAsShowcaseData_SoTheResetFindsItAndDeletesNothingElse()
    {
        var world = await SeedAsync();
        var stay = await world.Db.Bookings.AsNoTracking().SingleAsync(b => b.PropertyId == world.FlatId);
        var stayGuests = await world.Db.StayGuests.AsNoTracking().Where(g => g.BookingId == stay.Id).ToListAsync();

        var markers = await world.Db.TestDataMarkers.AsNoTracking().Where(m => m.OrgId == world.Org.Id).ToListAsync();

        Assert.All(markers, m => Assert.Equal(TestDataOrigins.Showcase, m.Origin));
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.Property && m.EntityId == world.FlatId && m.TemplateKey == DemoShowcaseCatalog.SwissFlatKey);
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.Booking && m.EntityId == stay.Id);
        Assert.Contains(markers, m => m.EntityType == TestDataEntities.Guest && m.EntityId == stay.GuestId);
        Assert.Equal(
            stayGuests.Select(g => g.Id).Order(),
            markers.Where(m => m.EntityType == TestDataEntities.StayGuest).Select(m => m.EntityId).Order());
    }

    private static async Task<SeedWorld> SeedAsync()
    {
        var clock = new FakeTimeProvider(Now);
        var db = OrgTeamTestData.NewDb();
        var org = OrgTeamTestData.AddOrg(db);
        var owner = OrgTeamTestData.AddUser(db, $"auth0|titolare-{Guid.NewGuid():N}"[..24], org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddMember(db, owner.Id, org.Id, OrgRole.Owner);

        var directory = SwissMunicipalitySeed.Parse(
            SwissMunicipalitySeed.ReadEmbedded() ?? throw new InvalidOperationException("The seed of the Swiss places is not part of this build."));
        db.SwissMunicipalities.AddRange(directory.Municipalities.Where(m => m.CantonCode == SwissPlace.TicinoCantonCode));
        db.SwissLocalities.AddRange(directory.Localities.Where(l => l.CantonCode == SwissPlace.TicinoCantonCode));
        await db.SaveChangesAsync();

        var files = new Dictionary<string, byte[]>();
        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((StorageBucket _, string key, Stream content, string _, CancellationToken _) =>
            {
                using var copy = new MemoryStream();
                content.CopyTo(copy);
                files[key] = copy.ToArray();
            })
            .Returns(Task.CompletedTask);
        var gate = new Mock<IDemoEnvironmentGate>();
        gate.SetupGet(g => g.IsAvailable).Returns(true);
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(h => h.EnvironmentName).Returns("Staging");

        var compliance = new PropertyComplianceStatusService(
            db,
            new ConfigurationBuilder().Build(),
            new RecordingEmailQueue(),
            EmailTestHelpers.Links(),
            Options.Create(new ComplianceOptions()),
            new ActivityLog(db),
            NullLogger<PropertyComplianceStatusService>.Instance,
            clock);
        var seeding = new DemoEnvironmentService(
            db,
            storage.Object,
            compliance,
            Options.Create(new ComplianceOptions()),
            gate.Object,
            NullLogger<DemoEnvironmentService>.Instance,
            clock,
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ICrmContactIndex>(),
            Mock.Of<IFeatureFlags>(),
            hostEnvironment.Object);

        await seeding.CreateShowcasePropertyAsync(org.Id, owner.Id, Template, CancellationToken.None);
        var flatId = (await db.Properties.AsNoTracking().SingleAsync()).Id;
        return new SeedWorld(db, org, seeding, clock, files, flatId);
    }

    private sealed record SeedWorld(
        AppDbContext Db,
        OrgEntity Org,
        DemoEnvironmentService Seeding,
        FakeTimeProvider Clock,
        IReadOnlyDictionary<string, byte[]> StoredFiles,
        Guid FlatId);
}
