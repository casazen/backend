using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

internal sealed class PropertyModeHarness : IDisposable
{
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

    public static DateTime Day(int day) => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day - 1);

    public PropertyModeService NewService(
        AppDbContext db,
        IActivityLog? activityLog = null,
        ICalendarChangeRecorder? changeRecorder = null,
        IBackgroundJobClient? jobs = null) =>
        new(
            db,
            new ConfigurationBuilder().Build(),
            Compliance.Object,
            Notifications.Object,
            activityLog ?? ActivityLogFor(db),
            NullLogger<PropertyModeService>.Instance,
            Clock,
            changeRecorder,
            jobs);

    public ActivityLog ActivityLogFor(AppDbContext db) => new(db, Clock);

    public AppDbContext SecondContext() => new(DbOptions);

    public void Dispose() => Db.Dispose();

    private DbContextOptions<AppDbContext>? _options;

    private DbContextOptions<AppDbContext> DbOptions => _options!;

    private AppDbContext CreateDb()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(_options);
    }

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

    /// <param name="terminatedOn">
    /// The lease is recorded as ended on that day (LR-14): a libero lease whose period is over renews by itself unless a notice or an
    /// early termination says otherwise, so a lease "that ended before today" is one that was concluded.
    /// </param>
    public async Task<LeaseContract> SeedLeaseAsync(
        Property property,
        DateTime start,
        DateTime end,
        LeaseStatus status = LeaseStatus.Registered,
        DateTime? terminatedOn = null)
    {
        var lease = new LeaseContract
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            Status = status,
            StartDate = start,
            EndDate = end,
            MonthlyRent = 800m,
            TerminatedOn = terminatedOn,
        };
        Db.LeaseContracts.Add(lease);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return lease;
    }

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
