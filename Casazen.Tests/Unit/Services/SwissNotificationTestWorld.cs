using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

internal sealed class SwissNotificationTestWorld : IDisposable
{
    public static readonly DateOnly Day = new(2026, 11, 19);

    private static readonly IReadOnlyDictionary<string, string> MunicipalityOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TI"] = "5192",
        ["SH"] = "2939",
        ["ZG"] = "1711",
        ["ZH"] = "261",
        ["NW"] = "1509",
        ["FR"] = "2196",
        ["BS"] = "2701",
        ["BE"] = "351",
        ["GE"] = "6621",
        ["NE"] = "6458",
        ["VD"] = "5586",
    };

    private readonly string _store = Guid.NewGuid().ToString();

    public SwissNotificationTestWorld(bool regimesOn = true)
    {
        Flags.Setup(f => f.IsEnabled(FeatureFlags.GuestRegistrationRegimes)).Returns(() => RegimesOn);
        RegimesOn = regimesOn;
        Db = NewDb();
    }

    public bool RegimesOn { get; set; }

    public AppDbContext Db { get; }

    public string Store => _store;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

    public Mock<IFeatureFlags> Flags { get; } = new();

    public List<(string Key, PushAudience Audience, PushNotificationPayload Payload)> Pushes { get; } = [];

    public AppDbContext NewDb(ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_store).Options, tenant);

    public IGuestRegistrationRegimeResolver Resolver(AppDbContext? db = null) =>
        new GuestRegistrationRegimeResolver(db ?? Db, new PropertySwissPlaceResolver(db ?? Db), Flags.Object);

    public SwissNotificationService Service(AppDbContext? db = null, bool withPush = false, ILogger<SwissNotificationService>? logger = null)
    {
        var context = db ?? Db;
        return new SwissNotificationService(
            context,
            Resolver(context),
            Flags.Object,
            Clock,
            withPush ? new Push(this) : null,
            logger ?? NullLogger<SwissNotificationService>.Instance);
    }

    public OrgEntity AddOrg(string contactEmail = "org@example.com")
    {
        var org = new OrgEntity { Name = "TEST", Slug = Guid.NewGuid().ToString("N"), ContactEmail = contactEmail };
        Db.Orgs.Add(org);
        Db.SaveChanges();
        return org;
    }

    public void AddUser(string id, string firstName, string lastName)
    {
        Db.Users.Add(new User { Id = id, Email = $"{firstName.ToLowerInvariant()}@example.com", FirstName = firstName, LastName = lastName });
        Db.SaveChanges();
    }

    public Property AddProperty(
        string canton,
        OrgEntity? org = null,
        GuestNotificationOverride? choice = null,
        string timezone = "Europe/Zurich",
        string name = "Casa TEST",
        string country = "CH")
    {
        var code = MunicipalityOf[canton];
        if (!Db.SwissMunicipalities.Any(m => m.MunicipalityCode == code))
            Db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = code, CantonCode = canton, OfficialName = $"Comune {canton}" });

        org ??= AddOrg();
        var property = new Property
        {
            OrgId = org.Id,
            Name = name,
            Address = "Via Test 1",
            City = "Test",
            PostalCode = "0000",
            Country = country,
            Currency = "CHF",
            Timezone = timezone,
            SwissMunicipalityCode = code,
            TicinoStructureId = canton == "TI" ? "NL-12345678" : null,
            GuestNotificationOverride = choice,
            RentalMode = RentalMode.Short,
            IsActive = true,
        };
        Db.Properties.Add(property);
        Db.SaveChanges();
        return property;
    }

    public Booking AddStay(
        Property property,
        DateOnly? day = null,
        BookingStatus status = BookingStatus.CheckedIn,
        string? arrivedAtUtc = null,
        DateTime? noShowAt = null,
        int guests = 1,
        string? bookingCode = null,
        string? plate = null,
        string? expectedArrival = null)
    {
        var arrival = day ?? Day;
        var guest = new Guest { OrgId = property.OrgId, FirstName = "Booker", LastName = "Person", Email = "booker@example.com" };
        var checkIn = DateTime.SpecifyKind(arrival.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var booking = new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            Status = status,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(2),
            NumberOfGuests = guests,
            NoShowAt = noShowAt,
            VehiclePlate = plate,
            ExpectedArrivalTime = expectedArrival,
            ArrivedAt = arrivedAtUtc is null
                ? null
                : DateTime.Parse(arrivedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        };
        if (bookingCode is not null)
            booking.BookingCode = bookingCode;

        Db.AddRange(guest, booking);
        Db.SaveChanges();
        return booking;
    }

    public StayGuest AddGuest(
        Booking stay,
        int position = 0,
        string firstName = "Anna",
        string lastName = "Rossi",
        DateTime? birth = null,
        string? nationalityCode = "100000241",
        string nationalityName = "SVIZZERA",
        string? documentTypeCode = "PASOR",
        string documentNumber = "X1234567",
        string? street = "Via Berna 11",
        string? postalCode = "8001",
        string? city = "Zürich",
        string? exemption = null,
        bool noBirth = false)
    {
        var row = new StayGuest
        {
            BookingId = stay.Id,
            OrgId = stay.OrgId,
            Position = position,
            FirstName = firstName,
            LastName = lastName,
            DateOfBirth = noBirth ? null : birth ?? new DateTime(1984, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            CitizenshipCode = nationalityCode,
            CitizenshipName = nationalityName,
            DocumentTypeCode = documentTypeCode,
            DocumentNumber = documentNumber,
            ResidenceStreet = street,
            ResidencePostalCode = postalCode,
            ResidenceCityName = city,
            TaxExemptionCode = exemption,
        };
        Db.StayGuests.Add(row);
        Db.SaveChanges();
        return row;
    }

    public void Dispose() => Db.Dispose();

    private sealed class Push(SwissNotificationTestWorld world) : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
        {
            world.Pushes.Add((deliveryKey, audience, payload));
            return true;
        }
    }
}

internal sealed class SwissTestTenant(Guid orgId) : ITenantContext
{
    public Guid? OrgId { get; } = orgId;

    public bool FilterEnabled => true;
}
