using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Casazen.Web.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Casazen.Tests.Integration.Acceptance;

/// <summary>
/// The Ticino pilot of the acceptance scenario (E30/F03/US01/T01, NF-133, casazen/backend#979): an agency of Lugano with a flat whose postal code and city are the only things
/// that say where it is (6900 «Lugano», no municipality stored), a second agency of Locarno that must see nothing of it, and a family of three from Zurich (two adults and a girl
/// of nine) that books the flat, completes the online check-in and arrives. The services are the real ones over one database (in memory, or PostgreSQL for the twin of the main
/// path); only the e-mail transport, the push queue, the storage of the receipts and the audience of the alerts are stand-ins that record what they are given. The clock is the one of
/// the world and starts on <see cref="TicinoStartDay"/>, the one fixed date of the pilot: every other date is relative to it.
/// </summary>
internal sealed partial class AcceptanceScenarioBuilder
{
    /// <summary>The «Numero identificativo» of the flat of Lugano as the portal prints it (the host types it in lower case, with spaces around it).</summary>
    public const string TicinoStructureNumber = "NL-12345678";

    /// <summary>The code of the booking of the pilot as it is stored; hosts and the file read it as <c>QK519-2AB7M</c>.</summary>
    public const string TicinoBookingCode = "QK5192AB7M";

    /// <summary>The address of the agency of Lugano: the one the alerts of its arrival days go to.</summary>
    public const string TicinoAgencyEmail = "agenzia.quokka.lugano@example.com";

    /// <summary>The name of the person of the agency of Lugano who declares the days (what the cockpit shows as «declared by»).</summary>
    public const string TicinoHostName = "TEST Gestore";

    /// <summary>The second agency: its address, its flat in Locarno and its structure number.</summary>
    public const string OtherAgencyEmail = "agenzia.locarno@example.com";

    public const string OtherPropertyName = "TEST Casa Locarno";

    public const string OtherStructureNumber = "NL-87654321";

    /// <summary>
    /// The first day of the pilot, a Tuesday of the past in winter time: the clock of the world starts there at 09:00 in Lugano and every other date of the scenario is relative to it
    /// (the booking a day later, the arrival nine days after, the retention five years after the arrival).
    /// </summary>
    public static readonly DateOnly TicinoStartDay = new(2026, 11, 10);

    /// <summary>The day the family arrives: nine days after the start.</summary>
    public static DateOnly TicinoArrivalDay => TicinoStartDay.AddDays(9);

    /// <summary>The second agency (Locarno): its organization and its flat.</summary>
    public Guid OtherOrgId { get; } = Guid.NewGuid();

    public Guid OtherPropertyId { get; } = Guid.NewGuid();

    /// <summary>The push queue: what the alerts and the declarations tell the hosts (the bell, the pop-up and the open pages are made from it by the decorator of the application).</summary>
    public RecordingPushQueue Pushes { get; } = new();

    /// <summary>Every line the services of the pilot wrote to their logs.</summary>
    public ScenarioLog Log { get; } = new();

    private readonly Dictionary<Guid, ComplianceAlertRecipients> _audiences = [];

    /// <summary>
    /// The Ticino pilot on a database of its own (in memory unless <paramref name="options"/> says otherwise): the Ticino part of the official directory of the Swiss places, the agency of Lugano with
    /// its host, the second agency with its flat in Locarno. The flat of Lugano is NOT there yet: <see cref="CreateTheFlatInLuganoAsync"/> is the first step of the story.
    /// </summary>
    public static async Task<AcceptanceScenarioBuilder> CreateTicinoAsync(DbContextOptions<AppDbContext>? options = null)
    {
        var world = new AcceptanceScenarioBuilder(
            options ?? new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"AcceptanceTicino_{Guid.NewGuid():N}").Options);
        world.At(InRome(TicinoStartDay, 9));
        await world.SetUpTicinoAsync();
        return world;
    }

    /// <summary>A unit of work of the agency of Lugano (a request, a job) with the services of the Ticino chain over a context of its own: no tenant filter, as a job.</summary>
    public TicinoScope OpenTicinoScope() => new(this, new AppDbContext(_options, NullTenantContext.Instance, Keys));

    /// <summary>A unit of work of a person of the organization <paramref name="orgId"/>: the tenant filter is on and scoped to its org, as in every authenticated request.</summary>
    public TicinoScope OpenTicinoScopeOf(Guid orgId) => new(this, new AppDbContext(_options, new OrgRequestTenant(orgId), Keys));

    /// <summary>Who is told about an alert of a property of <paramref name="orgId"/>: the address of the org and the person in charge (the rule that picks them is covered by its own tests).</summary>
    internal ComplianceAlertRecipients AudienceOf(Guid orgId) => _audiences.GetValueOrDefault(orgId) ?? ComplianceAlertRecipients.None;

    private async Task SetUpTicinoAsync()
    {
        await using var scope = OpenTicinoScope();
        var db = scope.Db;

        // The official directory of swisstopo, the file the application loads at startup, in its Ticino part: the municipalities and the localities of the canton with their official numbers and
        // postal codes (the whole country is some 5,700 rows, and this story never leaves Ticino).
        var directory = SwissMunicipalitySeed.Parse(
            SwissMunicipalitySeed.ReadEmbedded() ?? throw new InvalidOperationException("The seed of the Swiss places is not part of this build."));
        db.SwissMunicipalities.AddRange(directory.Municipalities.Where(m => m.CantonCode == SwissPlace.TicinoCantonCode));
        db.SwissLocalities.AddRange(directory.Localities.Where(l => l.CantonCode == SwissPlace.TicinoCantonCode));


        var now = Clock.GetUtcNow().UtcDateTime;
        db.Orgs.Add(new OrgEntity
        {
            Id = OrgId,
            Name = "TEST Quokka Lugano",
            Slug = $"quokka-lugano-{OrgId:N}"[..28],
            DisplayName = "TEST Quokka Lugano",
            ContactEmail = TicinoAgencyEmail,
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Agency,
            DefaultLanguage = "it",
        });
        db.Orgs.Add(new OrgEntity
        {
            Id = OtherOrgId,
            Name = "TEST Agenzia Locarno",
            Slug = $"agenzia-locarno-{OtherOrgId:N}"[..28],
            DisplayName = "TEST Agenzia Locarno",
            ContactEmail = OtherAgencyEmail,
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Agency,
            DefaultLanguage = "it",
        });
        db.Users.Add(new User
        {
            Id = AgencyUser,
            Email = "gestore.lugano@example.com",
            FirstName = "TEST",
            LastName = "Gestore",
            Role = UserRole.PropertyOwner,
            OrgId = OrgId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // The flat of the other agency: in Ticino too (6600 «Locarno», no municipality stored), already active, with its own structure number and no stay.
        db.Properties.Add(new Property
        {
            Id = OtherPropertyId,
            OrgId = OtherOrgId,
            OwnerId = "auth0|test-locarno-agency",
            Name = OtherPropertyName,
            Address = "Via della Pace 3",
            City = "Locarno",
            PostalCode = "6600",
            Country = "CH",
            Currency = "CHF",
            Timezone = "Europe/Zurich",
            TicinoStructureId = OtherStructureNumber,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 150m,
            ComplianceStatus = PropertyComplianceStatus.Active,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        _audiences[OrgId] = new ComplianceAlertRecipients([TicinoAgencyEmail], [AgencyUser]);
        _audiences[OtherOrgId] = new ComplianceAlertRecipients([OtherAgencyEmail], ["auth0|test-locarno-agency"]);
    }

    // ---------------------------------------------------------------- the steps the pilot shares

    /// <summary>
    /// The flat of Quokka in Lugano as the host creates it. The create request has no country and no municipality (<see cref="CreatePropertyRequest"/>): a Swiss flat comes in the way the
    /// import of the channel manager makes it, with the country CH, the postal code and the city and no municipality stored; the property service gives it the franc and the clock of
    /// Zurich. It is pending and has no structure number.
    /// </summary>
    public async Task CreateTheFlatInLuganoAsync()
    {
        var request = new CreatePropertyRequest
        {
            Name = PropertyName,
            Address = "Riva Paradiso 1",
            City = "Lugano",
            PostalCode = "6900",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 180m,
        };
        var property = request.ToProperty(AgencyUser);
        property.Id = PropertyId;
        property.OrgId = OrgId;
        property.Country = "CH";

        await using var scope = OpenTicinoScope();
        await scope.Properties.CreatePropertyAsync(property);
    }

    /// <summary>The host sets the structure number in the form of the property: the update request is applied to the property and saved by the property service, which re-evaluates its compliance.</summary>
    public async Task SetTheStructureNumberAsync(string typed)
    {
        await using var scope = OpenTicinoScope();
        var property = await scope.Properties.GetPropertyRecordAsync(PropertyId)
            ?? throw new InvalidOperationException("The flat of Lugano does not exist yet.");
        new UpdatePropertyRequest { TicinoStructureId = typed }.ApplyTo(property);
        await scope.Properties.UpdatePropertyAsync(property);
    }

    /// <summary>
    /// The booking of the family of Zurich as the booking site leaves it: confirmed, two adults and a child, the code of the booking, the arrival time the guest said (15:00). The booker is Hans Muster.
    /// </summary>
    public async Task<Guid> BookTheFamilyOfZurichAsync(DateOnly arrival, DateOnly departure)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        var booker = new Guest
        {
            OrgId = OrgId,
            FirstName = "Hans",
            LastName = "Muster",
            Email = $"hans.muster@{GuestMailDomain}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = PropertyId,
            GuestId = booker.Id,
            CheckInDate = StoredDay(arrival),
            CheckOutDate = StoredDay(departure),
            NumberOfGuests = 3,
            NumberOfAdults = 2,
            NumberOfChildren = 1,
            Status = BookingStatus.Confirmed,
            ConfirmedAt = now,
            Source = BookingSource.Direct,
            BookingCode = TicinoBookingCode,
            ExpectedArrivalTime = "15:00",
            BasePrice = 540m,
            TotalPrice = 540m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var scope = OpenTicinoScope();
        scope.Db.Guests.Add(booker);
        scope.Db.Bookings.Add(booking);
        await scope.Db.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>One hourly run of the <c>stay-alerts</c> job at the instant of the clock: how many alerts it sent.</summary>
    public async Task<int> RunTheHourlyAlertsAsync()
    {
        await using var scope = OpenTicinoScope();
        return (await scope.Alerts.RunAsync()).Sent;
    }

    /// <summary>One night of the retention job of the Swiss documents at the instant of the clock.</summary>
    public async Task RunTheRetentionAsync()
    {
        await using var scope = OpenTicinoScope();
        await scope.Retention.ExecuteAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- the family of Zurich

    /// <summary>A calendar day as the stays and the receipts store it: midnight UTC of that day.</summary>
    public static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>The birth dates of the family, relative to the day they arrive: the head is 41, the second adult 39, the girl 9 (under 14: she is counted in «Min14» on the row of the head).</summary>
    public static DateOnly BirthOfTheHead(DateOnly arrival) => arrival.AddYears(-41).AddDays(-6);

    public static DateOnly BirthOfTheSecondAdult(DateOnly arrival) => arrival.AddYears(-39).AddDays(-40);

    public static DateOnly BirthOfTheGirl(DateOnly arrival) => arrival.AddYears(-9).AddDays(-100);

    /// <summary>
    /// What the guests type on the check-in page of a flat in Ticino: the head of family (a passport) and his wife (an identity card), both born abroad and living at the same address in
    /// Zurich, and their daughter, who has no document. With <paramref name="documentOfTheSecondAdult"/> false the wife leaves her document empty.
    /// </summary>
    public static IReadOnlyList<StayGuestInput> TheFamilyOfZurich(DateOnly arrival, bool documentOfTheSecondAdult = true) =>
    [
        new StayGuestInput
        {
            Type = "HeadOfFamily",
            FirstName = "Hans",
            LastName = "Muster",
            Gender = Gender.Male,
            DateOfBirth = StoredDay(BirthOfTheHead(arrival)),
            BornInItaly = false,
            BirthCountryName = "Svizzera",
            CitizenshipName = "Svizzera",
            DocumentType = "Passport",
            DocumentNumber = "X1234567",
            DocumentIssuePlaceName = "Zürich",
            ResidenceInItaly = false,
            ResidenceCountryName = "Svizzera",
            ResidenceStreet = "Bahnhofstrasse 12",
            ResidencePostalCode = "8001",
            ResidenceCityName = "Zürich",
        },
        new StayGuestInput
        {
            Type = "FamilyMember",
            FirstName = "Erika",
            LastName = "Muster",
            Gender = Gender.Female,
            DateOfBirth = StoredDay(BirthOfTheSecondAdult(arrival)),
            BornInItaly = false,
            BirthCountryName = "Germania",
            CitizenshipName = "Svizzera",
            DocumentType = documentOfTheSecondAdult ? "IdentityCard" : null,
            DocumentNumber = documentOfTheSecondAdult ? "S1234567" : null,
            ResidenceInItaly = false,
            ResidenceCountryName = "Svizzera",
            ResidenceStreet = "Bahnhofstrasse 12",
            ResidencePostalCode = "8001",
            ResidenceCityName = "Zürich",
        },
        new StayGuestInput
        {
            Type = "FamilyMember",
            FirstName = "Lea",
            LastName = "Muster",
            Gender = Gender.Female,
            DateOfBirth = StoredDay(BirthOfTheGirl(arrival)),
            BornInItaly = false,
            BirthCountryName = "Svizzera",
            CitizenshipName = "Svizzera",
            ResidenceInItaly = false,
            ResidenceCountryName = "Svizzera",
            ResidenceStreet = "Bahnhofstrasse 12",
            ResidencePostalCode = "8001",
            ResidenceCityName = "Zürich",
        },
    ];
}

/// <summary>
/// One unit of work of the Ticino pilot (a request of the host, a night of a job): a context of its own over the database of the scenario and the real services of the chain of
/// the notification of the guests to the police of Ticino over it, as the container builds them. Only what is outside the application is a stand-in: the e-mail queue, the push queue,
/// the private bucket of the receipts, the audience of the alerts and the services of the other duties (Alloggiati, the traffic light, the connection of the channel manager).
/// </summary>
internal sealed class TicinoScope : IAsyncDisposable
{
    public TicinoScope(AcceptanceScenarioBuilder world, AppDbContext db)
    {
        var clock = world.Clock;
        var log = world.Log;
        var links = EmailTestHelpers.Links(AcceptanceScenarioBuilder.PublicSiteBaseUrl);
        Db = db;

        Places = new PropertySwissPlaceResolver(db);
        Ticino = new TicinoNotificationService(
            db,
            clock,
            Places,
            Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.Bind<GuestNotificationOptions>(GuestNotificationOptions.SectionName)),
            world.Pushes,
            log.For<TicinoNotificationService>());

        // The property, its compliance status and the onboarding part of the guest registration (E29/F02/US01/T02).
        Status = new PropertyComplianceStatusService(
            db,
            AcceptanceScenarioBuilder.Configuration,
            world.Emails,
            links,
            Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.Bind<ComplianceOptions>(ComplianceOptions.SectionName)),
            new ActivityLog(db, clock),
            log.For<PropertyComplianceStatusService>(),
            clock,
            Places);
        Properties = new PropertyService(
            new PropertyRepository(db),
            Status,
            new CinDeadlineCalendar(Microsoft.Extensions.Options.Options.Create(new CinOptions()), clock),
            log.For<PropertyService>(),
            clock);
        OnboardingPart = new GuestRegistrationPartEvaluator(db, Places);

        // The public check-in: the service the link of the guest opens, with the checks of the Swiss data of the guests (NF-132).
        var stayGuests = new StayGuestService(
            db,
            new AlloggiatiCodeTableService(db, log.For<AlloggiatiCodeTableService>(), clock),
            clock,
            ticinoNotifications: Ticino);
        CheckIn = new GuestCheckInService(
            db,
            log.For<GuestCheckInService>(),
            stayGuestService: stayGuests,
            timeProvider: clock,
            ticinoNotifications: Ticino);
        Stays = new StayLifecycleService(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            log.For<StayLifecycleService>(),
            clock,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock));

        // The cockpit of the host (the section of the days to notify to the police) and the hourly job that watches the 24 hours.
        Cockpit = new ComplianceWizardService(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Stays,
            Mock.Of<ITouristTaxQuoteService>(),
            Status,
            log.For<ComplianceWizardService>(),
            clock,
            featureFlags: world.Flags,
            ticinoNotifications: Ticino);
        var recipients = new Mock<IComplianceAlertRecipientResolver>();
        recipients
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid orgId, Guid _, CancellationToken _) => world.AudienceOf(orgId));
        Alerts = new ComplianceAlertService(
            db,
            Mock.Of<IComplianceStatusService>(),
            Mock.Of<IComplianceConnectionHealthService>(),
            Mock.Of<IPropertySenderStore>(),
            recipients.Object,
            Mock.Of<ITouristTaxDeadlineSource>(),
            new NotificationService(db, world.Emails, world.Pushes, links, log.For<NotificationService>()),
            Mock.Of<IComplianceAlertFollowUp>(),
            world.Flags,
            Microsoft.Extensions.Options.Options.Create(AcceptanceScenarioBuilder.Bind<ComplianceOptions>(ComplianceOptions.SectionName)),
            log.For<ComplianceAlertService>(),
            clock,
            Ticino);

        // The receipts of the portal, encrypted in the private bucket, and the night that deletes them after five years.
        Receipts = new SwissComplianceReceiptService(db, Ticino, log.For<SwissComplianceReceiptService>(), world.Storage, world.Keys, clock);
        Retention = new SwissComplianceRetentionJob(db, world.Storage, clock, log.For<SwissComplianceRetentionJob>());
    }

    public AppDbContext Db { get; }

    public PropertySwissPlaceResolver Places { get; }

    public TicinoNotificationService Ticino { get; }

    public PropertyComplianceStatusService Status { get; }

    public PropertyService Properties { get; }

    public GuestRegistrationPartEvaluator OnboardingPart { get; }

    public GuestCheckInService CheckIn { get; }

    public StayLifecycleService Stays { get; }

    public ComplianceWizardService Cockpit { get; }

    public ComplianceAlertService Alerts { get; }

    public SwissComplianceReceiptService Receipts { get; }

    public SwissComplianceRetentionJob Retention { get; }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>What the request of a person of an organization has: an authenticated caller, so the tenant filter is on, scoped to the org of the caller.</summary>
internal sealed class OrgRequestTenant(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;

    public bool FilterEnabled => true;
}

/// <summary>
/// The logs of the whole pilot in one place: every line the services wrote, with its structured values and its exception, as a real provider would get them, so the scenario can
/// check at the end that no line holds the data of a guest (NF-128 AC4).
/// </summary>
internal sealed class ScenarioLog
{
    private readonly List<string> _lines = [];

    /// <summary>A logger of <typeparamref name="T"/> that writes into this log.</summary>
    public ILogger<T> For<T>() => new Sink<T>(this);

    /// <summary>How many lines were written.</summary>
    public int Count
    {
        get
        {
            lock (_lines)
                return _lines.Count;
        }
    }

    /// <summary>Everything a log provider could write.</summary>
    public string AllOutput
    {
        get
        {
            lock (_lines)
                return string.Join(Environment.NewLine, _lines);
        }
    }

    private void Add(string line)
    {
        lock (_lines)
            _lines.Add(line);
    }

    private sealed class Sink<T>(ScenarioLog log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            log.Add($"{typeof(T).Name} {logLevel}: {formatter(state, exception)} | {string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"))} | {exception}");
        }
    }
}
