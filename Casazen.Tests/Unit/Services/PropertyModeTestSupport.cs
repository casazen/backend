using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: the scheduled change of rental mode on the EF InMemory database, with a clock the test moves, the notifications and
/// the compliance service as mocks. "Today" in Rome is 10 October 2026 (the clock is 12:00 there).
/// </summary>
internal sealed class PropertyModeHarness : IDisposable
{
    /// <summary>10 October 2026, midnight UTC: the calendar day of Rome the clock shows.</summary>
    public static readonly DateTime Today = Day(10);

    public static readonly DateTimeOffset Now = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

    public const string UserId = "auth0|owner";

    public PropertyModeHarness()
    {
        Db = CreateDb();
        Clock = new FakeTimeProvider(Now);
        Notifications.Setup(n => n.SendPropertyModeChangeAsync(It.IsAny<PropertyModeNotice>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Compliance.Setup(c => c.ReevaluateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new PropertyComplianceCheck(
                id, PropertyComplianceStatus.Active, PropertyComplianceStatus.Active, [], false));
        Service = NewService(Db);
    }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public Mock<INotificationService> Notifications { get; } = new();

    public Mock<IPropertyComplianceStatusService> Compliance { get; } = new();

    public PropertyModeService Service { get; }

    /// <summary>The given day of October 2026, midnight UTC (negative or past 31: the days around it).</summary>
    public static DateTime Day(int day) => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day - 1);

    /// <summary>A service on another context over the same database, as a second request or a second job run would have.</summary>
    public PropertyModeService NewService(AppDbContext db) =>
        new(
            db,
            new ConfigurationBuilder().Build(),
            Compliance.Object,
            Notifications.Object,
            NullLogger<PropertyModeService>.Instance,
            Clock);

    public AppDbContext SecondContext() => new(DbOptions);

    public void Dispose() => Db.Dispose();

    private DbContextOptions<AppDbContext>? _options;

    private DbContextOptions<AppDbContext> DbOptions => _options!;

    private AppDbContext CreateDb()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        // No Data Protection provider, like every other unit test: EF caches one model per provider, so a provider per test
        // would build (and keep) one model per test and crowd out the cache the other tests share.
        return new AppDbContext(_options);
    }

    // ─── Seed ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<Property> SeedPropertyAsync(
        RentalMode mode = RentalMode.Short,
        Guid? orgId = null,
        string name = "Casa del Mare",
        string contactEmail = "host@casadelmare.test")
    {
        var org = orgId is { } existing ? await Db.Orgs.FindAsync(existing) : null;
        if (org is null)
        {
            org = new OrgEntity
            {
                Id = orgId ?? Guid.NewGuid(),
                Name = "Org",
                Slug = $"org-{Guid.NewGuid():N}",
                ContactEmail = contactEmail,
            };
            Db.Orgs.Add(org);
        }

        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = UserId,
            Name = name,
            Address = $"Via Roma {Guid.NewGuid():N}",
            City = "Lecce",
            PostalCode = "73100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            RentalMode = mode,
        };
        Db.Properties.Add(property);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return property;
    }

    public async Task<Booking> SeedStayAsync(
        Property property,
        DateTime checkIn,
        DateTime checkOut,
        BookingStatus status = BookingStatus.Confirmed,
        BookingSource source = BookingSource.Direct,
        DateTime? createdAt = null,
        bool checkoutHold = false)
    {
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Guest = guest,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = source,
            CreatedAt = createdAt ?? Now.UtcDateTime.AddDays(-1),
            // A checkout hold: a Pending booking of the public checkout with its PaymentIntent (BK-21).
            StripeSetupIntentId = checkoutHold ? $"seti_{Guid.NewGuid():N}" : null,
        };
        Db.Bookings.Add(booking);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return booking;
    }

    public async Task<CalendarBlock> SeedImportedBlockAsync(
        Property property,
        DateTime start,
        DateTime end,
        ICalFeedChannel? channel = ICalFeedChannel.Airbnb,
        Guid? bookingId = null)
    {
        Guid? feedId = null;
        if (channel is { } feedChannel)
        {
            var feed = new PropertyICalFeed
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Channel = feedChannel,
                ImportUrl = $"https://example.com/{Guid.NewGuid():N}.ics",
            };
            Db.PropertyICalFeeds.Add(feed);
            feedId = feed.Id;
        }

        var block = new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Source = CalendarBlockSource.ICalImport,
            FeedId = feedId,
            ExternalUid = $"uid-{Guid.NewGuid():N}",
            StartUtc = start,
            EndUtc = end,
            Summary = "Reserved",
            BookingId = bookingId,
        };
        Db.CalendarBlocks.Add(block);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return block;
    }

    public async Task<CalendarBlock> SeedManualBlockAsync(
        Property property,
        DateTime start,
        DateTime end,
        CalendarBlockReason reason = CalendarBlockReason.Owner)
    {
        var block = new CalendarBlock
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Source = CalendarBlockSource.Manual,
            StartUtc = start,
            EndUtc = end,
            ManualReason = reason,
        };
        Db.CalendarBlocks.Add(block);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return block;
    }

    public async Task<LeaseContract> SeedLeaseAsync(
        Property property,
        DateTime start,
        DateTime end,
        LeaseStatus status = LeaseStatus.Registered)
    {
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = status,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 800m,
        };
        Db.LeaseContracts.Add(lease);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return lease;
    }

    /// <summary>A change straight in the database, as the creation would have written it.</summary>
    public async Task<PropertyModeChange> SeedChangeAsync(
        Property property,
        RentalMode to,
        DateTime effectiveDate,
        PropertyModeChangeStatus status = PropertyModeChangeStatus.Scheduled,
        RentalMode? from = null)
    {
        var change = new PropertyModeChange
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            FromMode = from ?? PropertyModeRules.Opposite(to),
            ToMode = to,
            EffectiveDate = effectiveDate,
            Status = status,
            CreatedByUserId = UserId,
            CreatedAt = Now.UtcDateTime.AddDays(-3),
        };
        Db.PropertyModeChanges.Add(change);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return change;
    }

    public async Task<Property> ReloadPropertyAsync(Guid propertyId)
    {
        Db.ChangeTracker.Clear();
        return await Db.Properties.AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    public async Task<PropertyModeChange> ReloadChangeAsync(Guid changeId)
    {
        Db.ChangeTracker.Clear();
        return await Db.PropertyModeChanges.AsNoTracking().SingleAsync(c => c.Id == changeId);
    }

    public async Task<List<CalendarBlock>> ModeChangeBlocksAsync(Guid propertyId)
    {
        Db.ChangeTracker.Clear();
        return await Db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.PropertyId == propertyId && b.ManualReason == CalendarBlockReason.ModeChange)
            .ToListAsync();
    }
}
