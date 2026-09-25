using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-08 (A4-14) over the real pipeline on PostgreSQL: the supplier sees comune, zone, date and stay dates of a request;
/// street address and host contact only once it took the request (GDPR minimization); never the guest of the stay. The
/// detail of another supplier's request is 404; the inbox history is paginated server-side with status and period
/// filters (Europe/Rome days).
/// </summary>
public class SupplierInboxPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Host = "PropertyOwner";
    private const string Supplier = "Supplier";
    /// <summary>Street of the property; each world adds a suffix (properties are unique by address).</summary>
    private const string Street = "Via dei Fori Imperiali 12, interno";
    private const string PostalCode = "00184";
    private const string HostDisplayName = "Villa Rosa Affitti";
    private const string HostEmail = "host-su08@example.com";
    private const string OwnerPhone = "+39 333 1112223";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Stay of the requests: check-in 9 October 2026, check-out 12 October 2026 (stored as UTC midnight).</summary>
    private static readonly DateTime CheckIn = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOut = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Guest data that must never reach the supplier.</summary>
    private static readonly string[] GuestData =
    [
        "Giulietta",
        "Capuleti",
        "giulietta.capuleti",
        "+39 347 9990001",
        "AY7654321",
        "Via dell'Ospite 77",
        "Verona",
    ];

    private readonly CasazenWebApplicationFactory _factory;

    public SupplierInboxPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task GetInboxItem_BeforeTake_ShowsComuneZoneAndDateButNoAddressNorHostContact()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateRequestAsync(host, w, w.SupplierA.OrgId);

        var (detail, detailText) = await GetDetailAsync(supplier, id);
        var (inbox, inboxText) = await GetInboxAsync(supplier, "");

        Assert.Equal("Richiesto", detail.GetProperty("status").GetString());
        Assert.Equal("Roma", detail.GetProperty("city").GetString());
        Assert.Equal(PostalCode, detail.GetProperty("postalCode").GetString());
        Assert.Equal("Casa SU08", detail.GetProperty("propertyName").GetString());
        Assert.Equal("2026-10-12", detail.GetProperty("scheduledFor").GetString());
        Assert.Equal("2026-10-09", detail.GetProperty("stay").GetProperty("checkIn").GetString());
        Assert.Equal("2026-10-12", detail.GetProperty("stay").GetProperty("checkOut").GetString());
        Assert.Equal(w.BookingId, detail.GetProperty("stay").GetProperty("bookingId").GetGuid());
        Assert.False(detail.GetProperty("contactDisclosed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("address").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("hostContact").ValueKind);

        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("2026-10-12", item.GetProperty("scheduledFor").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("address").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("hostContact").ValueKind);

        foreach (var text in new[] { detailText, inboxText })
        {
            Assert.DoesNotContain(Street, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(HostEmail, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(OwnerPhone, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(HostDisplayName, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgresFact]
    public async Task GetInboxItem_AfterTake_ShowsAddressHostContactAndWhoTookIt()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateRequestAsync(host, w, w.SupplierA.OrgId);

        await AssertStatusAsync(HttpStatusCode.OK, await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }));
        var (detail, _) = await GetDetailAsync(supplier, id);
        var (inbox, _) = await GetInboxAsync(supplier, "");

        Assert.Equal("PresoInCarico", detail.GetProperty("status").GetString());
        Assert.True(detail.GetProperty("contactDisclosed").GetBoolean());
        Assert.Equal(w.Address, detail.GetProperty("address").GetString());
        Assert.Equal("Roma", detail.GetProperty("city").GetString());
        var contact = detail.GetProperty("hostContact");
        Assert.Equal(HostDisplayName, contact.GetProperty("name").GetString());
        Assert.Equal(HostEmail, contact.GetProperty("email").GetString());
        Assert.Equal(OwnerPhone, contact.GetProperty("phone").GetString());
        Assert.Equal("2026-10-12", detail.GetProperty("scheduledFor").GetString());

        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("Richiesto", history[0].GetProperty("status").GetString());
        Assert.Equal("Host", history[0].GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("actorName").ValueKind);
        Assert.Equal("PresoInCarico", history[1].GetProperty("status").GetString());
        Assert.Equal("Supplier", history[1].GetProperty("actor").GetString());
        Assert.Equal("Alfa Fornitore", history[1].GetProperty("actorName").GetString());
        Assert.Equal(detail.GetProperty("takenAt").GetDateTime(), history[1].GetProperty("at").GetDateTime());

        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray());
        Assert.Equal(w.Address, item.GetProperty("address").GetString());
        Assert.Equal(HostEmail, item.GetProperty("hostContact").GetProperty("email").GetString());
        Assert.False(item.TryGetProperty("history", out _));
    }

    [PostgresFact]
    public async Task GetInboxItem_RejectedWithReason_NeverShowsContactAndHistoryHasTheReason()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateRequestAsync(host, w, w.SupplierA.OrgId);

        await AssertStatusAsync(
            HttpStatusCode.OK,
            await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non disponibile quel giorno" }));
        var (detail, text) = await GetDetailAsync(supplier, id);

        Assert.Equal("Rifiutato", detail.GetProperty("status").GetString());
        Assert.False(detail.GetProperty("contactDisclosed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("hostContact").ValueKind);
        Assert.DoesNotContain(Street, text, StringComparison.OrdinalIgnoreCase);
        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(new[] { "Richiesto", "Rifiutato" }, history.Select(h => h.GetProperty("status").GetString()));
        Assert.Equal("Supplier", history[1].GetProperty("actor").GetString());
        Assert.Equal("Non disponibile quel giorno", history[1].GetProperty("reason").GetString());
    }

    [PostgresFact]
    public async Task SupplierInbox_EveryStatusListAndDetail_NeverContainsGuestPersonalData()
    {
        var w = await SeedWorldAsync();
        var ids = await AddRequestsAsync(
            w,
            Seed(w.SupplierA.OrgId, ServiceRequestStatus.Richiesto),
            Seed(w.SupplierA.OrgId, ServiceRequestStatus.PresoInCarico),
            Seed(w.SupplierA.OrgId, ServiceRequestStatus.InCorso),
            Seed(w.SupplierA.OrgId, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 10, 9)),
            Seed(w.SupplierA.OrgId, ServiceRequestStatus.Pagato, completedAt: Utc(2026, 9, 11, 9)),
            Seed(w.SupplierA.OrgId, ServiceRequestStatus.Rifiutato, updatedAt: Utc(2026, 9, 12, 9)));
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);

        var texts = new List<string>();
        foreach (var status in new[] { "all", "open", "history" })
            texts.Add((await GetInboxAsync(supplier, $"status={status}&pageSize=100")).Text);
        foreach (var id in ids)
            texts.Add((await GetDetailAsync(supplier, id)).Text);

        var (all, _) = await GetInboxAsync(supplier, "status=all&pageSize=100");
        Assert.Equal(ids.Count, all.GetProperty("total").GetInt32());
        Assert.All(texts, text =>
        {
            foreach (var value in GuestData)
                Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"guest", text, StringComparison.OrdinalIgnoreCase);
        });
        // The stay is there, by its dates only.
        Assert.All(all.GetProperty("items").EnumerateArray(), item =>
        {
            var stay = item.GetProperty("stay");
            Assert.Equal(new[] { "bookingId", "checkIn", "checkOut" }, stay.EnumerateObject().Select(p => p.Name));
        });
    }

    [PostgresFact]
    public async Task GetInboxItem_AnotherSupplierRequest_Returns404AndIsNotInTheInbox()
    {
        var w = await SeedWorldAsync();
        using var host = _factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplierA = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        using var supplierB = _factory.CreateAuthenticatedClient(w.SupplierB.UserId, Supplier);
        var forB = await CreateRequestAsync(host, w, w.SupplierB.OrgId);
        await AssertStatusAsync(HttpStatusCode.OK, await supplierB.PostAsJsonAsync($"/api/service-requests/{forB}/take", new { }));

        var otherSupplier = await supplierA.GetAsync($"/api/supplier/inbox/{forB}");
        var missing = await supplierA.GetAsync($"/api/supplier/inbox/{Guid.NewGuid()}");
        var (inboxA, inboxAText) = await GetInboxAsync(supplierA, "status=all");

        foreach (var response in new[] { otherSupplier, missing })
        {
            var text = await AssertStatusAsync(HttpStatusCode.NotFound, response);
            Assert.Equal("service_request_not_found", JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("code").GetString());
            Assert.DoesNotContain(Street, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0, inboxA.GetProperty("total").GetInt32());
        Assert.DoesNotContain(forB.ToString(), inboxAText, StringComparison.OrdinalIgnoreCase);
        // The supplier it was sent to sees it.
        var (detailB, _) = await GetDetailAsync(supplierB, forB);
        Assert.Equal(w.Address, detailB.GetProperty("address").GetString());
    }

    [PostgresFact]
    public async Task GetInbox_History_PaginatesServerSideAndFiltersByStatusAndRomePeriod()
    {
        var w = await SeedWorldAsync();
        var supplierOrg = w.SupplierA.OrgId;
        var seeds = new List<RequestSeed>();
        // Twelve completed in September 2026 (Rome), one per day from the 2nd: newest first is the 13th.
        for (var day = 2; day <= 13; day++)
            seeds.Add(Seed(supplierOrg, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, day, 9)));
        seeds.AddRange(
        [
            // 1 September 00:30 in Rome, still 31 August in UTC: September.
            Seed(supplierOrg, ServiceRequestStatus.Pagato, completedAt: new DateTime(2026, 8, 31, 22, 30, 0, DateTimeKind.Utc)),
            // 31 August 23:30 in Rome: August.
            Seed(supplierOrg, ServiceRequestStatus.Pagato, completedAt: new DateTime(2026, 8, 31, 21, 30, 0, DateTimeKind.Utc)),
            Seed(supplierOrg, ServiceRequestStatus.Rifiutato, updatedAt: Utc(2026, 9, 20, 9)),
            Seed(supplierOrg, ServiceRequestStatus.Rifiutato, updatedAt: Utc(2026, 8, 5, 9)),
            // Open work is not history.
            Seed(supplierOrg, ServiceRequestStatus.Richiesto),
            Seed(supplierOrg, ServiceRequestStatus.PresoInCarico),
            // Another supplier's history never shows.
            Seed(w.SupplierB.OrgId, ServiceRequestStatus.Completato, completedAt: Utc(2026, 9, 14, 9)),
        ]);
        await AddRequestsAsync(w, [.. seeds]);
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        const string september = "from=2026-09-01&to=2026-09-30";

        var page1 = (await GetInboxAsync(supplier, $"status=history&{september}&page=1&pageSize=5")).Body;
        var page3 = (await GetInboxAsync(supplier, $"status=history&{september}&page=3&pageSize=5")).Body;
        var rejected = (await GetInboxAsync(supplier, $"status=Rifiutato&{september}")).Body;
        var paidInAugust = (await GetInboxAsync(supplier, "status=pagato&from=2026-08-01&to=2026-08-31")).Body;
        var allHistory = (await GetInboxAsync(supplier, "status=history&pageSize=100")).Body;
        var open = (await GetInboxAsync(supplier, "")).Body;

        // 12 completed + 1 paid (Rome 1 September) + 1 rejected in September.
        Assert.Equal(14, page1.GetProperty("total").GetInt32());
        Assert.Equal(1, page1.GetProperty("page").GetInt32());
        Assert.Equal(5, page1.GetProperty("pageSize").GetInt32());
        var first = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, first.Count);
        Assert.Equal("Rifiutato", first[0].GetProperty("status").GetString());
        Assert.Equal(Utc(2026, 9, 13, 9), first[1].GetProperty("completedAt").GetDateTime().ToUniversalTime());
        var last = page3.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, last.Count);
        Assert.Equal("Pagato", last[^1].GetProperty("status").GetString());

        var rejectedItem = Assert.Single(rejected.GetProperty("items").EnumerateArray());
        Assert.Equal("Rifiutato", rejectedItem.GetProperty("status").GetString());
        var paidItem = Assert.Single(paidInAugust.GetProperty("items").EnumerateArray());
        Assert.Equal(new DateTime(2026, 8, 31, 21, 30, 0, DateTimeKind.Utc), paidItem.GetProperty("completedAt").GetDateTime().ToUniversalTime());

        Assert.Equal(16, allHistory.GetProperty("total").GetInt32());
        Assert.All(allHistory.GetProperty("items").EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("status").GetString(), new[] { "Completato", "Pagato", "Rifiutato" }));
        Assert.Equal(2, open.GetProperty("total").GetInt32());
        Assert.All(open.GetProperty("items").EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("status").GetString(), new[] { "Richiesto", "PresoInCarico" }));
    }

    [PostgresTheory]
    [InlineData("status=unknown")]
    [InlineData("status=3")]
    [InlineData("from=2026-09-30&to=2026-09-01")]
    public async Task GetInbox_InvalidStatusOrPeriod_Returns400ValidationError(string query)
    {
        var w = await SeedWorldAsync();
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);

        var text = await AssertStatusAsync(HttpStatusCode.BadRequest, await supplier.GetAsync($"/api/supplier/inbox?{query}"));

        Assert.Equal("validation_error", JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task GetInboxItem_LongRentRequest_HasNoStayNorDate()
    {
        var w = await SeedWorldAsync();
        var ids = await AddRequestsAsync(w, Seed(w.SupplierA.OrgId, ServiceRequestStatus.PresoInCarico, longRent: true));
        using var supplier = _factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);

        var (detail, _) = await GetDetailAsync(supplier, ids[0]);

        Assert.Equal("LongRent", detail.GetProperty("rentalContext").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("scheduledFor").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("stay").ValueKind);
        Assert.Equal(w.Address, detail.GetProperty("address").GetString());
    }

    // ─── Helpers ───

    private static async Task<(JsonElement Body, string Text)> GetDetailAsync(HttpClient client, Guid id)
    {
        var text = await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync($"/api/supplier/inbox/{id}"));
        return (JsonSerializer.Deserialize<JsonElement>(text, JsonOptions), text);
    }

    private static async Task<(JsonElement Body, string Text)> GetInboxAsync(HttpClient client, string query)
    {
        var url = string.IsNullOrEmpty(query) ? "/api/supplier/inbox" : $"/api/supplier/inbox?{query}";
        var text = await AssertStatusAsync(HttpStatusCode.OK, await client.GetAsync(url));
        return (JsonSerializer.Deserialize<JsonElement>(text, JsonOptions), text);
    }

    private static async Task<string> AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text;
    }

    private static async Task<Guid> CreateRequestAsync(HttpClient host, World w, Guid supplierOrgId)
    {
        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId,
            category = "cleaning",
            notes = "Cambio biancheria per 4 persone",
        });
        var text = await AssertStatusAsync(HttpStatusCode.Created, response);
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("id").GetGuid();
    }

    private static DateTime Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static RequestSeed Seed(
        Guid supplierOrgId,
        ServiceRequestStatus status,
        DateTime? completedAt = null,
        DateTime? updatedAt = null,
        bool longRent = false) => new(supplierOrgId, status, completedAt, updatedAt, longRent);

    private async Task<List<Guid>> AddRequestsAsync(World w, params RequestSeed[] seeds)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var created = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
        var ids = new List<Guid>();
        foreach (var seed in seeds)
        {
            var taken = seed.Status is not (ServiceRequestStatus.Richiesto or ServiceRequestStatus.Rifiutato);
            var request = new ServiceRequest
            {
                OrgId = w.HostOrgId,
                PropertyId = w.PropertyId,
                BookingId = seed.LongRent ? null : w.BookingId,
                RentalContext = seed.LongRent ? ServiceRequestRentalContext.LongRent : ServiceRequestRentalContext.ShortRent,
                SupplierOrgId = seed.SupplierOrgId,
                Category = "cleaning",
                Status = seed.Status,
                TakenAt = taken ? created.AddHours(1) : null,
                TakenByUserId = taken ? w.SupplierA.UserId : null,
                CompletedAt = seed.CompletedAt,
                PaidAt = seed.Status == ServiceRequestStatus.Pagato ? seed.CompletedAt?.AddDays(1) : null,
                RejectionReason = seed.Status == ServiceRequestStatus.Rifiutato ? "Non disponibile" : null,
                CreatedAt = created,
                UpdatedAt = seed.UpdatedAt ?? seed.CompletedAt ?? created,
            };
            db.ServiceRequests.Add(request);
            ids.Add(request.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<World> SeedWorldAsync()
    {
        var ownerId = $"auth0|su08-owner-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var hostOrg = await db.Orgs.IgnoreQueryFilters().FirstAsync(o => o.Id == org.Id);
        hostOrg.DisplayName = HostDisplayName;
        hostOrg.ContactEmail = HostEmail;
        var owner = await db.Users.FirstAsync(u => u.Id == ownerId);
        owner.PhoneNumber = OwnerPhone;

        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa SU08",
            Address = $"{Street} {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = PostalCode,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Giulietta",
            LastName = "Capuleti",
            Email = $"giulietta.capuleti-{Guid.NewGuid():N}@example.com",
            PhoneNumber = "+39 347 9990001",
            Address = "Via dell'Ospite 77",
            City = "Verona",
            DocumentType = GuestDocumentType.IdentityCard,
            DocumentNumber = "AY7654321",
        };
        var stay = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = CheckIn,
            CheckOutDate = CheckOut,
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
            SpecialRequests = "Giulietta Capuleti arriva tardi",
        };
        db.AddRange(property, guest, stay);

        SupplierSeed AddSupplier(string name)
        {
            var supplierOrg = new OrgEntity
            {
                Name = $"SU08 {name}",
                Slug = $"su08-{name}-{Guid.NewGuid():N}"[..25],
                DisplayName = $"SU08 {name}",
                ContactEmail = $"su08-{name}-{Guid.NewGuid():N}@example.com",
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
            };
            var member = new User
            {
                Id = $"auth0|su08-{name}-{Guid.NewGuid():N}",
                Email = $"{name}-{Guid.NewGuid():N}@example.com",
                FirstName = char.ToUpperInvariant(name[0]) + name[1..],
                LastName = "Fornitore",
                OrgId = supplierOrg.Id,
                SupplierOrgId = supplierOrg.Id,
                IsActive = true,
            };
            var profile = new SupplierProfile
            {
                OrgId = supplierOrg.Id,
                Email = supplierOrg.ContactEmail,
                LegalName = $"SU08 {name} Srl",
                Phone = "+39 06 000000",
                Status = SupplierStatus.Active,
                ComuniJson = "[\"058091\"]",
                CategoriesJson = "[\"cleaning\"]",
                TosAcceptedAt = DateTime.UtcNow,
            };
            db.AddRange(supplierOrg, member, profile);
            return new SupplierSeed(supplierOrg.Id, member.Id);
        }

        var supplierA = AddSupplier("alfa");
        var supplierB = AddSupplier("beta");
        await db.SaveChangesAsync();

        return new World(ownerId, org.Id, property.Id, property.Address, stay.Id, supplierA, supplierB);
    }

    private sealed record SupplierSeed(Guid OrgId, string UserId);

    private sealed record World(
        string OwnerId,
        Guid HostOrgId,
        Guid PropertyId,
        string Address,
        Guid BookingId,
        SupplierSeed SupplierA,
        SupplierSeed SupplierB);

    private sealed record RequestSeed(
        Guid SupplierOrgId,
        ServiceRequestStatus Status,
        DateTime? CompletedAt,
        DateTime? UpdatedAt,
        bool LongRent);
}
