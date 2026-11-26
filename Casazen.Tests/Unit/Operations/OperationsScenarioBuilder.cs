using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Octorate;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Extensions;
using Casazen.Web.Resources;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StripeIntent = Stripe.PaymentIntent;

namespace Casazen.Tests.Unit.Operations;

/// <summary>
/// The agency of the operations acceptance chain (E28/F01/US01/T01): an agency of Como on the plan Agency, with one flat in Como on the channel manager, the
/// organization settings the chain needs, the routines of the operations steps and every flag of those steps on. The world is the application's own composition, not
/// a copy of it: the registrations are the extension methods <c>Program.cs</c> calls (the core services, the booking operations, the Octorate connector, the guests
/// written to the channel manager, the check-in link delivery, the tourist tax collection, the scheduled messages, the message hub), over one in-memory database, so a
/// handler nobody registered, or a registration that broke, stops the chain at the step that needed it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The real code, the external services replaced.</b> The channel manager (<see cref="Octorate"/>) and Stripe (<see cref="Stripe"/>) are mocks, nothing reaches a
/// network; so is the AI provider the engine of the routines asks which operations to run (it answers «all of them»: <see cref="CreateWithoutAnAiProviderAsync"/> is the world
/// without one, which closes every routine); the e-mail queue, the push queue and the Hangfire client record what they are given (<see cref="Emails"/>, <see cref="Pushes"/>, <see cref="Jobs"/>); the files are
/// in memory (<see cref="Storage"/>); the clock is the scenario's (<see cref="Clock"/>). The outbox is delivered by the real dispatcher over the real bus
/// (<c>DeliverEventsAsync</c>): the handlers that run are the ones the application registers. The one door that cannot run in memory is the write of the
/// conversation of a direct booking (the service updates the thread with a bulk <c>ExecuteUpdate</c>): it is a recorder (<see cref="DirectThread"/>), and the
/// message layer's own tests prove that write.</item>
/// <item><b>One scope per step.</b> Every step opens its own scope (<see cref="Scope"/>) as a request or a job does, on the same database: nothing a step tracks leaks into
/// the next one. A step that stands for a request of the host is under the org of the agency, the way the tenant filter sees it; a step of a guest or of a job is not.</item>
/// <item><b>The configuration shipped.</b> The options are the ones of <c>Casazen.Web/appsettings.json</c>; the flags of the chain are on, nothing else.</item>
/// </list>
/// Everything is invented and obviously test data.
/// </remarks>
internal sealed partial class OperationsScenarioBuilder : IAsyncDisposable
{
    public const string PublicSiteBaseUrl = "https://app.casazen.test";

    public const string AgencyName = "TEST Agenzia Lago di Como Srl";

    public const string FlatName = "TEST Appartamento Lago Como";

    /// <summary>The ISTAT code of Como, the comune of the flat.</summary>
    public const string ComuneIstatCode = "013075";

    public const string ComuneName = "Como";

    public const string HostUser = "auth0|test-ops-host";

    /// <summary>The member of the staff of the agency who cleans the flat.</summary>
    public const string StaffUser = "auth0|test-ops-staff";

    public const string OctorateAccommodation = "585492";

    public const string OctorateRoom = "956528";

    /// <summary>The channel id Octorate gives Booking.com.</summary>
    public const string BookingComChannel = "142";

    /// <summary>The connected Stripe account of the agency.</summary>
    public const string StripeAccount = "acct_test_como";

    /// <summary>The address of a guest of the direct and manual bookings (a domain of the tests, not one the standards reserve).</summary>
    public const string GuestEmail = "mario.rossi@ospiti-test.it";

    /// <summary>The nightly price of the flat and the cleaning fee of its direct bookings.</summary>
    public const decimal NightlyRate = 150m;

    public const decimal CleaningFee = 50m;

    /// <summary>The tourist tax of Como for each person and night, from the age of 14.</summary>
    public const decimal TouristTaxPerNight = 2.00m;

    /// <summary>What the agency pays its member of the staff for a cleaning, in cents of euro.</summary>
    public const int CleaningPriceCents = 4000;

    /// <summary>The arrival and the departure of every stay of the chain (Rome): three nights.</summary>
    public static readonly DateOnly CheckInDay = new(2026, 12, 4);

    public static readonly DateOnly CheckOutDay = new(2026, 12, 7);

    private static readonly IDataProtectionProvider SharedKeys = new EphemeralDataProtectionProvider();

    private static readonly Regex TokenInText = TokenPattern();

    private readonly string _database = $"Operations_{Guid.NewGuid():N}";
    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<OctorateCheckInGuestUpsert> _guestsWritten = new();
    private readonly ConcurrentQueue<string> _chatMessages = new();
    private readonly ConcurrentDictionary<string, StripeIntent> _taxIntents = new();
    private readonly ConcurrentDictionary<string, StripeIntent> _taxIntentsByKey = new();
    private ServiceProvider _provider = null!;
    private int _sequence;
    private bool _noAiProvider;

    private OperationsScenarioBuilder()
    {
        DirectThread = new DirectThreadRecorder(Clock);
    }

    /// <summary>The clock of every service: the scenario moves it from step to step.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));

    /// <summary>The channel manager.</summary>
    public Mock<IOctorateApiClient> Octorate { get; } = new();

    /// <summary>Stripe: a payment intent is created the way the checkout and the tourist tax ask for it.</summary>
    public Mock<IStripeService> Stripe { get; } = new();

    /// <summary>The e-mail queue: what it was asked to queue.</summary>
    public RecordingEmailQueue Emails { get; } = new();

    /// <summary>The push queue: the live notices to the hosts.</summary>
    public RecordingPushQueue Pushes { get; } = new();

    /// <summary>The Hangfire client: the jobs the services handed to the background (none runs by itself).</summary>
    public RecordingJobClient Jobs { get; } = new();

    /// <summary>The private bucket in memory.</summary>
    public ObjectStorage Storage { get; } = new();

    /// <summary>The conversations of the direct bookings: the automatic messages written to them.</summary>
    public DirectThreadRecorder DirectThread { get; }

    /// <summary>Every guest payload the channel manager received, in order.</summary>
    public IReadOnlyList<OctorateCheckInGuestUpsert> GuestsWritten => _guestsWritten.ToArray();

    /// <summary>Every message the portal chat took, in order.</summary>
    public IReadOnlyList<string> ChatMessages => _chatMessages.ToArray();

    public Guid OrgId { get; } = Guid.NewGuid();

    public Guid PropertyId { get; } = Guid.NewGuid();

    public HostScope WholeOrg => new(OrgId);

    public IConfiguration Configuration { get; private set; } = null!;

    /// <summary>The flat of the agency, set up as the wizards of the host leave it, with the routines of the chain.</summary>
    public static async Task<OperationsScenarioBuilder> CreateAsync(params string[] moreFlags)
    {
        var world = new OperationsScenarioBuilder();
        foreach (var flag in moreFlags)
            world._settings[$"{FeatureFlags.SectionName}:{flag}"] = "true";

        world.Build();
        world.SetUpTheExternalWorld();
        await world.SetUpAsync();
        return world;
    }

    /// <summary>
    /// The same agency in an environment where no AI provider is in effect (<c>Ai:Provider=Stub</c>, the default, which is also an environment with no <c>Ai__ApiKey</c>): the routines are planned by the
    /// provider the application ships (<see cref="StubAiProvider"/>) instead of the one of <see cref="CreateAsync"/>, which chooses every operation.
    /// </summary>
    public static async Task<OperationsScenarioBuilder> CreateWithoutAnAiProviderAsync()
    {
        var world = new OperationsScenarioBuilder { _noAiProvider = true };
        world.Build();
        world.SetUpTheExternalWorld();
        await world.SetUpAsync();
        return world;
    }

    /// <summary>The instant of <paramref name="hour"/>:<paramref name="minute"/> on <paramref name="day"/> in Rome, in UTC.</summary>
    public static DateTimeOffset InRome(DateOnly day, int hour, int minute = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Casazen.Core.Utilities.RomeCalendar.TimeZone), TimeSpan.Zero);
    }

    public static DateOnly Day(int month, int day) => new(2026, month, day);

    public static DateTime Utc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public void At(DateTimeOffset instant) => Clock.SetUtcNow(instant);

    /// <summary>A unit of work of its own (a request, a job, a delivery of an event) over the database of the scenario.</summary>
    public OperationsScope Scope(bool underTheOrg = true)
    {
        var scope = _provider.CreateAsyncScope();
        if (underTheOrg)
            scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(OrgId);
        return new OperationsScope(scope);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    /// <summary>The token of the check-in link a text carries (<c>/checkin/{token}</c>), or fails when it carries none.</summary>
    public static string TokenIn(string text)
    {
        var match = TokenInText.Match(text);
        return match.Success ? match.Groups[1].Value : throw new InvalidOperationException("The text carries no check-in link.");
    }

    [GeneratedRegex("/checkin/([0-9a-f]{64})", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    // ---------------------------------------------------------------- the composition

    private void Build()
    {
        foreach (var (key, value) in OperationsConfiguration.Settings(PublicSiteBaseUrl))
            _settings[key] = value;

        Configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .AddInMemoryCollection(_settings)
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Testing");
        environment.SetupGet(e => e.ApplicationName).Returns("Casazen.Web");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddOptions();
        services.AddHttpContextAccessor();
        services.AddLocalization();
        services.AddSingleton(Configuration);
        services.AddSingleton(environment.Object);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(SharedKeys);
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseInMemoryDatabase(_database);
            options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        });

        // The application's own registrations, in the order Program.cs calls them (only the groups the operations chain runs through).
        services.AddCasazenRepositories();
        services.AddScoped<IGuestRepository, GuestRepository>();
        services.AddScoped<IPropertyRepository, PropertyRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<ITouristTaxRateRepository, TouristTaxRateRepository>();
        services.AddScoped<IPricingAdapterConfigRepository, PricingAdapterConfigRepository>();
        services.AddScoped<IPricingHistoryRepository, PricingHistoryRepository>();
        services.AddCasazenAuthorization();
        services.AddCasazenServices();
        services.AddScoped<IGuestService, GuestService>();
        services.AddScoped<IPropertyService, PropertyService>();
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<ITouristTaxService, TouristTaxService>();
        services.AddCasazenEmail(Configuration, environment.Object);
        services.AddCasazenAiProvider(Configuration);
        services.AddSingleton<IDocumentTexts, ResxDocumentTexts>();
        services.AddOptions<FiscalDocumentRulesOptions>().Bind(Configuration.GetSection(FiscalDocumentRulesOptions.SectionName));
        services.AddOptions<SwissTouristTaxOptions>().Bind(Configuration.GetSection(SwissTouristTaxOptions.SectionName));
        services.AddScoped<IGuestDocumentLineBuilder, GuestDocumentLineBuilder>();
        services.AddOptions<StorageOptions>().Bind(Configuration.GetSection(StorageOptions.SectionName));
        services.AddScoped<IImageStorageService, ImageStorageService>();
        services.AddScoped<ISdiEInvoiceProvider, UnconfiguredSdiEInvoiceProvider>();
        services.AddScoped<IStripeConnectGateway>(_ => Mock.Of<IStripeConnectGateway>());
        services.AddScoped<IConnectOnboardingService, ConnectOnboardingService>();
        services.AddScoped<StripeWebhookHandler>();
        services.AddScoped<IAlloggiatiWebService>(_ => Mock.Of<IAlloggiatiWebService>());
        services.AddScoped<IAlloggiatiReportScheduler>(_ => Mock.Of<IAlloggiatiReportScheduler>());
        services.AddScoped<IPricingAdapterService, PricingAdapterService>();
        services.AddCasazenBookingOperations();
        services.AddCasazenTouristTaxCollection();
        services.AddCasazenOctorate(Configuration);
        services.AddCasazenOctorateGuestAutoSend();
        services.AddMessageSync();
        services.AddMessagesHub();
        services.AddCheckInLinkDelivery();
        services.AddNotificationPreferences();
        services.AddScheduledMessages();

        // What the push services register in the application, without the hub: the live notices go to a queue the world records.
        services.TryAddSingleton<IPopupAlertPublisher, NoOpPopupAlertPublisher>();
        services.TryAddSingleton<IInAppNotificationPublisher, NoOpInAppNotificationPublisher>();
        services.AddScoped<IInAppNotificationService, InAppNotificationService>();

        // The external world, replaced: nothing here reaches a network.
        services.Replace(ServiceDescriptor.Singleton(Octorate.Object));
        services.Replace(ServiceDescriptor.Singleton(Stripe.Object));
        services.Replace(ServiceDescriptor.Singleton<IEmailQueue>(Emails));
        services.Replace(ServiceDescriptor.Singleton<IPushNotificationService>(Pushes));
        services.Replace(ServiceDescriptor.Singleton<IBackgroundJobClient>(Jobs));
        services.Replace(ServiceDescriptor.Singleton<IFileStorage>(Storage));
        services.Replace(ServiceDescriptor.Singleton<IGuestMessageService>(DirectThread));
        // The engine of the routines asks an AI provider which operations of a routine to run, on every execution (RoutineAgentPlanner): the cleaning at the check-out, the request of a review and the
        // scheduled message all go through it. The world answers «all of them», so the chain does not depend on a model; an environment with no provider in effect (Ai:Provider=Stub, the default)
        // closes every routine with routine_ai_unavailable (OperationsChain_RoutinesNeedAnAiProvider_Test), and an organization with a commercial signup also needs the add-on ai-agents (the world's
        // agency has none: a legacy organization has the access). See docs/runbooks/operations-scenario.md § 3 and § 4.
        IAiProvider planner = _noAiProvider ? new StubAiProvider(NullLogger<StubAiProvider>.Instance) : RoutineRig.SelectAllOperations();
        services.Replace(ServiceDescriptor.Singleton(planner));

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false, ValidateOnBuild = false });
    }

    /// <summary>What the channel manager and Stripe answer the whole chain long (the steps that change the answer say so).</summary>
    private void SetUpTheExternalWorld()
    {
        // The police account the host connected on the panel: valid, and Octorate sends the daily communication.
        Octorate
            .Setup(c => c.GetPoliceAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePoliceAccount { Valid = true, Autorun = true });

        // The table of the places Octorate knows: the comune of the flat and of the guests.
        Octorate
            .Setup(c => c.GetCheckInCitiesAsync("IT", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctoratePlaceEntry("COMO", "403013075", "CITY"), new OctoratePlaceEntry("ROMA", "412058091", "CITY")]);

        // The guests written to the reservation: every one is accepted and named, and the arrival Octorate registered is read back empty.
        Octorate
            .Setup(c => c.UpsertCheckInGuestsAsync(OctorateAccommodation, It.IsAny<string>(), It.IsAny<IReadOnlyList<OctorateCheckInGuestUpsert>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, IReadOnlyList<OctorateCheckInGuestUpsert> guests, CancellationToken _) =>
            {
                foreach (var guest in guests)
                    _guestsWritten.Enqueue(guest);

                return guests
                    .Select(g => new OctorateCheckInGuestResult(g.Position, g.OctorateGuestId ?? "guest-" + g.Position, g.OctorateGuestId is null, true, null))
                    .ToArray();
            });
        Octorate
            .Setup(c => c.GetReservationArrivalAsync(OctorateAccommodation, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReservationArrival(null, null));

        // A booking CasaZen holds is created on the channel manager when it is confirmed: the nights are free, and the reservation is made.
        Octorate
            .Setup(c => c.CheckAvailabilityAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateAvailabilityResult(true, null));
        Octorate
            .Setup(c => c.FindReservationByExternalReferenceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OctorateReservation?)null);
        Octorate
            .Setup(c => c.CreateReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<OctorateReservationCreate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OctorateReservation
            {
                Id = $"WEB{Interlocked.Increment(ref _sequence):D4}",
                Status = OctorateBookingStatus.Confirmed,
                Product = OctorateRoom,
                ChannelId = "270",
                ChannelName = "Booking Engine",
                UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            });

        // A reservation CasaZen made is read back as the channel manager holds it, and a payment recorded on it is taken.
        Octorate
            .Setup(c => c.GetReservationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string id, CancellationToken _) => ReservationOf(id));
        Octorate
            .Setup(c => c.GetReservationAsync(It.IsAny<OctorateBookingTarget>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OctorateBookingTarget _, string id, CancellationToken _) => ReservationOf(id));
        Octorate
            .Setup(c => c.RegisterPaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctoratePaymentRegistration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctoratePaymentResult("pay-1"));

        // The chat of the portal takes the message the check-in link goes in (a step that wants it refused says so).
        TheChatTakesMessages();

        // Stripe: the payment intent of the checkout of a direct booking (the four-argument door) and the one of the tourist tax (the seven-argument door).
        Stripe
            .Setup(s => s.CreateConnectedAccountPaymentIntentAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ReturnsAsync(() => new StripeIntent { Id = $"pi_test_{Guid.NewGuid():N}", ClientSecret = "secret_test", Status = "requires_payment_method" });
        Stripe
            .Setup(s => s.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, long cents, string currency, Dictionary<string, string> metadata, string key, string? _, CancellationToken _) =>
            {
                // What Stripe does with an idempotency key: the same key gets the same payment intent, so a retry never makes a second one.
                var intent = _taxIntentsByKey.GetOrAdd(
                    key,
                    _ => new StripeIntent
                    {
                        Id = $"pi_tax_{Guid.NewGuid():N}",
                        ClientSecret = "secret_tax",
                        Amount = cents,
                        Currency = currency,
                        Status = "requires_payment_method",
                        Metadata = new Dictionary<string, string>(metadata),
                    });
                _taxIntents[intent.Id] = intent;
                return intent;
            });

        // The payment intent a stay holds open is read back by its id before another is asked for (the guest who opens the payment twice gets the same one).
        Stripe
            .Setup(s => s.GetPaymentIntentAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, string? _, CancellationToken _) =>
                _taxIntents.TryGetValue(id, out var intent) ? intent : new StripeIntent { Id = id, Status = "requires_payment_method" });
    }

    private OctorateReservation ReservationOf(string id) => new()
    {
        Id = id,
        Status = OctorateBookingStatus.Confirmed,
        Product = OctorateRoom,
        UpdatedAt = Clock.GetUtcNow().UtcDateTime,
    };

    /// <summary>The chat of the portal takes every message and gives each its own id.</summary>
    public void TheChatTakesMessages()
    {
        var next = 90000;
        Octorate
            .Setup(c => c.SendChatMessageAsync(OctorateAccommodation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string text, CancellationToken _) =>
            {
                _chatMessages.Enqueue(text);
                return new OctorateChatSendResult((++next).ToString(CultureInfo.InvariantCulture), OtaMessageDeliveryState.Sent);
            });
    }

    /// <summary>The application of the agency has no permission to write in the chat of the portal yet: the channel manager refuses the message.</summary>
    public void TheChatIsNotLicensed() =>
        Octorate
            .Setup(c => c.SendChatMessageAsync(OctorateAccommodation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(OctorateAuthException.ChatNotLicensed(403));

    private async Task SetUpAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        var db = scope.Db;
        db.Orgs.Add(new OrgEntity
        {
            Id = OrgId,
            Name = AgencyName,
            Slug = $"como-{OrgId:N}"[..28],
            DisplayName = AgencyName,
            ContactEmail = "agenzia.como@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Agency,
            DefaultLanguage = "it",
            AutoSendGuestsToOctorate = true,
            TouristTaxOnlinePaymentEnabled = true,
            StripeConnectedAccountId = StripeAccount,
            ConnectChargesEnabled = true,
        });
        db.Properties.Add(new Property
        {
            Id = PropertyId,
            OrgId = OrgId,
            OwnerId = HostUser,
            Name = FlatName,
            Address = "Via Volta 3",
            City = ComuneName,
            PostalCode = "22100",
            Country = "IT",
            Currency = "EUR",
            ComuneIstatCode = ComuneIstatCode,
            MaxGuests = 4,
            NightlyRate = NightlyRate,
            CleaningFee = CleaningFee,
            CheckInFrom = new TimeOnly(15, 0),
            CheckOutBy = new TimeOnly(10, 0),
            ComplianceStatus = PropertyComplianceStatus.Active,
        });

        // The listing of the flat on the channel manager: the room of the reservations it imports and the one CasaZen creates.
        var connection = new OctorateConnection { OrgId = OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
        db.OctorateConnections.Add(connection);
        db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = OrgId,
            ConnectionId = connection.Id,
            OctorateAccommodationId = OctorateAccommodation,
            OctorateProductId = OctorateRoom,
            PropertyId = PropertyId,
            Status = OctorateMappingStatus.Confirmed,
            SnapshotName = FlatName,
        });

        // The tourist tax of Como: the official rate of the comune, and the portal that collects it for its own bookings.
        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = ComuneName,
            IstatCode = ComuneIstatCode,
            RegionCode = "LOM",
            RatePerPersonPerNight = TouristTaxPerNight,
            MinimumAge = 14,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        db.TouristTaxChannelRules.Add(new TouristTaxChannelRule
        {
            Channel = BookingSource.BookingCom,
            IstatCode = ComuneIstatCode,
            Collector = TouristTaxCollector.Channel,
            SourceUrl = "https://example.com/test-regola-tassa-como-booking",
            VerifiedAt = Clock.GetUtcNow().UtcDateTime,
        });

        // The member of the staff who cleans the flat.
        db.Users.Add(new User
        {
            Id = StaffUser,
            Email = "staff.como@example.com",
            FirstName = "TEST",
            LastName = "Staff",
            OrgId = OrgId,
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        db.OrgMembers.Add(new OrgMember { OrgId = OrgId, UserId = StaffUser, Role = OrgRole.Owner });

        // The routines of the agency (the library the host picks from, saved as the page of the routines saves them): the cleaning at the check-out time, and the
        // request of a review the day after.
        db.Routines.Add(NewRoutine("Pulizia al check-out", RoutineEventType.AfterCheckout, """{"hours":0}""", """[{"type":"CreateCleaningRequest","config":{}}]"""));
        db.Routines.Add(NewRoutine("Richiesta di recensione", RoutineEventType.AfterCheckout, """{"days":1,"hours":0}""", """[{"type":"RequestReview","config":{}}]"""));
        await db.SaveChangesAsync();

        // The default cleaner of the flat, saved as the wizard of the property saves it: the member of the staff, at the price agreed.
        await using var host = Scope();
        await host.Get<IPropertyCleaningSetupService>().SaveAsync(
            OrgId,
            PropertyId,
            new CleaningSetupInput(PropertyCleanerKind.Staff, null, StaffUser, CleaningPriceCents, "EUR", null, null),
            HostUser);
    }

    private Routine NewRoutine(string name, RoutineEventType eventType, string eventConfig, string actionsJson) => new()
    {
        OrgId = OrgId,
        Name = name,
        EventType = eventType,
        EventConfig = eventConfig,
        ConditionJson = "{}",
        ActionsJson = actionsJson,
        Active = true,
        TestMode = false,
    };

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}

/// <summary>The settings of the world on top of the shipped configuration: the flags of the operations steps on, the addresses and keys of the test environment.</summary>
internal static class OperationsConfiguration
{
    public static IEnumerable<KeyValuePair<string, string?>> Settings(string publicSiteBaseUrl)
    {
        yield return new("App:PublicSiteBaseUrl", publicSiteBaseUrl);
        yield return new("Stripe:PublishableKey", "pk_test_operations");
        yield return new("Email:ApiKey", "re_test_operations");
        yield return new("Email:FromAddress", "noreply@casazen.test");
        yield return new("Octorate:DirectChannelId", "270");
        yield return new("Octorate:PaymentModes:Stripe", "STRIPE");
        yield return new("Octorate:PaymentModes:CashOnArrival", "CASH");
        yield return new("Octorate:PaymentModes:BankTransfer", "BANK_TRANSFER");
        // The hold of a direct booking is stamped with the real clock by the booking service: the scenario's own clock must never read it as expired.
        yield return new("DirectBooking:PendingTtlMinutes", "100000");
        string[] flags =
        [
            FeatureFlags.OctorateConnector,
            FeatureFlags.PortalChatCheckInLink,
            FeatureFlags.TouristTaxOnlinePayment,
            FeatureFlags.PurgeDocumentsAfterReceipt,
            FeatureFlags.Routines,
            FeatureFlags.ScheduledMessages,
            FeatureFlags.ComplianceTrafficLight,
        ];

        foreach (var flag in flags)
            yield return new($"{FeatureFlags.SectionName}:{flag}", "true");
    }
}

/// <summary>One scope of the world: the unit of work of a request or of a job, with its container.</summary>
internal sealed class OperationsScope(AsyncServiceScope scope) : IAsyncDisposable
{
    public IServiceProvider Services => scope.ServiceProvider;

    public AppDbContext Db => Get<AppDbContext>();

    /// <summary>A job or a handler as Hangfire and the bus build it: its constructor filled from the container of the scope.</summary>
    public T Create<T>()
        where T : class => ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);

    public T Get<T>()
        where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

/// <summary>The push queue of the world: every live notice the services handed to it, in order.</summary>
internal sealed class RecordingPushQueue : IPushNotificationService
{
    private readonly ConcurrentQueue<(string Key, PushAudience Audience, PushNotificationPayload Payload)> _pushes = new();

    public IReadOnlyList<(string Key, PushAudience Audience, PushNotificationPayload Payload)> Queued => _pushes.ToArray();

    public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        _pushes.Enqueue((deliveryKey, audience, payload));
        return true;
    }
}

/// <summary>The Hangfire client of the world: it records the jobs handed to the background and runs none of them.</summary>
internal sealed class RecordingJobClient : IBackgroundJobClient
{
    private readonly ConcurrentQueue<(Job Job, IState State)> _jobs = new();

    public IReadOnlyList<(Job Job, IState State)> Created => _jobs.ToArray();

    public string Create(Job job, IState state)
    {
        _jobs.Enqueue((job, state));
        return $"job-{_jobs.Count}";
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;
}

/// <summary>The private bucket of the world, in memory, that tells which objects it holds.</summary>
internal sealed class ObjectStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToArray();

    public void Add(string key, byte[] content) => _objects[key] = content;

    public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        _objects[key] = copy.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.ContainsKey(key));

    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key) => $"https://files.casazen.test/{key}";

    public string? TryGetPublicKey(string url) => null;

    public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);
}

/// <summary>
/// The conversations of the direct bookings of the world. The service that writes an automatic message to the thread of a direct booking updates the thread with a bulk
/// <c>ExecuteUpdate</c>, which the in-memory provider cannot run: this recorder takes its place for the one door the scheduled messages use, and keeps what was written
/// (the text and the key of the message, once for each key). Its own tests are the message layer's.
/// </summary>
internal sealed class DirectThreadRecorder(TimeProvider clock) : IGuestMessageService
{
    private readonly object _gate = new();
    private readonly List<(Guid BookingId, string Text, string Key)> _automatic = [];

    public IReadOnlyList<(Guid BookingId, string Text, string Key)> Automatic
    {
        get
        {
            lock (_gate)
                return _automatic.ToList();
        }
    }

    public Task<SentGuestMessage> ReplyAsAutomationAsync(Guid bookingId, string? text, string? idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = text ?? string.Empty;
        var key = idempotencyKey ?? string.Empty;
        bool replayed;
        lock (_gate)
        {
            replayed = _automatic.Any(m => m.BookingId == bookingId && m.Key == key);
            if (!replayed)
                _automatic.Add((bookingId, body, key));
        }

        var message = new GuestThreadMessage(Guid.NewGuid(), GuestMessageSender.Host, body, clock.GetUtcNow().UtcDateTime);
        return Task.FromResult(new SentGuestMessage(message, false, replayed));
    }

    public Task<GuestThreadView> GetForGuestAsync(Booking booking, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SentGuestMessage> SendFromPortalAsync(Booking booking, string? text, string? idempotencyKey, string? guideAnswer, CancellationToken cancellationToken = default, string? localDraft = null) =>
        SendAsGuestAsync(booking, text, idempotencyKey, cancellationToken);

    public Task<SentGuestMessage> SendAsGuestAsync(Booking booking, string? text, string? idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<HostThreadView> GetForHostAsync(Guid bookingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SentGuestMessage> ReplyAsHostAsync(Guid bookingId, string? text, string? idempotencyKey, string? userId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<HostThreadView> MarkReadAsync(Guid bookingId, string? userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<HostThreadPage> ListForHostAsync(
        HostScope scope,
        bool? needsReply,
        Guid? propertyId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<HostUnreadThread>> ListUnreadForHostAsync(HostScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
