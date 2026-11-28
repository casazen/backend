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
/// that say where it is (6900 «Lugano», no municipality stored), a second agency of Locarno with a guest who arrives the same day and that must see nothing of the first, and a
/// family of three from Zurich (two adults and a girl of nine) that books the flat, completes the online check-in and arrives. The services are the real ones over one database (in memory, or PostgreSQL for the twin of the main
/// path); only the e-mail transport, the push queue, the storage of the receipts and the audience of the alerts are stand-ins that record what they are given. The clock is the one of
/// the world and starts on <see cref="TicinoStartDay"/>, the one fixed date of the pilot: every other date is relative to it.
/// </summary>
internal sealed partial class AcceptanceScenarioBuilder
{
    public const string TicinoStructureNumber = "NL-12345678";

    public const string TicinoBookingCode = "QK5192AB7M";

    public const string TicinoAgencyEmail = "agenzia.quokka.lugano@example.com";

    public const string TicinoHostName = "TEST Gestore";

    public const string OtherAgencyEmail = "agenzia.locarno@example.com";

    public const string OtherAgencyUser = "auth0|test-locarno-agency";

    public const string OtherPropertyName = "TEST Casa Locarno";

    public const string OtherStructureNumber = "NL-87654321";

    public const string OtherGuestFirstName = "Mirko";

    public const string OtherGuestLastName = "Bernasconi";

    public const string OtherBookingCode = "LC7401XY2Z";

    public static readonly DateOnly TicinoStartDay = new(2026, 11, 10);

    public static DateOnly TicinoArrivalDay => TicinoStartDay.AddDays(9);

    public Guid OtherOrgId { get; } = Guid.NewGuid();

    public Guid OtherPropertyId { get; } = Guid.NewGuid();

    public Guid OtherBookingId { get; private set; }

    public RecordingPushQueue Pushes { get; } = new();

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

    public TicinoScope OpenTicinoScope() => new(this, new AppDbContext(_options, NullTenantContext.Instance, Keys));

    public TicinoScope OpenTicinoScopeOf(Guid orgId) => new(this, new AppDbContext(_options, new OrgRequestTenant(orgId), Keys));

    internal ComplianceAlertRecipients AudienceOf(Guid orgId) => _audiences.GetValueOrDefault(orgId) ?? ComplianceAlertRecipients.None;

    private async Task SetUpTicinoAsync()
    {
        await using var scope = OpenTicinoScope();
        var db = scope.Db;

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

        db.Properties.Add(new Property
        {
            Id = OtherPropertyId,
            OrgId = OtherOrgId,
            OwnerId = OtherAgencyUser,
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

        var otherGuest = new Guest
        {
            OrgId = OtherOrgId,
            FirstName = OtherGuestFirstName,
            LastName = OtherGuestLastName,
            Email = $"mirko.bernasconi@{GuestMailDomain}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherBooking = new Booking
        {
            OrgId = OtherOrgId,
            PropertyId = OtherPropertyId,
            GuestId = otherGuest.Id,
            CheckInDate = StoredDay(TicinoArrivalDay),
            CheckOutDate = StoredDay(TicinoArrivalDay.AddDays(2)),
            NumberOfGuests = 1,
            NumberOfAdults = 1,
            NumberOfChildren = 0,
            Status = BookingStatus.Confirmed,
            ConfirmedAt = now,
            Source = BookingSource.Direct,
            BookingCode = OtherBookingCode,
            ExpectedArrivalTime = "15:00",
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Guests.Add(otherGuest);
        db.Bookings.Add(otherBooking);
        OtherBookingId = otherBooking.Id;
        await db.SaveChangesAsync();

        _audiences[OrgId] = new ComplianceAlertRecipients([TicinoAgencyEmail], [AgencyUser]);
        _audiences[OtherOrgId] = new ComplianceAlertRecipients([OtherAgencyEmail], [OtherAgencyUser]);
    }

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

    public async Task SetTheStructureNumberAsync(string typed)
    {
        await using var scope = OpenTicinoScope();
        var property = await scope.Properties.GetPropertyRecordAsync(PropertyId)
            ?? throw new InvalidOperationException("The flat of Lugano does not exist yet.");
        new UpdatePropertyRequest { TicinoStructureId = typed }.ApplyTo(property);
        await scope.Properties.UpdatePropertyAsync(property);
    }

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

    public async Task<int> RunTheHourlyAlertsAsync()
    {
        await using var scope = OpenTicinoScope();
        return (await scope.Alerts.RunAsync()).Sent;
    }

    public async Task RunTheRetentionAsync()
    {
        await using var scope = OpenTicinoScope();
        await scope.Retention.ExecuteAsync(CancellationToken.None);
    }

    public static DateTime StoredDay(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    public static DateOnly BirthOfTheHead(DateOnly arrival) => arrival.AddYears(-41).AddDays(-6);

    public static DateOnly BirthOfTheSecondAdult(DateOnly arrival) => arrival.AddYears(-39).AddDays(-40);

    public static DateOnly BirthOfTheGirl(DateOnly arrival) => arrival.AddYears(-9).AddDays(-100);

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
            pushNotifications: world.Pushes,
            ticinoNotifications: Ticino);
        Stays = new StayLifecycleService(
            db,
            Mock.Of<IAlloggiatiWebService>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            log.For<StayLifecycleService>(),
            clock,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock));

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
        // The alerts of the 24 hours are told to the people who can act on them (alloggiati.submit): the audience is read with that permission, and here the agency's own user holds it.
        recipients
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<NotificationPermission>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid orgId, Guid _, NotificationPermission _, CancellationToken _) => world.AudienceOf(orgId));
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

internal sealed class OrgRequestTenant(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;

    public bool FilterEnabled => true;
}

internal sealed class ScenarioLog
{
    private readonly List<string> _lines = [];

    public ILogger<T> For<T>() => new Sink<T>(this);

    public int Count
    {
        get
        {
            lock (_lines)
                return _lines.Count;
        }
    }

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
