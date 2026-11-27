using System.Buffers.Text;
using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.GuestPortal;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E50/F01/US01/T01 (NF-152, AC-NF152-1, AC-NF152-2, AC-NF152-5) on a real PostgreSQL database, what EF InMemory cannot prove: the partial unique index
/// that keeps one active link for each booking, under two writers at the same moment. A confirmed, checked-in or checked-out booking of any source gets
/// the same link every time it is asked; a request, a cancelled booking and a no-show get none; a revoked, regenerated or expired link and an unknown
/// token open nothing.
/// </summary>
public class GuestPortalLinkServiceTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly TimeProvider Clock = new FixedTimeProvider(HostScopeScenario.Now);

    private static readonly PublicSiteLinks Links =
        new(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.casazen.example" }));

    private PostgresTestDatabase? _database;
    private Guid _orgId;
    private Guid _propertyId;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync("guestportal");
        await using var db = _database.CreateContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Host del portale", $"portale-{suffix}");
        db.Orgs.Add(org);
        var property = HostScopeScenario.NewProperty(org.Id, $"auth0|titolare-{suffix}", "Trullo del Portale");
        db.Properties.Add(property);
        await db.SaveChangesAsync(Ct);
        (_orgId, _propertyId) = (org.Id, property.Id);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    /// <summary>
    /// Data rule (one link for each stay, any source): asked twice, a confirmed, checked-in or checked-out booking of the booking site, of a portal or
    /// entered by the host gets the same link; the token has 192 random bits and the address carries nothing but it.
    /// </summary>
    [PostgresFact]
    public async Task GetOrCreate_ConfirmedAnySource_ReturnsSameLinkTwice()
    {
        var cases = new[]
        {
            (BookingStatus.Confirmed, BookingSource.Direct),
            (BookingStatus.Confirmed, BookingSource.Airbnb),
            (BookingStatus.CheckedIn, BookingSource.BookingCom),
            (BookingStatus.CheckedOut, BookingSource.OtherChannel),
            (BookingStatus.Confirmed, BookingSource.Manual),
        };

        foreach (var (status, source) in cases)
        {
            var bookingId = await AddBookingAsync(status, source);

            GuestPortalLinkResult? first;
            string url;
            await using (var db = _database!.CreateContext())
            {
                first = await Service(db).GetOrCreateAsync(_orgId, bookingId, "auth0|titolare", Ct);
                url = Service(db).BuildUrl(first!.Link);
            }

            GuestPortalLinkResult? second;
            await using (var db = _database.CreateContext())
                second = await Service(db).GetOrCreateAsync(_orgId, bookingId, "auth0|titolare", Ct);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.True(first.Created, $"{status}/{source}: the first call makes the link");
            Assert.False(second.Created, $"{status}/{source}: the second call gets the same one");
            Assert.Equal(first.Link.Id, second.Link.Id);
            Assert.Equal(first.Link.Token, second.Link.Token);

            // 192 random bits in base64url: 32 characters that decode to 24 bytes.
            Assert.Equal(32, first.Link.Token.Length);
            Assert.Equal(GuestPortalLink.TokenBytes, Base64Url.DecodeFromChars(first.Link.Token).Length);
            Assert.Equal($"https://app.casazen.example/s/{first.Link.Token}", url);

            await using var verify = _database.CreateContext();
            Assert.Single(await verify.GuestPortalLinks.AsNoTracking().Where(l => l.BookingId == bookingId).ToListAsync(Ct));
        }
    }

    /// <summary>Data rule: a request, a cancelled booking and a no-show get no link, and nothing is written for them.</summary>
    [PostgresFact]
    public async Task GetOrCreate_PendingOrCancelled_ReturnsNull()
    {
        var pending = await AddBookingAsync(BookingStatus.Pending, BookingSource.Direct);
        var cancelled = await AddBookingAsync(BookingStatus.Cancelled, BookingSource.Airbnb);
        var noShow = await AddBookingAsync(BookingStatus.Confirmed, BookingSource.BookingCom, noShow: true);

        foreach (var bookingId in new[] { pending, cancelled, noShow })
        {
            await using var db = _database!.CreateContext();
            Assert.Null(await Service(db).GetOrCreateAsync(_orgId, bookingId, "auth0|titolare", Ct));
            Assert.Null(await Service(db).RegenerateAsync(_orgId, bookingId, "auth0|titolare", Ct));
        }

        await using var verify = _database!.CreateContext();
        Assert.False(await verify.GuestPortalLinks.AnyAsync(l => l.BookingId == pending || l.BookingId == cancelled || l.BookingId == noShow, Ct));
    }

    /// <summary>
    /// Data rule (one active link): eight requests that all find no link at the same moment end with one active link, and every caller gets that one.
    /// </summary>
    [PostgresFact]
    public async Task Create_Concurrent_LeavesOneActiveLink()
    {
        var bookingId = await AddBookingAsync(BookingStatus.Confirmed, BookingSource.Airbnb);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = _database!.CreateContext();
            return await Service(db).GetOrCreateAsync(_orgId, bookingId, createdByUserId: null, Ct);
        }));

        Assert.All(results, result => Assert.NotNull(result));
        Assert.Single(results.Select(r => r!.Link.Token).Distinct());
        Assert.Single(results, r => r!.Created);

        await using var verify = _database!.CreateContext();
        var rows = await verify.GuestPortalLinks.AsNoTracking().Where(l => l.BookingId == bookingId).ToListAsync(Ct);
        Assert.Single(rows);
        Assert.Null(rows[0].RevokedAt);
    }

    /// <summary>
    /// Privacy rule (AC-NF152-2): a regenerated link, a revoked one, an expired one and a token that never existed open nothing; the new link opens, and
    /// a departure moved later extends the link instead of letting it lapse.
    /// </summary>
    [PostgresFact]
    public async Task Resolve_RegeneratedRevokedExpiredOrUnknown_OpensNothing()
    {
        var bookingId = await AddBookingAsync(BookingStatus.Confirmed, BookingSource.Direct);
        string oldToken;
        string newToken;
        await using (var db = _database!.CreateContext())
        {
            oldToken = (await Service(db).GetOrCreateAsync(_orgId, bookingId, null, Ct))!.Link.Token;
            newToken = (await Service(db).RegenerateAsync(_orgId, bookingId, null, Ct))!.Token;
        }

        await using (var db = _database.CreateContext())
        {
            Assert.Null(await Service(db).ResolveAsync(oldToken, Ct));
            Assert.Null(await Service(db).ResolveAsync("ThisTokenNeverExisted_0000000000", Ct));
            Assert.Equal(newToken, (await Service(db).ResolveAsync(newToken, Ct))?.Token);
        }

        // 13 October + 31 days: on 13 November the link no longer opens.
        var afterTheEnd = new FixedTimeProvider(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        await using (var db = _database.CreateContext())
            Assert.Null(await Service(db, afterTheEnd).ResolveAsync(newToken, Ct));

        // The departure moves to 20 October: the link opens again until the end of 19 November, and the new expiry is stored.
        await using (var db = _database.CreateContext())
        {
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId, Ct);
            booking.CheckOutDate = HostScopeScenario.Day(2026, 10, 20);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = _database.CreateContext())
            Assert.Equal(newToken, (await Service(db, afterTheEnd).ResolveAsync(newToken, Ct))?.Token);
        await using (var db = _database.CreateContext())
        {
            var stored = await db.GuestPortalLinks.AsNoTracking().SingleAsync(l => l.Token == newToken, Ct);
            Assert.Equal(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), stored.ExpiresAt);
        }

        await using (var db = _database.CreateContext())
        {
            Assert.True(await Service(db).RevokeAsync(_orgId, bookingId, Ct));
            Assert.Null(await Service(db).ResolveAsync(newToken, Ct));
            Assert.Null(await Service(db).GetActiveAsync(_orgId, bookingId, Ct));
        }
    }

    private static GuestPortalLinkService Service(AppDbContext db, TimeProvider? clock = null) =>
        new(db, Links, clock ?? Clock, NullLogger<GuestPortalLinkService>.Instance);

    private async Task<Guid> AddBookingAsync(BookingStatus status, BookingSource source, bool noShow = false)
    {
        await using var db = _database!.CreateContext();
        var guest = new Guest { OrgId = _orgId, FirstName = "Anna", LastName = "Portale", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = _orgId,
            PropertyId = _propertyId,
            GuestId = guest.Id,
            CheckInDate = HostScopeScenario.Day(2026, 10, 10),
            CheckOutDate = HostScopeScenario.Day(2026, 10, 13),
            NumberOfGuests = 2,
            Status = status,
            Source = source,
            NoShowAt = noShow ? HostScopeScenario.Now.UtcDateTime : null,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(Ct);
        return booking.Id;
    }
}
