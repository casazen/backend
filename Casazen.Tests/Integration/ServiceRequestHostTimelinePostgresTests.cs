using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-09 (A4-28) over the real pipeline on PostgreSQL: the host reads the real timeline of a request (every step with its
/// date and party, the rejection reason), asks another supplier after a rejection, and the supplier is told by email
/// and push when the host marks the request as paid.
/// </summary>
public class ServiceRequestHostTimelinePostgresTests(ServiceRequestHostTimelinePostgresTests.TimelineFactory factory)
    : IClassFixture<ServiceRequestHostTimelinePostgresTests.TimelineFactory>
{
    private const string Host = "PropertyOwner";
    private const string Landlord = "LongTermLandlord";
    private const string Supplier = "Supplier";
    private const string Comune = "H501";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ─── The host's timeline ───

    [PostgresFact]
    public async Task GetById_AsHost_ShowsEveryStepWithItsDateAndPartyOfAPaidRequest()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }));
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { }));
        await AssertOkAsync(await host.PostAsync($"/api/service-requests/{id}/mark-paid", null));

        var request = await GetJsonAsync(host, $"/api/service-requests/{id}");

        var steps = request.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(
            new[] { "Richiesto", "PresoInCarico", "Completato", "Pagato" },
            steps.Select(s => s.GetProperty("status").GetString()));
        Assert.Equal(
            new[] { "Host", "Supplier", "Supplier", "Host" },
            steps.Select(s => s.GetProperty("actor").GetString()));
        // The dates are the ones the request keeps, oldest first.
        Assert.Equal(request.GetProperty("createdAt").GetDateTime(), steps[0].GetProperty("at").GetDateTime());
        Assert.Equal(request.GetProperty("takenAt").GetDateTime(), steps[1].GetProperty("at").GetDateTime());
        Assert.Equal(request.GetProperty("completedAt").GetDateTime(), steps[2].GetProperty("at").GetDateTime());
        Assert.Equal(request.GetProperty("paidAt").GetDateTime(), steps[3].GetProperty("at").GetDateTime());
        Assert.True(steps.Zip(steps.Skip(1)).All(pair => pair.First.GetProperty("at").GetDateTime() <= pair.Second.GetProperty("at").GetDateTime()));
        Assert.Equal("SU09 Alfa Pulizie Srl", request.GetProperty("supplierName").GetString());
    }

    [PostgresFact]
    public async Task GetById_AsHost_NeverNamesAMemberOfTheSupplierTeam()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }));

        var response = await host.GetAsync($"/api/service-requests/{id}");

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The supplier is shown by its business name: the first and last name of the member who took it stay private.
        Assert.DoesNotContain(MemberFirstName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberLastName, text, StringComparison.Ordinal);
        var steps = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("history").EnumerateArray();
        Assert.All(steps, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("actorName").ValueKind));
    }

    [PostgresFact]
    public async Task GetById_AsHost_ShowsTheRejectionWithItsReasonAndDate()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Siamo chiusi per ferie" }));

        var request = await GetJsonAsync(host, $"/api/service-requests/{id}");

        Assert.Equal("Rifiutato", request.GetProperty("status").GetString());
        Assert.Equal("Siamo chiusi per ferie", request.GetProperty("rejectionReason").GetString());
        var steps = request.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(new[] { "Richiesto", "Rifiutato" }, steps.Select(s => s.GetProperty("status").GetString()));
        var rejection = steps[1];
        Assert.Equal("Supplier", rejection.GetProperty("actor").GetString());
        Assert.Equal("Siamo chiusi per ferie", rejection.GetProperty("reason").GetString());
        // A rejection is final: its date is the last update of the request.
        Assert.Equal(request.GetProperty("updatedAt").GetDateTime(), rejection.GetProperty("at").GetDateTime());
        Assert.True(rejection.GetProperty("at").GetDateTime() >= steps[0].GetProperty("at").GetDateTime());
    }

    [PostgresFact]
    public async Task GetById_AsHost_NewRequestShowsOnlyTheRequestStep()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        var id = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);

        var request = await GetJsonAsync(host, $"/api/service-requests/{id}");

        var step = Assert.Single(request.GetProperty("history").EnumerateArray());
        Assert.Equal("Richiesto", step.GetProperty("status").GetString());
        Assert.Equal("Host", step.GetProperty("actor").GetString());
    }

    [PostgresFact]
    public async Task List_AsHost_EveryItemCarriesItsHistory()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var rejected = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{rejected}/reject", new { reason = "Non disponibile" }));
        var fresh = await CreateStayRequestAsync(host, w, w.SupplierB.OrgId);

        var list = await GetJsonAsync(host, $"/api/service-requests?bookingId={w.BookingId}");

        var items = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal(
            new[] { "Richiesto", "Rifiutato" },
            items[rejected].GetProperty("history").EnumerateArray().Select(s => s.GetProperty("status").GetString()));
        Assert.Equal(
            new[] { "Richiesto" },
            items[fresh].GetProperty("history").EnumerateArray().Select(s => s.GetProperty("status").GetString()));
    }

    [PostgresFact]
    public async Task List_AsLandlord_LongRentRequestsCarryTheirHistoryToo()
    {
        var w = await SeedWorldAsync();
        using var landlord = factory.CreateAuthenticatedClient(w.OwnerId, Landlord);
        using var supplier = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var response = await landlord.PostAsJsonAsync("/api/long-rent/service-requests", new
        {
            propertyId = w.PropertyId,
            supplierOrgId = w.SupplierA.OrgId,
            category = "cleaning",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Fuori zona" }));

        var list = await GetJsonAsync(landlord, $"/api/long-rent/service-requests?propertyId={w.PropertyId}");

        var item = Assert.Single(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
        var last = item.GetProperty("history").EnumerateArray().Last();
        Assert.Equal("Rifiutato", last.GetProperty("status").GetString());
        Assert.Equal("Fuori zona", last.GetProperty("reason").GetString());
    }

    // ─── Ask another supplier after a rejection ───

    [PostgresFact]
    public async Task Create_AfterTheFirstSupplierRejected_AnotherSupplierCanBeRequestedForTheSameStay()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplierA = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var first = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId, "Chiavi in portineria");
        await AssertOkAsync(await supplierA.PostAsJsonAsync($"/api/service-requests/{first}/reject", new { reason = "Non disponibile" }));

        var second = await CreateStayRequestAsync(host, w, w.SupplierB.OrgId, "Chiavi in portineria");

        var list = await GetJsonAsync(host, $"/api/service-requests?bookingId={w.BookingId}");
        var items = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal(2, items.Count);
        Assert.Equal("Rifiutato", items[first].GetProperty("status").GetString());
        Assert.Equal("Richiesto", items[second].GetProperty("status").GetString());
        Assert.Equal(w.SupplierB.OrgId, items[second].GetProperty("supplierOrgId").GetGuid());
        Assert.Equal(w.BookingId, items[second].GetProperty("bookingId").GetGuid());
        // The new supplier gets the request in its inbox, the first one's request stays rejected.
        using var supplierB = factory.CreateAuthenticatedClient(w.SupplierB.UserId, Supplier);
        var inbox = await GetJsonAsync(supplierB, "/api/supplier/inbox");
        Assert.Contains(second, inbox.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [PostgresFact]
    public async Task Suppliers_ForTheCategory_ListTheSupplierThatRejectedTooSoTheHostCanDecide()
    {
        // The API does not exclude the supplier that rejected: the web form leaves it out by default, the host may still pick it.
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplierA = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var first = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        await AssertOkAsync(await supplierA.PostAsJsonAsync($"/api/service-requests/{first}/reject", new { reason = "Non disponibile" }));

        var suppliers = await GetJsonAsync(host, $"/api/suppliers?propertyId={w.PropertyId}&category=cleaning");

        var ids = suppliers.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("orgId").GetGuid()).ToList();
        Assert.Contains(w.SupplierA.OrgId, ids);
        Assert.Contains(w.SupplierB.OrgId, ids);
    }

    // ─── The supplier is told when the host marks the request as paid ───

    [PostgresFact]
    public async Task MarkPaid_AsHost_QueuesOneEmailAndOnePushToTheSupplier()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        using var supplier = factory.CreateAuthenticatedClient(w.SupplierA.UserId, Supplier);
        var id = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/take", new { }));
        await AssertOkAsync(await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { }));
        var emailsBefore = factory.Emails.Snapshot().Count(e => e.To == w.SupplierA.Email);
        var pushesBefore = factory.Pushes.Count(p => p.Payload.Type == PushTypes.ServiceRequestPaid);

        await AssertOkAsync(await host.PostAsync($"/api/service-requests/{id}/mark-paid", null));

        var emails = factory.Emails.Snapshot().Where(e => e.To == w.SupplierA.Email).Skip(emailsBefore).ToList();
        var email = Assert.Single(emails);
        Assert.Equal(EmailTemplates.Names.ServiceRequestPaid, email.Template);
        Assert.Contains("segnato come <strong>pagata</strong>", email.Content.HtmlBody, StringComparison.Ordinal);
        var push = Assert.Single(factory.Pushes.Where(p => p.Payload.Type == PushTypes.ServiceRequestPaid).Skip(pushesBefore));
        Assert.Equal(PushAudience.SupplierOrg(w.SupplierA.OrgId), push.Audience);
        Assert.Equal(id, push.Payload.ServiceRequestId);
        // Marking it paid a second time is refused and notifies nobody again.
        var again = await host.PostAsync($"/api/service-requests/{id}/mark-paid", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Single(factory.Emails.Snapshot().Where(e => e.To == w.SupplierA.Email).Skip(emailsBefore));
        Assert.Single(factory.Pushes.Where(p => p.Payload.Type == PushTypes.ServiceRequestPaid).Skip(pushesBefore));
    }

    [PostgresFact]
    public async Task MarkPaid_BeforeCompletion_Returns422AndNotifiesNobody()
    {
        var w = await SeedWorldAsync();
        using var host = factory.CreateAuthenticatedClient(w.OwnerId, Host);
        var id = await CreateStayRequestAsync(host, w, w.SupplierA.OrgId);
        var emailsBefore = factory.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestPaid);
        var pushesBefore = factory.Pushes.Count(p => p.Payload.Type == PushTypes.ServiceRequestPaid);

        var response = await host.PostAsync($"/api/service-requests/{id}/mark-paid", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(emailsBefore, factory.Emails.Snapshot().Count(e => e.Template == EmailTemplates.Names.ServiceRequestPaid));
        Assert.Equal(pushesBefore, factory.Pushes.Count(p => p.Payload.Type == PushTypes.ServiceRequestPaid));
    }

    // ─── helpers ───

    private const string MemberFirstName = "Zaccaria";
    private const string MemberLastName = "Squadrista";

    private sealed record SeededSupplier(Guid OrgId, string UserId, string Email);

    private sealed record World(string OwnerId, Guid PropertyId, Guid BookingId, SeededSupplier SupplierA, SeededSupplier SupplierB);

    private static async Task<Guid> CreateStayRequestAsync(HttpClient host, World w, Guid supplierOrgId, string? notes = null)
    {
        var response = await host.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = w.PropertyId,
            bookingId = w.BookingId,
            supplierOrgId,
            category = "cleaning",
            notes,
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("id").GetGuid();
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {text}");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: expected 200, got {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    /// <summary>
    /// A host org with one property in comune H501 (also a long-term property of the same owner, who holds both roles)
    /// and one confirmed stay on it, and two active suppliers (their own orgs) in H501 for cleaning.
    /// </summary>
    private async Task<World> SeedWorldAsync()
    {
        var ownerId = $"auth0|su09-owner-{Guid.NewGuid():N}";
        var org = await factory.SeedOrgForOwnerAsync(ownerId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = org.Id,
            Name = "Casa SU09",
            Address = $"Via SU09 {Guid.NewGuid():N}",
            City = Comune,
            PostalCode = "00100",
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
            FirstName = "Anna",
            LastName = "Ospite",
            Email = $"su09-{Guid.NewGuid():N}@example.com",
        };
        var stay = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(5),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(8),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.AddRange(property, guest, stay);
        var supplierA = AddSupplier(db, "SU09 Alfa Pulizie Srl");
        var supplierB = AddSupplier(db, "SU09 Beta Pulizie Srl");
        await db.SaveChangesAsync();
        return new World(ownerId, property.Id, stay.Id, supplierA, supplierB);
    }

    private static SeededSupplier AddSupplier(AppDbContext db, string legalName)
    {
        var email = $"su09-supplier-{Guid.NewGuid():N}@example.com";
        var userId = $"auth0|su09-supplier-{Guid.NewGuid():N}";
        var supplierOrg = new OrgEntity
        {
            Name = legalName,
            Slug = $"su09-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = legalName,
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.AddRange(
            supplierOrg,
            new User
            {
                Id = userId,
                Email = $"member-{Guid.NewGuid():N}@example.com",
                FirstName = MemberFirstName,
                LastName = MemberLastName,
                // A supplier account links its supplier org only: User.OrgId is the host org (PL-05, A1-40).
                SupplierOrgId = supplierOrg.Id,
                IsActive = true,
            },
            new SupplierProfile
            {
                OrgId = supplierOrg.Id,
                Email = email,
                LegalName = legalName,
                Phone = "+39 06 000000",
                Status = SupplierStatus.Active,
                ComuniJson = $"[\"{Comune}\"]",
                CategoriesJson = "[\"cleaning\"]",
                TosAcceptedAt = DateTime.UtcNow,
            });
        return new SeededSupplier(supplierOrg.Id, userId, email);
    }

    /// <summary>The pilot-comuni factory with a recording email queue, and a recording push queue.</summary>
    public sealed class TimelineFactory : SupplierRegistrationIntegrationTests.PilotComuniFactory
    {
        public ConcurrentQueue<(string Key, PushAudience Audience, PushNotificationPayload Payload)> PushQueue { get; } = new();

        public IEnumerable<(string Key, PushAudience Audience, PushNotificationPayload Payload)> Pushes => PushQueue.ToArray();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPushNotificationService>();
                services.AddSingleton<IPushNotificationService>(new RecordingPush(PushQueue));
            });
        }

        private sealed class RecordingPush(ConcurrentQueue<(string Key, PushAudience Audience, PushNotificationPayload Payload)> queue)
            : IPushNotificationService
        {
            public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
            {
                queue.Enqueue((deliveryKey, audience, payload));
                return true;
            }
        }
    }
}
