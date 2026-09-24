using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-1 (A9-01, A2-02, A5-07, A2-35, A9-11) over HTTP on the FD-04 PostgreSQL factory: two orgs hold a guest
/// with the same e-mail; neither org can read, list, change, delete or erase the other's guest, the public
/// check-in prefill of one org never shows the other's data, and guest deletion never fails on the FK.
/// </summary>
public class GuestTenantIsolationIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string OwnerRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;

    public GuestTenantIsolationIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetAll_TwoOrgsWithSameGuestEmail_ListsOnlyCallerOrgGuestsAsSummaries()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();

        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);
        using var listB = await ReadJsonAsync(await clientB.GetAsync("/api/guests"));
        var itemsB = listB.RootElement.GetProperty("items").EnumerateArray().ToList();
        var idsB = itemsB.Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([s.GuestB], idsB);
        Assert.Equal(1, listB.RootElement.GetProperty("totalCount").GetInt32());
        Assert.False(itemsB[0].TryGetProperty("documentNumber", out _));
        Assert.False(itemsB[0].TryGetProperty("dateOfBirth", out _));

        using var clientA = _factory.CreateAuthenticatedClient(s.OwnerA, OwnerRole);
        using var listA = await ReadJsonAsync(await clientA.GetAsync($"/api/guests?search={Uri.EscapeDataString(s.Email)}"));
        var idsA = listA.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid());
        Assert.Equal([s.GuestA], idsA);
    }

    [Fact]
    public async Task GetAll_WithPageSize_ReturnsRequestedPageAndTotal()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        for (var i = 0; i < 3; i++)
            await AddGuestAsync(property.OrgId, $"page{i}.{Guid.NewGuid():N}@example.com", createdAt: DateTime.UtcNow.AddMinutes(i));

        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);
        using var page1 = await ReadJsonAsync(await client.GetAsync("/api/guests?page=1&pageSize=2"));
        using var page2 = await ReadJsonAsync(await client.GetAsync("/api/guests?page=2&pageSize=2"));

        Assert.Equal(3, page1.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page1.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, page1.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page2.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetById_GuestOfOtherOrg_Returns404GuestNotFound_AndOwnGuestIsDto()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();

        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);
        var leaked = await clientB.GetAsync($"/api/guests/{s.GuestA}");
        await AssertGuestNotFoundAsync(leaked);

        var byEmail = await clientB.GetAsync($"/api/guests/email/{Uri.EscapeDataString(s.Email)}");
        using (var byEmailDoc = await ReadJsonAsync(byEmail))
            Assert.Equal(s.GuestB, byEmailDoc.RootElement.GetProperty("id").GetGuid());

        using var clientA = _factory.CreateAuthenticatedClient(s.OwnerA, OwnerRole);
        using var own = await ReadJsonAsync(await clientA.GetAsync($"/api/guests/{s.GuestA}"));
        var root = own.RootElement;
        Assert.Equal("AA1111111", root.GetProperty("documentNumber").GetString());
        Assert.True(root.GetProperty("hasDocumentScan").GetBoolean());
        foreach (var hidden in new[] { "bookings", "alloggiatiWebReports", "orgId", "org", "documentScanUrl", "consentIpAddress" })
            Assert.False(root.TryGetProperty(hidden, out _), $"GuestDto must not expose '{hidden}'");
    }

    [Fact]
    public async Task Update_GuestOfOtherOrg_Returns404AndKeepsData()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var response = await clientB.PutAsJsonAsync($"/api/guests/{s.GuestA}", new
        {
            firstName = "Hijacked",
            lastName = "ByB",
            email = s.Email,
        });

        await AssertGuestNotFoundAsync(response);
        var guestA = await LoadGuestAsync(s.GuestA);
        Assert.Equal("Mario", guestA.FirstName);
        Assert.Equal("Note of org A", guestA.Notes);
    }

    [Fact]
    public async Task Update_OwnGuest_ReturnsUpdatedDto()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var response = await clientB.PutAsJsonAsync($"/api/guests/{s.GuestB}", new
        {
            firstName = "Mario",
            lastName = "Rossi",
            email = s.Email,
            city = "Torino",
        });

        using var doc = await ReadJsonAsync(response);
        Assert.Equal("Torino", doc.RootElement.GetProperty("city").GetString());
        Assert.Equal(string.Empty, (await LoadGuestAsync(s.GuestA)).City);
    }

    [Fact]
    public async Task Delete_GuestOfOtherOrg_Returns404AndKeepsGuest()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var response = await clientB.DeleteAsync($"/api/guests/{s.GuestA}");

        await AssertGuestNotFoundAsync(response);
        var guestA = await LoadGuestAsync(s.GuestA);
        Assert.False(guestA.IsDeleted);
        Assert.Equal("Mario", guestA.FirstName);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("delete")]
    [InlineData("anonymize")]
    [InlineData("consent")]
    public async Task GdprEndpoint_GuestOfOtherOrg_Returns404AndKeepsData(string operation)
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var response = await SendGdprAsync(clientB, operation, s.GuestA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var guestA = await LoadGuestAsync(s.GuestA);
        Assert.Equal("Mario", guestA.FirstName);
        Assert.Equal("AA1111111", guestA.DocumentNumber);
        Assert.False(guestA.IsDeleted);
        Assert.False(guestA.MarketingConsent);
    }

    [Theory]
    [InlineData("export", HttpStatusCode.OK)]
    [InlineData("anonymize", HttpStatusCode.NoContent)]
    [InlineData("consent", HttpStatusCode.NoContent)]
    public async Task GdprEndpoint_OwnGuest_SucceedsOnlyOnCallerGuest(string operation, HttpStatusCode expected)
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        using var clientB = _factory.CreateAuthenticatedClient(s.OwnerB, OwnerRole);

        var response = await SendGdprAsync(clientB, operation, s.GuestB);

        Assert.Equal(expected, response.StatusCode);
        var guestA = await LoadGuestAsync(s.GuestA);
        Assert.Equal("Mario", guestA.FirstName);
        Assert.False(guestA.MarketingConsent);
    }

    [Fact]
    public async Task Create_EmailOfOtherOrgGuest_CreatesOwnGuest_AndSameOrgDuplicateIs409()
    {
        var ownerA = NewOwner();
        var ownerB = NewOwner();
        var propertyA = await _factory.SeedPropertyAsync(ownerA);
        var orgB = await _factory.SeedOrgForOwnerAsync(ownerB);
        var email = $"shared.{Guid.NewGuid():N}@example.com";
        await AddGuestAsync(propertyA.OrgId, email);
        using var clientB = _factory.CreateAuthenticatedClient(ownerB, OwnerRole);
        var body = new { firstName = "Mario", lastName = "Rossi", email };

        var created = await clientB.PostAsJsonAsync("/api/guests", body);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var doc = await ReadJsonAsync(created))
        {
            var createdId = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(orgB.Id, (await LoadGuestAsync(createdId)).OrgId);
        }

        var duplicate = await clientB.PostAsJsonAsync("/api/guests", body);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var problem = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
        Assert.Equal("guest_email_exists", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(email, problem.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckInPrefill_BookingOfSecondOrgWithSameEmail_ShowsOnlyItsOwnGuestData()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        string token;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            token = await new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance)
                .CreateSessionAsync(s.BookingB, s.OrgB);
        }

        using var anonymous = _factory.CreateClient();
        using var doc = await ReadJsonAsync(await anonymous.GetAsync($"/api/public/checkin/{token}"));

        // CO-12: the prefill is the list of the stay's guests; before any registration, the booking's own booker.
        var prefill = Assert.Single(doc.RootElement.GetProperty("guests").EnumerateArray());
        Assert.Equal("Mario", prefill.GetProperty("firstName").GetString());
        Assert.Equal("Bianchi", prefill.GetProperty("lastName").GetString());
        Assert.Equal(JsonValueKind.Null, prefill.GetProperty("dateOfBirth").ValueKind);
        Assert.False(prefill.TryGetProperty("documentNumber", out _));
        Assert.Equal(JsonValueKind.Null, prefill.GetProperty("documentNumberMasked").ValueKind);
        Assert.Equal(string.Empty, prefill.GetProperty("birthComuneName").GetString());
    }

    [Fact]
    public async Task Delete_GuestWithOpenBooking_Returns409GuestHasOpenBookings()
    {
        var s = await SeedTwoOrgsWithSameGuestEmailAsync();
        using var clientA = _factory.CreateAuthenticatedClient(s.OwnerA, OwnerRole);

        var response = await clientA.DeleteAsync($"/api/guests/{s.GuestA}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("guest_has_open_bookings", problem.RootElement.GetProperty("code").GetString());
        Assert.False((await LoadGuestAsync(s.GuestA)).IsDeleted);
    }

    [Fact]
    public async Task Delete_GuestWithoutBookings_Returns204AndRemovesRow()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        var guestId = await AddGuestAsync(property.OrgId, $"lonely.{Guid.NewGuid():N}@example.com");
        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);

        var response = await client.DeleteAsync($"/api/guests/{guestId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Guests.AnyAsync(g => g.Id == guestId));
    }

    [Fact]
    public async Task Delete_GuestWithPastBookingAndReport_Returns204SoftDeletesAnonymizesAndHidesFromList()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        var email = $"past.{Guid.NewGuid():N}@example.com";
        var guestId = await AddGuestAsync(property.OrgId, email, documentNumber: "PP7654321");
        var bookingId = await AddBookingAsync(property, guestId, BookingStatus.CheckedOut, daysFromToday: -10);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
            {
                BookingId = bookingId,
                GuestId = guestId,
                OrgId = property.OrgId,
                Status = AlloggiatiWebStatus.InviatoManualmente,
                ReportedAt = DateTime.UtcNow.Date.AddDays(-10),
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);
        var response = await client.DeleteAsync($"/api/guests/{guestId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = await LoadGuestAsync(guestId);
        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedAt);
        Assert.Equal("ANONYMIZED", stored.FirstName);
        Assert.Equal(string.Empty, stored.DocumentNumber);
        Assert.DoesNotContain(email, stored.Email, StringComparison.OrdinalIgnoreCase);

        using var list = await ReadJsonAsync(await client.GetAsync("/api/guests"));
        var ids = list.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid());
        Assert.DoesNotContain(guestId, ids);
    }

    private sealed record TwoOrgs(
        string OwnerA,
        string OwnerB,
        Guid OrgA,
        Guid OrgB,
        Guid GuestA,
        Guid GuestB,
        Guid BookingB,
        string Email);

    /// <summary>
    /// Org A registers Mario (document, date of birth, notes) with a booking; org B then creates a host booking
    /// for the same e-mail through the API, which must produce B's own guest record.
    /// </summary>
    private async Task<TwoOrgs> SeedTwoOrgsWithSameGuestEmailAsync()
    {
        var ownerA = NewOwner();
        var ownerB = NewOwner();
        var propertyA = await _factory.SeedPropertyAsync(ownerA);
        var propertyB = await _factory.SeedPropertyAsync(ownerB);
        var email = $"mario.{Guid.NewGuid():N}@example.com";

        var guestA = await AddGuestAsync(propertyA.OrgId, email, documentNumber: "AA1111111", withPrivateData: true);
        await AddBookingAsync(propertyA, guestA, BookingStatus.Confirmed, daysFromToday: 5);

        using var clientB = _factory.CreateAuthenticatedClient(ownerB, OwnerRole);
        var create = await clientB.PostAsJsonAsync("/api/bookings", new
        {
            propertyId = propertyB.Id,
            checkInDate = DateTime.UtcNow.Date.AddDays(20),
            checkOutDate = DateTime.UtcNow.Date.AddDays(22),
            numberOfGuests = 2,
            guest = new
            {
                firstName = "Mario",
                lastName = "Bianchi",
                email,
                phone = "+393331234567",
                country = "Italia",
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var bookingB = created.RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guestB = await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingB).Select(b => b.GuestId).SingleAsync();
        Assert.NotEqual(guestA, guestB);
        Assert.Equal(propertyB.OrgId, await db.Guests.Where(g => g.Id == guestB).Select(g => g.OrgId).SingleAsync());

        return new TwoOrgs(ownerA, ownerB, propertyA.OrgId, propertyB.OrgId, guestA, guestB, bookingB, email);
    }

    private async Task<Guid> AddGuestAsync(
        Guid orgId,
        string email,
        string documentNumber = "",
        bool withPrivateData = false,
        DateTime? createdAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = orgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = email,
            DocumentNumber = documentNumber,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        if (withPrivateData)
        {
            guest.DateOfBirth = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            guest.PlaceOfBirth = "Milano";
            guest.DocumentType = GuestDocumentType.IdentityCard;
            guest.DocumentScanUrl = $"guest-documents/{orgId:N}/scan.pdf";
            guest.Notes = "Note of org A";
        }

        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return guest.Id;
    }

    private async Task<Guid> AddBookingAsync(Property property, Guid guestId, BookingStatus status, int daysFromToday)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = new Booking
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            GuestId = guestId,
            CheckInDate = DateTime.UtcNow.Date.AddDays(daysFromToday),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(daysFromToday + 2),
            NumberOfGuests = 2,
            Status = status,
            Source = BookingSource.Direct,
            BasePrice = 200m,
            TotalPrice = 200m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Guest> LoadGuestAsync(Guid guestId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Guests.AsNoTracking().SingleAsync(g => g.Id == guestId);
    }

    private static Task<HttpResponseMessage> SendGdprAsync(HttpClient client, string operation, Guid guestId) => operation switch
    {
        "export" => client.GetAsync($"/api/gdpr/guests/{guestId}/export"),
        "delete" => client.DeleteAsync($"/api/gdpr/guests/{guestId}?reason=test"),
        "anonymize" => client.PostAsync($"/api/gdpr/guests/{guestId}/anonymize", null),
        _ => client.PutAsJsonAsync($"/api/gdpr/guests/{guestId}/consent", new { marketingConsent = true }),
    };

    private static async Task AssertGuestNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("guest_not_found", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static string NewOwner() => $"auth0|tn1-{Guid.NewGuid():N}";
}
