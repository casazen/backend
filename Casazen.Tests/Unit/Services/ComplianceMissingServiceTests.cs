using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03: every item of the compliance cockpit says what it still lacks, with stable codes and fields: the blockers of the
/// activation wizard for a property, the fields of the Alloggiati record per stay with how many guests lack them, the single
/// thing left for the others. The cockpit itself (its sections, counts and items) is not changed.
/// </summary>
public class ComplianceMissingServiceTests
{
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Compliance:RequiredDocuments:default:0"] = "CinCertificate" })
        .Build();

    internal static ComplianceMissingService Service(AppDbContext db) => new(
        db,
        new PropertyComplianceStatusService(
            db,
            Config(),
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Options.Create(new ComplianceOptions()),
            NullLogger<PropertyComplianceStatusService>.Instance,
            Clock),
        new StayGuestService(db, new AlloggiatiCodeTableService(db, NullLogger<AlloggiatiCodeTableService>.Instance)));

    private static ComplianceSummarySection Section(params ComplianceSummaryItem[] items) => new(items.Length, items);

    private static ComplianceSummaryResult Summary(
        ComplianceSummarySection? properties = null,
        ComplianceSummarySection? checkIns = null,
        ComplianceSummarySection? checkOuts = null,
        ComplianceSummarySection? failures = null,
        ComplianceSummarySection? manual = null,
        ComplianceSummarySection? turnovers = null)
    {
        var empty = Section();
        return new ComplianceSummaryResult(
            properties ?? empty, checkIns ?? empty, checkOuts ?? empty, failures ?? empty, manual ?? empty, turnovers ?? empty);
    }

    private static ComplianceSummaryItem PropertyItem(Property property) =>
        ComplianceSummaryItem.ForProperty(ComplianceCockpitAction.ActivateProperty, property.Id, property.Name);

    private static ComplianceSummaryItem StayItem(ComplianceCockpitAction action, Booking booking) =>
        ComplianceSummaryItem.ForBooking(action, booking.Id, "Anna Verdi");

    // --- A property to activate -------------------------------------------------------------------------

    private static async Task<Property> SeedPropertyAsync(
        AppDbContext db, string name, bool compliant = false, string? cinCode = null)
    {
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|owner-sr03", name);
        property.ComplianceStatus = PropertyComplianceStatus.Pending;
        property.CinCode = compliant ? "IT058091C27G5FFZDZ" : cinCode;
        db.Properties.Add(property);
        if (compliant)
        {
            db.PropertyDocuments.Add(new PropertyDocument
            {
                PropertyId = property.Id,
                OrgId = orgId,
                FileName = "cin.pdf",
                StorageUrl = "documents/cin.pdf",
                DocumentType = DocumentType.CinCertificate,
                UploadedBy = "auth0|owner-sr03",
            });
            db.PropertySafetyChecklists.Add(SafetyChecklistTestData.CompleteAllElectric(property.Id, orgId));
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return property;
    }

    [Fact]
    public async Task Property_EveryBlockerOfTheWizard_IsListedWithItsStepAsTheField()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var property = await SeedPropertyAsync(db, "Casa Senza Carte");

        var described = await Service(db).DescribeAsync(Summary(properties: Section(PropertyItem(property))));

        var missing = Assert.Single(described.PropertiesPending.Items).Missing;
        Assert.Contains(new ComplianceMissing("activation_cin_missing", "cin"), missing);
        Assert.Contains(new ComplianceMissing("activation_documents_missing", "documents"), missing);
        Assert.Contains(missing, m => m.Field == "safety");
        // The base data are complete: nothing about that step.
        Assert.DoesNotContain(missing, m => m.Field == "base-data");
        Assert.All(missing, m => Assert.False(string.IsNullOrWhiteSpace(m.Code)));
    }

    [Fact]
    public async Task Property_WithEveryRequirementMet_OnlyTheConfirmationOfTheActivationIsLeft()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var property = await SeedPropertyAsync(db, "Casa In Regola", compliant: true);

        var described = await Service(db).DescribeAsync(Summary(properties: Section(PropertyItem(property))));

        Assert.Equal(
            [new ComplianceMissing(ComplianceMissingCodes.ActivationNotConfirmed)],
            Assert.Single(described.PropertiesPending.Items).Missing);
    }

    [Fact]
    public async Task Property_OnlyTheFirstTenAreDetailed_TheOthersAreNeverEmpty()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var properties = new List<Property>();
        for (var i = 0; i < IComplianceMissingService.MaxDetailedProperties + 2; i++)
            properties.Add(await SeedPropertyAsync(db, $"Casa {i:00}"));

        var described = await Service(db).DescribeAsync(Summary(properties: Section(properties.Select(PropertyItem).ToArray())));

        var items = described.PropertiesPending.Items;
        Assert.Equal(properties.Select(p => p.Id), items.Select(i => i.Id));
        Assert.All(items.Take(IComplianceMissingService.MaxDetailedProperties), item =>
            Assert.Contains(new ComplianceMissing("activation_cin_missing", "cin"), item.Missing));
        Assert.All(items.Skip(IComplianceMissingService.MaxDetailedProperties), item =>
            Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.ActivationIncomplete)], item.Missing));
    }

    // --- The guests of a stay ---------------------------------------------------------------------------

    private static async Task<Booking> SeedStayAsync(AppDbContext db, params StayGuest[] guests)
    {
        var orgId = Guid.NewGuid();
        var property = HostScopeScenario.NewProperty(orgId, "auth0|owner-sr03", "Trullo");
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            CheckInDate = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = guests.Length,
        };
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        for (var i = 0; i < guests.Length; i++)
        {
            guests[i].BookingId = booking.Id;
            guests[i].OrgId = orgId;
            guests[i].Position = i;
            db.StayGuests.Add(guests[i]);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return booking;
    }

    private static StayGuest Complete(StayGuestType type = StayGuestType.SingleGuest) => new()
    {
        Type = type,
        FirstName = "Mario",
        LastName = "Rossi",
        Gender = Gender.Male,
        DateOfBirth = new DateTime(1980, 4, 2, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Milano",
        BirthProvince = "MI",
        CitizenshipName = "Italia",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "CA12345AB",
        DocumentIssuePlaceName = "Milano",
    };

    [Fact]
    public async Task Stay_TheFieldsTheGuestsLack_ComeWithHowManyGuestsLackThem()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var withoutNumber = Complete();
        withoutNumber.DocumentNumber = string.Empty;
        var withoutNumberAndGender = Complete();
        withoutNumberAndGender.DocumentNumber = string.Empty;
        withoutNumberAndGender.Gender = null;
        var booking = await SeedStayAsync(db, withoutNumber, withoutNumberAndGender);

        var described = await Service(db).DescribeAsync(
            Summary(checkIns: Section(StayItem(ComplianceCockpitAction.CompleteGuestCheckIn, booking))));

        var missing = Assert.Single(described.GuestCheckInsIncomplete.Items).Missing;
        Assert.Equal(
            [
                new ComplianceMissing(ComplianceMissingCodes.GuestFieldMissing, "documentNumber", 2),
                new ComplianceMissing(ComplianceMissingCodes.GuestFieldMissing, "gender", 1),
            ],
            missing);
    }

    [Fact]
    public async Task Stay_AHeadWithoutMembers_IsACompositionErrorOfTheGuests()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var booking = await SeedStayAsync(db, Complete(StayGuestType.HeadOfFamily));

        var described = await Service(db).DescribeAsync(
            Summary(checkIns: Section(StayItem(ComplianceCockpitAction.CompleteGuestCheckIn, booking))));

        // The data of the one guest are complete: the order of the guests is what the record does not accept.
        Assert.Equal(
            [new ComplianceMissing(ComplianceMissingCodes.GuestCompositionInvalid, "guests")],
            Assert.Single(described.GuestCheckInsIncomplete.Items).Missing);
    }

    [Fact]
    public async Task Stay_NothingLacksAnymore_OrTheStayIsGone_ItemIsNeverLeftEmpty()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var complete = await SeedStayAsync(db, Complete());
        var gone = StayItem(ComplianceCockpitAction.CompleteGuestCheckIn, new Booking { Id = Guid.NewGuid() });

        var described = await Service(db).DescribeAsync(
            Summary(checkIns: Section(StayItem(ComplianceCockpitAction.CompleteGuestCheckIn, complete), gone)));

        Assert.All(
            described.GuestCheckInsIncomplete.Items,
            item => Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.GuestDataIncomplete)], item.Missing));
    }

    // --- The other sections -----------------------------------------------------------------------------

    [Fact]
    public async Task OtherSections_HaveTheSingleThingLeft_AndTheCockpitIsNotChanged()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var booking = new Booking { Id = Guid.NewGuid() };
        var summary = Summary(
            checkOuts: Section(StayItem(ComplianceCockpitAction.CheckOut, booking)),
            failures: Section(StayItem(ComplianceCockpitAction.ResolveAlloggiatiFailure, booking)),
            manual: new ComplianceSummarySection(25, [StayItem(ComplianceCockpitAction.SendAlloggiati, booking)]),
            turnovers: Section(StayItem(ComplianceCockpitAction.ConfirmPropertyReady, booking)));

        var described = await Service(db).DescribeAsync(summary);

        Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.CheckoutNotClosed, "checkOut")], described.CheckoutsDue.Items.Single().Missing);
        Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.AlloggiatiFailed, "alloggiati")], described.AlloggiatiFailures.Items.Single().Missing);
        Assert.Equal([new ComplianceMissing(ComplianceMissingCodes.AlloggiatiNotSent, "alloggiati")], described.AlloggiatiManualRequired.Items.Single().Missing);
        Assert.Equal(
            [new ComplianceMissing(ComplianceMissingCodes.PropertyReadyNotConfirmed, "propertyReady")],
            described.TurnoversPending.Items.Single().Missing);
        // The counts (a section lists its latest items only) and the items themselves are as they were.
        Assert.Equal(25, described.AlloggiatiManualRequired.Count);
        Assert.Equal(summary.CheckoutsDue.Items.Select(i => (i.Id, i.Label, i.Action)), described.CheckoutsDue.Items.Select(i => (i.Id, i.Label, i.Action)));
    }

    [Fact]
    public async Task ARealCockpit_EveryItemOfEverySection_SaysWhatItLacks_WithinTheScope()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var cockpit = ComplianceWizardServiceTests.CreateService(db, Clock);
        var service = Service(db);

        foreach (var scope in new[] { world.OrgWide, world.Restricted })
        {
            var plain = await cockpit.GetSummaryAsync(scope);
            var described = await service.DescribeAsync(plain);

            var sections = new[]
            {
                (plain.PropertiesPending, described.PropertiesPending),
                (plain.GuestCheckInsIncomplete, described.GuestCheckInsIncomplete),
                (plain.CheckoutsDue, described.CheckoutsDue),
                (plain.AlloggiatiFailures, described.AlloggiatiFailures),
                (plain.AlloggiatiManualRequired, described.AlloggiatiManualRequired),
                (plain.TurnoversPending, described.TurnoversPending),
            };
            foreach (var (before, after) in sections)
            {
                Assert.Equal(before.Count, after.Count);
                Assert.Equal(before.Items.Select(i => i.Id), after.Items.Select(i => i.Id));
                Assert.All(after.Items, item => Assert.NotEmpty(item.Missing));
                Assert.All(before.Items, item => Assert.Empty(item.Missing));
            }
        }

        // The scenario has one stay to complete and one turnover per property: the collaborator's cockpit is the half of the owner's.
        var all = await service.DescribeAsync(await cockpit.GetSummaryAsync(world.OrgWide));
        var granted = await service.DescribeAsync(await cockpit.GetSummaryAsync(world.Restricted));
        Assert.Equal(2, all.TurnoversPending.Count);
        Assert.Equal(1, granted.TurnoversPending.Count);
        Assert.Equal(2, all.PropertiesPending.Count);
        Assert.Equal(1, granted.PropertiesPending.Count);
    }
}
