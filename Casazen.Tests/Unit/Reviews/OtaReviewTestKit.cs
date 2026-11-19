using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Octorate;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Casazen.Tests.Unit.Reviews;

/// <summary>An authenticated request of one org: the tenant filter is on, so a row of another org is invisible whatever the query says.</summary>
internal sealed class RequestTenant(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;

    public bool FilterEnabled => true;
}

/// <summary>One stay of the world of the reviews: a booking of a channel that ended three days ago, with its reference on the channel manager and the listing of its property.</summary>
internal sealed record ReviewStay(Guid OrgId, Guid PropertyId, Guid BookingId, string Accommodation, string Refer);

/// <summary>A service of the reviews as an authenticated request of one org builds it: its own context (the tenant filter on) and its own scope of the org.</summary>
internal sealed class ReviewRequestRig(AppDbContext db, OtaReviewService service) : IAsyncDisposable
{
    public AppDbContext Db => db;

    public OtaReviewService Service => service;

    public ValueTask DisposeAsync() => db.DisposeAsync();
}

/// <summary>
/// The world every test of the reviews of the channels (E05/F06/US01/T02) runs on, over an in-memory database: the org of <see cref="HostScopeScenario"/> (two houses, the
/// collaborator «Solo alcuni» who was given only the first, a second org with a house), an Airbnb stay on each house that ended three days ago with the listing of its house
/// confirmed, a loose client of the channel manager the test steers and watches, a clock the test moves, and the log sink. The service is built as the container builds it, one
/// per request, on its own context of the same database.
/// </summary>
internal sealed class OtaReviewHarness
{
    public const string GrantedAccommodation = "585001";
    public const string HiddenAccommodation = "585002";
    public const string OtherOrgAccommodation = "585003";
    public const string GrantedRefer = "HMGRANTED1";
    public const string HiddenRefer = "HMHIDDEN01";
    public const string OtherOrgRefer = "HMOTHERORG";

    /// <summary>2026-11-20 09:00 in Rome.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly string _database;
    private readonly Dictionary<Guid, List<ChannelMappingRow>> _rows = [];

    private OtaReviewHarness(string database, HostScopeWorld world)
    {
        _database = database;
        World = world;
        Scope = new OctorateOrgScope(NullTenantContext.Instance);
        Limiter = new OctorateRateLimiter(Clock);
        Mappings
            .Setup(m => m.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid org, CancellationToken _) => (IReadOnlyList<ChannelMappingRow>)(_rows.TryGetValue(org, out var rows) ? rows.ToList() : []));
    }

    public HostScopeWorld World { get; }

    public ReviewStay Granted { get; private set; } = null!;

    public ReviewStay Hidden { get; private set; } = null!;

    public ReviewStay Other { get; private set; } = null!;

    /// <summary>The loose client of the channel manager: a call a test did not set up answers nothing and counts.</summary>
    public Mock<IOctorateApiClient> Client { get; } = new();

    public Mock<IChannelMappingService> Mappings { get; } = new();

    public TestFeatureFlags Flags { get; } = new(FeatureFlags.OctorateConnector);

    public FakeTimeProvider Clock { get; } = new(Now);

    public CapturingLogger<OtaReviewService> Log { get; } = new();

    /// <summary>The scope of the calls of a job or a handler (no request): a test opens <c>Use(org)</c> around the calls it makes.</summary>
    public OctorateOrgScope Scope { get; }

    /// <summary>The one rate limiter of the application, over the clock of the harness.</summary>
    public OctorateRateLimiter Limiter { get; }

    public static async Task<OtaReviewHarness> CreateAsync()
    {
        var database = $"ota-reviews-{Guid.NewGuid():N}";
        HostScopeWorld world;
        await using (var seed = OpenDb(database, NullTenantContext.Instance))
            world = await HostScopeScenario.SeedAsync(seed);

        var harness = new OtaReviewHarness(database, world);
        harness.Granted = await harness.AddStayAsync(world.OrgId, world.Granted.Id, GrantedAccommodation, GrantedRefer);
        harness.Hidden = await harness.AddStayAsync(world.OrgId, world.Hidden.Id, HiddenAccommodation, HiddenRefer);
        harness.Other = await harness.AddStayAsync(world.OtherOrgId, world.OtherOrgProperty.Id, OtherOrgAccommodation, OtherOrgRefer);
        return harness;
    }

    private static AppDbContext OpenDb(string database, ITenantContext tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options, tenant);

    public AppDbContext NewDb() => OpenDb(_database, NullTenantContext.Instance);

    /// <summary>The service in system mode (a job, a handler): the test opens <c>Scope.Use(org)</c> around its calls.</summary>
    public OtaReviewService Service(AppDbContext db, IPushNotificationService? push = null) =>
        new(db, Client.Object, Scope, Mappings.Object, Flags, new DomainEventRecorder(db, NullTenantContext.Instance, Clock), Log, Clock, push);

    /// <summary>The service as an authenticated request of <paramref name="orgId"/> builds it: the org is the request's, and the tenant filter of the context is on.</summary>
    public ReviewRequestRig AsRequestOf(Guid orgId)
    {
        var tenant = new RequestTenant(orgId);
        var db = OpenDb(_database, tenant);
        var service = new OtaReviewService(
            db,
            Client.Object,
            new OctorateOrgScope(tenant),
            Mappings.Object,
            Flags,
            new DomainEventRecorder(db, tenant, Clock),
            Log,
            Clock);
        return new ReviewRequestRig(db, service);
    }

    /// <summary>A booking of a channel, ended three days ago, with a reservation on the channel manager and (by default) the confirmed listing of its property.</summary>
    public async Task<ReviewStay> AddStayAsync(
        Guid orgId,
        Guid propertyId,
        string accommodation,
        string refer,
        BookingSource source = BookingSource.Airbnb,
        BookingStatus status = BookingStatus.CheckedOut,
        bool listing = true,
        int endedDaysAgo = 3)
    {
        await using var db = NewDb();
        var guest = new Guest { OrgId = orgId, FirstName = "Giulietta", LastName = "Verdi", Email = $"{Guid.NewGuid():N}@example.com" };
        var checkOut = Now.UtcDateTime.Date.AddDays(-endedDaysAgo);
        var booking = new Booking
        {
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = checkOut.AddDays(-3),
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            NumberOfAdults = 2,
            Status = status,
            Source = source,
            OctorateReservationId = refer,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        if (listing)
        {
            if (!_rows.TryGetValue(orgId, out var rows))
                _rows[orgId] = rows = [];

            if (rows.All(r => r.OctorateAccommodationId != accommodation))
                rows.Add(MessageSyncHarness.Row(accommodation, OctorateMappingStatus.Confirmed, propertyId));
        }

        return new ReviewStay(orgId, propertyId, booking.Id, accommodation, refer);
    }

    /// <summary>The listing of the property is no longer confirmed (the mapping was withdrawn).</summary>
    public void WithdrawListing(Guid orgId, string accommodation) =>
        _rows[orgId].RemoveAll(r => r.OctorateAccommodationId == accommodation);

    /// <summary>The org has a connected account in the test environment of the channel manager: the sync of the reviews reads it.</summary>
    public async Task ConnectAsync(params Guid[] orgIds)
    {
        await using var db = NewDb();
        foreach (var orgId in orgIds)
            db.OctorateConnections.Add(new OctorateConnection { OrgId = orgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected });

        await db.SaveChangesAsync();
    }

    /// <summary>The channel manager reports the guest's review of the stay as published, with an optional reply of the host already on the channel.</summary>
    public void ChannelHasReview(
        ReviewStay stay,
        string text = "Posto bellissimo, host gentilissimo.",
        decimal rating = 5m,
        string? hostReply = null,
        HostReviewWindow window = HostReviewWindow.Open)
    {
        Client
            .Setup(c => c.GetReviewAsync(stay.Accommodation, stay.Refer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReview(
                new OctorateGuestReview
                {
                    Published = true,
                    PublishedAt = Now.UtcDateTime.AddDays(-1),
                    Rating = rating,
                    Ratings = [new OctorateReviewRating("cleanliness", 5m), new OctorateReviewRating("communication", 4m)],
                    Text = text,
                    HostReply = hostReply,
                },
                new OctorateHostReviewInfo(window, null)));
    }

    /// <summary>The channel manager reports nothing for the stay yet: no review, no host review.</summary>
    public void ChannelHasNothing(ReviewStay stay) =>
        Client
            .Setup(c => c.GetReviewAsync(stay.Accommodation, stay.Refer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OctorateReview(null, null));

    /// <summary>Reads the stay from the channel manager (the sync of the job) and returns the row it made.</summary>
    public async Task<OtaReview> SyncAsync(ReviewStay stay)
    {
        await using (var db = NewDb())
        using (Scope.Use(stay.OrgId))
            await Service(db).SyncBookingAsync(stay.BookingId);

        return await ReviewOfAsync(stay);
    }

    public async Task<OtaReview> ReviewOfAsync(ReviewStay stay)
    {
        await using var db = NewDb();
        return await db.OtaReviews.AsNoTracking().SingleAsync(r => r.BookingId == stay.BookingId);
    }

    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        await using var db = NewDb();
        return await read(db);
    }
}
