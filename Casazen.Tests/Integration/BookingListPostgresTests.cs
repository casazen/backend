using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PC-14 (A2-17) on real PostgreSQL through the whole pipeline: <c>GET /api/bookings</c> runs the same number of SQL
/// commands whatever the number of bookings (no query per booking, no property loaded with all its bookings), and the
/// TN-3 scope is applied in that query: the owner sees the bookings of the properties they own, an org-wide role the
/// whole org, nobody another org's.
/// </summary>
public class BookingListPostgresTests : IClassFixture<BookingListPostgresTests.CountingFactory>
{
    private const string Owner = "PropertyOwner";
    private const string Manager = "PropertyOwner,PropertyManager";
    private readonly CountingFactory _factory;

    public BookingListPostgresTests(CountingFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetAll_TenBookingsOnThreeProperties_RunsAsManyQueriesAsWithOneBooking()
    {
        var hostId = $"auth0|pc14-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        var first = await SeedPropertyAsync(org.Id, hostId);
        await SeedBookingAsync(first, 0);
        using var host = _factory.CreateAuthenticatedClient(hostId, Owner);
        // Warm-up: the first request of a user may provision caches (org context, onboarding) and is not measured.
        await GetIdsAsync(host, "/api/bookings");

        var (oneBooking, oneCount) = await CountQueriesAsync(host);

        var second = await SeedPropertyAsync(org.Id, hostId);
        var third = await SeedPropertyAsync(org.Id, hostId);
        for (var i = 1; i <= 3; i++)
            await SeedBookingAsync(first, i);
        for (var i = 0; i < 3; i++)
            await SeedBookingAsync(second, i);
        for (var i = 0; i < 3; i++)
            await SeedBookingAsync(third, i);

        var (tenBookings, tenCount) = await CountQueriesAsync(host);

        Assert.Single(oneBooking);
        Assert.Equal(10, tenBookings.Count);
        Assert.True(oneCount > 0, "The counter saw no SQL command for GET /api/bookings.");
        Assert.Equal(oneCount, tenCount);
    }

    [PostgresFact]
    public async Task GetAll_OwnerManagerAndOtherOrg_SeeOnlyTheBookingsOfTheirScope()
    {
        var ownerId = $"auth0|pc14-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        var colleagueId = $"auth0|pc14-colleague-{Guid.NewGuid():N}";
        var managerId = $"auth0|pc14-manager-{Guid.NewGuid():N}";
        await SeedUserInOrgAsync(colleagueId, org.Id);
        await SeedUserInOrgAsync(managerId, org.Id);
        var ownerProperty = await SeedPropertyAsync(org.Id, ownerId);
        var colleagueProperty = await SeedPropertyAsync(org.Id, colleagueId);
        var ownerBooking = await SeedBookingAsync(ownerProperty, 0);
        var colleagueBooking = await SeedBookingAsync(colleagueProperty, 0);

        var otherHostId = $"auth0|pc14-other-{Guid.NewGuid():N}";
        var otherOrg = await _factory.SeedOrgForOwnerAsync(otherHostId);
        var otherProperty = await SeedPropertyAsync(otherOrg.Id, otherHostId);
        var otherBooking = await SeedBookingAsync(otherProperty, 0);

        using var owner = _factory.CreateAuthenticatedClient(ownerId, Owner);
        using var colleague = _factory.CreateAuthenticatedClient(colleagueId, Owner);
        using var manager = _factory.CreateAuthenticatedClient(managerId, Manager);
        using var other = _factory.CreateAuthenticatedClient(otherHostId, Owner);

        // Owner role: only the properties the caller owns, even inside the same org.
        Assert.Equal([ownerBooking.Id], await GetIdsAsync(owner, "/api/bookings"));
        Assert.Equal([colleagueBooking.Id], await GetIdsAsync(colleague, "/api/bookings"));
        // Org-wide role: the whole org, never another org.
        Assert.Equal(
            new[] { ownerBooking.Id, colleagueBooking.Id }.Order(),
            (await GetIdsAsync(manager, "/api/bookings")).Order());
        Assert.Equal([otherBooking.Id], await GetIdsAsync(other, "/api/bookings"));

        // guestId narrows inside the scope: the guest of a colleague's booking gives nothing to the owner.
        Assert.Empty(await GetIdsAsync(owner, $"/api/bookings?guestId={colleagueBooking.GuestId}"));
        Assert.Equal(
            [colleagueBooking.Id],
            await GetIdsAsync(manager, $"/api/bookings?guestId={colleagueBooking.GuestId}"));
        Assert.Equal(
            [colleagueBooking.Id],
            await GetIdsAsync(manager, $"/api/bookings?propertyId={colleagueProperty.Id}"));

        // propertyId: another member's property is 403, another org's is 404 (TN-3).
        var colleagues = await owner.GetAsync($"/api/bookings?propertyId={colleagueProperty.Id}");
        var otherOrgs = await owner.GetAsync($"/api/bookings?propertyId={otherProperty.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, colleagues.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherOrgs.StatusCode);
    }

    [PostgresFact]
    public async Task GetAll_Dto_CarriesPropertyNameAndGuestWithLatestCheckInFirst()
    {
        var hostId = $"auth0|pc14-dto-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        var property = await SeedPropertyAsync(org.Id, hostId);
        var earlier = await SeedBookingAsync(property, 0);
        var later = await SeedBookingAsync(property, 5);
        using var host = _factory.CreateAuthenticatedClient(hostId, Owner);

        var body = await host.GetFromJsonAsync<JsonElement>("/api/bookings");

        var items = body.EnumerateArray().ToList();
        Assert.Equal([later.Id, earlier.Id], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.All(items, i => Assert.Equal(property.Name, i.GetProperty("propertyName").GetString()));
        Assert.Equal("Anna", items[0].GetProperty("guest").GetProperty("firstName").GetString());
    }

    private async Task<(IReadOnlyList<Guid> Ids, int Commands)> CountQueriesAsync(HttpClient client)
    {
        _factory.Counter.Reset();
        var ids = await GetIdsAsync(client, "/api/bookings");
        return (ids, _factory.Counter.Count);
    }

    private static async Task<IReadOnlyList<Guid>> GetIdsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToList();
    }

    private async Task<Property> SeedPropertyAsync(Guid orgId, string ownerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = orgId,
            Name = $"PC-14 {Guid.NewGuid():N}"[..20],
            Address = $"Via PC14 {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    /// <summary>A confirmed booking with its own guest snapshot, <paramref name="weeksAhead"/> weeks from next year's June.</summary>
    private async Task<Booking> SeedBookingAsync(Property property, int weeksAhead)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var checkIn = new DateTime(DateTime.UtcNow.Year + 1, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(7 * weeksAhead);
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"pc14-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(3),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AddRange(guest, booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private async Task SeedUserInOrgAsync(string userId, Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Host",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }

    /// <summary>The test host with <see cref="Counter"/> on every <see cref="AppDbContext"/>.</summary>
    public sealed class CountingFactory : CasazenWebApplicationFactory
    {
        internal BookingListCommandCounter Counter { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Counter);
                services.ConfigureDbContext<AppDbContext>((sp, options) =>
                {
                    Counter.Accessor ??= sp.GetRequiredService<IHttpContextAccessor>();
                    options.AddInterceptors(Counter);
                });
            });
        }
    }

    /// <summary>Counts the SQL commands run while serving <c>GET /api/bookings</c> (requests of this class run one at a time).</summary>
    internal sealed class BookingListCommandCounter : DbCommandInterceptor
    {
        private int _count;

        public IHttpContextAccessor? Accessor { get; set; }

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Track();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Track();
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Track();
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Track();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Track()
        {
            var request = Accessor?.HttpContext?.Request;
            if (request is not null
                && HttpMethods.IsGet(request.Method)
                && request.Path.Equals("/api/bookings", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _count);
            }
        }
    }
}
