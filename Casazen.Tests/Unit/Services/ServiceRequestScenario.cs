using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A host and a supplier ready for the service request flows of SP-04, on one in-memory database and a clock the test moves
/// (<see cref="Clock"/>, Thursday 8 October 2026, 12:00 in Rome): a host org with a property (comune H501) and a stay, a supplier
/// org that works Monday to Friday 08-13 and 14-18 and Saturday 08-14 with no notice, and one published service
/// (<see cref="ListingId"/>, "Pulizia appartamento", 60 euro per job, two hours). Everything the services queue is recorded in
/// <see cref="Emails"/> and <see cref="Pushes"/>.
/// </summary>
internal sealed class ServiceRequestScenario : IDisposable
{
    public const string HostUserId = TestAuthHandler.DefaultUserId;
    public const string SupplierUserId = "auth0|supplier-member";
    public const string Comune = "H501";
    public const string PropertyName = "Casa Riservata";
    public const string PropertyAddress = "Via Segretissima 7";
    public const string HostNotes = "Il citofono non funziona, chiamare la proprietaria";
    public const string ServiceName = "Pulizia appartamento";
    public const int ServicePriceCents = 6000;
    public const int ServiceMinutes = 120;

    /// <summary>Thursday 8 October 2026, 12:00 in Rome (summer time).</summary>
    public static readonly DateTimeOffset Instant = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Friday 9 October 2026, 10:00 in Rome: inside the morning hours, a free slot of a two hours service.</summary>
    public static readonly DateTime FridayAt10 = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Friday 9 October 2026, 14:00 in Rome: the start of the afternoon hours.</summary>
    public static readonly DateTime FridayAt14 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Saturday 10 October 2026, 09:00 in Rome.</summary>
    public static readonly DateTime SaturdayAt09 = new(2026, 10, 10, 7, 0, 0, DateTimeKind.Utc);

    /// <summary>Sunday 11 October 2026, 10:00 in Rome: the supplier does not work on Sundays.</summary>
    public static readonly DateTime SundayAt10 = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private readonly ServiceRequestTestKit _kit;
    private readonly ClearablePushQueue _pushes;

    private ServiceRequestScenario(ServiceRequestTestKit kit, FakeTimeProvider clock, RecordingEmailQueue emails, ClearablePushQueue pushes)
    {
        _kit = kit;
        Clock = clock;
        Emails = emails;
        _pushes = pushes;
    }

    public FakeTimeProvider Clock { get; }

    public RecordingEmailQueue Emails { get; }

    public IReadOnlyList<QueuedPushRecord> Pushes => _pushes.Queued;

    public AppDbContext Db => _kit.Db;

    public ServiceRequestTestKit Kit => _kit;

    public ServiceRequestService Service => _kit.Service;

    public ServiceRequestNotifier Notifier => _kit.Notifier;

    public SupplierServiceRequestReader Reader => _kit.Reader;

    public SupplierAgendaService Agenda => _kit.Agenda;

    public SupplierServiceCatalogService Catalog => _kit.Catalog;

    /// <summary>The payment service of SP-15a, over <see cref="Gateway"/>.</summary>
    public SupplierPaymentService Payments => _kit.Payments;

    /// <summary>The fake Stripe of the payments: nothing in a scenario reaches the real one.</summary>
    public FakeSupplierPaymentGateway Gateway => _kit.Gateway;

    /// <summary>What would have been queued on Hangfire for the pending payment requests of a supplier (SP-15b).</summary>
    public RecordingSupplierPaymentJobScheduler JobScheduler => _kit.JobScheduler;

    /// <summary>A platform admin who can receive the alerts about the payments that need them (SP-15b).</summary>
    public async Task<string> AddAdminAsync(string email = "admin@test.com", bool active = true)
    {
        var admin = new User
        {
            Id = $"auth0|admin-{Guid.NewGuid():N}",
            Email = email,
            FirstName = "Admin",
            LastName = "CasaZen",
            Role = UserRole.Admin,
            IsActive = active,
        };
        Db.Users.Add(admin);
        await Db.SaveChangesAsync();
        return admin.Id;
    }

    /// <summary>The feature flags of the scenario: all off until a test (or <see cref="EnablePaymentsAsync"/>) switches one on.</summary>
    public TestFeatureFlags Flags => _kit.Flags;

    public Guid HostOrgId { get; private set; }

    public Guid PropertyId { get; private set; }

    public Guid BookingId { get; private set; }

    public Guid SupplierOrgId { get; private set; }

    public Guid ListingId { get; private set; }

    /// <param name="options">The options of the service request flows (the defaults of the wave spec when null).</param>
    /// <param name="saveInterceptor">Put on the context, to make a save fail at the moment the test chooses.</param>
    /// <param name="showcaseOptions">The options of the booking from a showcase (the kit's version of the privacy notice when null).</param>
    /// <param name="publicSiteBaseUrl">The base of the links in the e-mails; <c>null</c> for a site that is not configured.</param>
    /// <param name="dataProtection">
    /// When given, the context carries the converters of the encrypted personal columns, as in production
    /// (<c>EncryptedColumns</c>). The in-memory provider keeps the values it is given, so what is stored is proved on PostgreSQL.
    /// </param>
    /// <param name="paymentOptions">The options of the payments (SP-15a): a platform commission of 10 % when null.</param>
    /// <param name="paymentLogger">Where the payment service logs (SP-15b tests read the errors it writes); nowhere when null.</param>
    public static async Task<ServiceRequestScenario> CreateAsync(
        ServiceRequestOptions? options = null,
        FailingSaveInterceptor? saveInterceptor = null,
        ShowcaseBookingOptions? showcaseOptions = null,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl,
        IDataProtectionProvider? dataProtection = null,
        SupplierPaymentsOptions? paymentOptions = null,
        Microsoft.Extensions.Logging.ILogger<SupplierPaymentService>? paymentLogger = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString());
        if (saveInterceptor is not null)
            builder.AddInterceptors(saveInterceptor);

        var db = new AppDbContext(builder.Options, tenantContext: null, dataProtection);
        var clock = new FakeTimeProvider(Instant);
        var emails = new RecordingEmailQueue();
        var pushes = new ClearablePushQueue();
        var kit = new ServiceRequestTestKit(
            db, emails, pushes, clock, options, publicSiteBaseUrl, showcaseOptions, paymentOptions: paymentOptions, paymentLogger: paymentLogger);
        var scenario = new ServiceRequestScenario(kit, clock, emails, pushes);
        await scenario.SeedWorldAsync();
        return scenario;
    }

    /// <summary>The Stripe account the supplier of the scenario is connected to when <see cref="ConnectSupplierAsync"/> is not told another.</summary>
    public const string SupplierAccountId = "acct_supplier_scenario";

    /// <summary>
    /// Switches the payments flag on and connects the supplier's Stripe account: from here a request is paid inside CasaZen when the
    /// supplier takes it (SP-15a). <paramref name="ready"/> false leaves the account without charges and payouts (the KYC is not done).
    /// </summary>
    public async Task EnablePaymentsAsync(bool ready = true)
    {
        Flags.Set(Casazen.Core.Features.FeatureFlags.SupplierOnlinePayments, true);
        await ConnectSupplierAsync(ready);
    }

    /// <summary>Gives the supplier org a connected account, with charges and payouts enabled when <paramref name="ready"/>.</summary>
    public async Task ConnectSupplierAsync(bool ready = true, string accountId = SupplierAccountId)
    {
        var org = await Db.Orgs.SingleAsync(o => o.Id == SupplierOrgId);
        org.StripeConnectedAccountId = accountId;
        org.ConnectChargesEnabled = ready;
        org.ConnectPayoutsEnabled = ready;
        await Db.SaveChangesAsync();
    }

    /// <summary>The payments of a request as saved, whatever the context tracks (canceled ones included).</summary>
    public async Task<List<ServiceRequestPayment>> PaymentsOfAsync(Guid requestId) =>
        await Db.ServiceRequestPayments.AsNoTracking().Where(p => p.ServiceRequestId == requestId).OrderBy(p => p.CreatedAt).ToListAsync();

    /// <summary>
    /// A request paid inside CasaZen that the supplier completed with <paramref name="finalAmountCents"/> (the payments enabled, the
    /// supplier connected): the request, taken and completed through the service the way the API does.
    /// </summary>
    public async Task<ServiceRequest> CompletedOnlineAsync(int? finalAmountCents = ServicePriceCents)
    {
        await EnablePaymentsAsync();
        var request = await TakenAsync();
        return await Service.CompleteAsync(
            request.Id, SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: finalAmountCents));
    }

    /// <summary>Forgets the emails and pushes queued so far, to assert only on what the next step queues.</summary>
    public void ForgetNotifications()
    {
        lock (Emails.Queued)
            Emails.Queued.Clear();
        _pushes.Clear();
    }

    /// <summary>A request of the host for the supplier, created through the service (the way the API does).</summary>
    public Task<ServiceRequest> RequestAsync(
        DateTime? start = null,
        bool withService = true,
        ServiceRequestRentalContext rentalContext = ServiceRequestRentalContext.ShortRent,
        string? notes = HostNotes,
        Guid? serviceListingId = null,
        string category = ServiceCategories.Cleaning) =>
        Service.CreateAsync(new CreateServiceRequestCommand(
            HostOrgId,
            HostUserId,
            PropertyId,
            rentalContext == ServiceRequestRentalContext.ShortRent ? BookingId : null,
            SupplierOrgId,
            category,
            ServiceRequestUrgency.Normal,
            notes,
            false,
            rentalContext,
            serviceListingId ?? (withService ? ListingId : null),
            start));

    /// <summary>A request the supplier took (without a time of its own unless the request has one).</summary>
    public async Task<ServiceRequest> TakenAsync(DateTime? start = null, TakeServiceRequestCommand? command = null)
    {
        var request = await RequestAsync(start);
        return await Service.TakeAsync(request.Id, SupplierOrgId, SupplierUserId, command);
    }

    /// <summary>A request whose work has started.</summary>
    public async Task<ServiceRequest> StartedAsync(DateTime? start = null, TakeServiceRequestCommand? command = null)
    {
        var request = await TakenAsync(start, command);
        return await Service.StartAsync(request.Id, SupplierOrgId);
    }

    /// <summary>The request as saved, whatever the context tracks.</summary>
    public async Task<ServiceRequest> ReadAsync(Guid id) =>
        await Db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);

    /// <summary>A request written straight to the database in <paramref name="status"/>, for the states the flows reach slowly.</summary>
    public async Task<ServiceRequest> SeedAsync(
        ServiceRequestStatus status,
        Action<ServiceRequest>? change = null,
        Guid? supplierOrgId = null,
        DateTime? createdAt = null)
    {
        var created = createdAt ?? Instant.UtcDateTime;
        var request = new ServiceRequest
        {
            OrgId = HostOrgId,
            BookingId = BookingId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            PropertyId = PropertyId,
            SupplierOrgId = supplierOrgId ?? SupplierOrgId,
            Category = ServiceCategories.Cleaning,
            Status = status,
            Notes = HostNotes,
            CreatedAt = created,
            UpdatedAt = created,
        };
        change?.Invoke(request);
        Db.ServiceRequests.Add(request);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return request;
    }

    /// <summary>Writes a change straight to a request (a state the flows reach slowly), whatever the context tracks.</summary>
    public async Task ChangeRequestAsync(Guid id, Action<ServiceRequest> change)
    {
        var request = await Db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
        change(request);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    public async Task SetSupplierStatusAsync(SupplierStatus status)
    {
        var profile = await Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == SupplierOrgId);
        profile.Status = status;
        await Db.SaveChangesAsync();
    }

    /// <summary>Another supplier of the same comune, to check that nobody reaches the requests of someone else.</summary>
    public async Task<Guid> AddOtherSupplierAsync()
    {
        var org = new Casazen.Core.Entities.Org
        {
            Name = "Other Supplier",
            Slug = $"other-{Guid.NewGuid():N}"[..20],
            DisplayName = "Other Supplier",
            ContactEmail = "other@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        Db.Orgs.Add(org);
        Db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = "other@test.com",
            LegalName = "Other Srl",
            Phone = "+39 06 000000",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{Comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        await Db.SaveChangesAsync();
        return org.Id;
    }

    /// <summary>A service of the supplier's catalog (written straight to the table, as the catalog tests do).</summary>
    public async Task<Guid> AddListingAsync(
        string name = "Servizio extra",
        string category = ServiceCategories.Cleaning,
        SupplierServiceListingStatus status = SupplierServiceListingStatus.Active,
        int? priceFromCents = ServicePriceCents,
        SupplierServicePriceUnit priceUnit = SupplierServicePriceUnit.PerJob,
        bool requiresQuote = false,
        int? durationMinutes = ServiceMinutes,
        int? minNoticeHours = null,
        int weekdaysMask = SupplierServiceWeekdays.AllMask,
        DateTime? deletedAt = null,
        Guid? supplierOrgId = null)
    {
        var listing = new SupplierServiceListing
        {
            OrgId = supplierOrgId ?? SupplierOrgId,
            Slug = $"servizio-{Guid.NewGuid():N}"[..24],
            Name = name,
            Category = category,
            PriceFromCents = priceFromCents,
            PriceUnit = priceUnit,
            RequiresQuote = requiresQuote,
            DurationMinutes = durationMinutes,
            MinNoticeHours = minNoticeHours,
            WeekdaysMask = weekdaysMask,
            Status = status,
            DeletedAt = deletedAt,
            CreatedAt = Instant.UtcDateTime,
            UpdatedAt = Instant.UtcDateTime,
        };
        Db.SupplierServiceListings.Add(listing);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return listing.Id;
    }

    public void Dispose()
    {
        _kit.Dispose();
        Db.Dispose();
    }

    private async Task SeedWorldAsync()
    {
        var hostOrg = new Casazen.Core.Entities.Org
        {
            Name = "Host Org",
            Slug = $"host-{Guid.NewGuid():N}"[..20],
            DisplayName = "Casa Rossi",
            ContactEmail = "host@test.com",
            PlanTier = PlanTier.Starter,
        };
        Db.Orgs.Add(hostOrg);

        var property = new Property
        {
            OwnerId = HostUserId,
            OrgId = hostOrg.Id,
            Name = PropertyName,
            Address = PropertyAddress,
            City = Comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
        };
        Db.Properties.Add(property);

        var booking = new Booking
        {
            OrgId = hostOrg.Id,
            PropertyId = property.Id,
            GuestId = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
        };
        Db.Bookings.Add(booking);

        var supplierOrg = new Casazen.Core.Entities.Org
        {
            Name = "Supplier Org",
            Slug = $"sup-{Guid.NewGuid():N}"[..20],
            DisplayName = "Supplier Org",
            ContactEmail = "supplier@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        Db.Orgs.Add(supplierOrg);
        Db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = "supplier@test.com",
            LegalName = "Supplier Srl",
            Phone = "+39 06 123456",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{Comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
        });
        Db.Users.AddRange(
            new User { Id = HostUserId, Email = "host@test.com", OrgId = hostOrg.Id, Role = UserRole.PropertyOwner, IsActive = true, PhoneNumber = "+39 333 0001111" },
            new User
            {
                Id = SupplierUserId,
                Email = "sup@test.com",
                FirstName = "Mario",
                LastName = "Fornitore",
                OrgId = supplierOrg.Id,
                SupplierOrgId = supplierOrg.Id,
                Role = UserRole.Supplier,
                IsActive = true,
            });
        await Db.SaveChangesAsync();

        HostOrgId = hostOrg.Id;
        PropertyId = property.Id;
        BookingId = booking.Id;
        SupplierOrgId = supplierOrg.Id;

        // 30 minutes between two jobs, 3 jobs a day, no notice, 35 days ahead, a slot every hour.
        await Agenda.ReplaceRulesAsync(SupplierOrgId, new SupplierRulesInput(30, 3, 0, 35, 60));
        await Agenda.ReplaceHoursAsync(SupplierOrgId, WorkingWeek());

        ListingId = await AddListingAsync(ServiceName);
    }

    /// <summary>Monday to Friday 08:00-13:00 and 14:00-18:00, Saturday 08:00-14:00, Sunday off.</summary>
    public static SupplierHoursInput WorkingWeek() => new(
    [
        Day(DayOfWeek.Monday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Tuesday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Wednesday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Thursday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Friday, Band(8, 13), Band(14, 18)),
        Day(DayOfWeek.Saturday, Band(8, 14)),
        Day(DayOfWeek.Sunday),
    ]);

    private static SupplierHoursBandInput Band(int startHour, int endHour) => new(startHour * 60, endHour * 60);

    private static SupplierHoursDayInput Day(DayOfWeek weekday, params SupplierHoursBandInput[] bands) => new(weekday, bands);

    private sealed class ClearablePushQueue : IPushNotificationService
    {
        private readonly List<QueuedPushRecord> _queued = [];

        public IReadOnlyList<QueuedPushRecord> Queued
        {
            get
            {
                lock (_queued)
                    return _queued.ToList();
            }
        }

        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            lock (_queued)
                _queued.Add(new QueuedPushRecord(deliveryKey, audience, payload));
            return true;
        }

        public void Clear()
        {
            lock (_queued)
                _queued.Clear();
        }
    }
}
