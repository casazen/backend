using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-10 on PostgreSQL, the part that cannot be shown in memory: customers who book the same slot at the same moment. Every
/// booking and every check of an e-mail takes the supplier's calendar lock (<c>SupplierCalendarSync</c>, the lock of the agenda,
/// of the iCal sync and of the requests with a time) and judges the slot with the slot planner after taking it — so of N customers
/// exactly one holds it, the others get 409 <c>supplier_slot_unavailable</c> and none gets a 500; two clicks on one link make
/// one request; and a booking and a host's request for the same slot never both succeed. Few parallel connections on purpose
/// (every request holds one while it waits for the lock).
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingConcurrencyPostgresTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    private const int Customers = 6;

    [PostgresFact]
    public async Task SameSlot_ManyCustomersAtTheSameMoment_OneHoldsItAndTheOthersGet409()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var emails = Enumerable.Range(0, Customers).Select(_ => PublicBookingTestData.NewEmail()).ToArray();
        using var client = factory.CreateClient();

        var responses = await InParallelAsync(
            emails.Select(email => (Func<Task<HttpResponseMessage>>)(() => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email)))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(Customers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        foreach (var lost in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            Assert.Equal("supplier_slot_unavailable", (await lost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(1, await CountHoldsAsync(supplier.OrgId));
        // Only the winner is written to: one verification e-mail in all.
        Assert.Equal(1, emails.Sum(email => factory.EmailsTo(email).Count));
    }

    [PostgresFact]
    public async Task OverlappingSlots_ManyCustomersAtTheSameMoment_OneHoldsTheMorning()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        // 09:00, 10:00 and 11:00 in Rome: a two hours job at any of them overlaps the others or breaks the 30 minutes between jobs.
        var starts = new[] { 0, 1, 2 }.Select(hour => PublicBookingTestData.TuesdayAt9.AddHours(hour)).ToArray();
        var emails = starts.SelectMany(_ => new[] { PublicBookingTestData.NewEmail(), PublicBookingTestData.NewEmail() }).ToArray();
        using var client = factory.CreateClient();

        var responses = await InParallelAsync(
            emails.Select((email, index) => (Func<Task<HttpResponseMessage>>)(() => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, starts[index / 2], email)))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Equal(1, await CountHoldsAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task SlotsThatFitTogether_AtTheSameMoment_AreAllHeld()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        // The morning and the afternoon of one day, and another day: no overlap, no buffer between them, three jobs in two days.
        var starts = new[] { PublicBookingTestData.TuesdayAt9, PublicBookingTestData.TuesdayAt9.AddHours(5), PublicBookingTestData.TuesdayAt9.AddDays(1) };

        var responses = await InParallelAsync(
            starts.Select(start => (Func<Task<HttpResponseMessage>>)(() => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, start, PublicBookingTestData.NewEmail())))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(3, await CountHoldsAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task OneAddress_ManyBookingsAtTheSameMoment_NeverMoreThanThreeWaitForTheCheck()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        // Six different slots of six working days (Tuesday to Friday, Monday and Tuesday after), all free: one address.
        var starts = new[] { 0, 1, 2, 3, 6, 7 }.Select(day => PublicBookingTestData.TuesdayAt9.AddDays(day)).ToArray();

        var responses = await InParallelAsync(
            starts.Select(start => (Func<Task<HttpResponseMessage>>)(() => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email)))));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Equal(3, await CountHoldsAsync(supplier.OrgId));
    }

    [PostgresFact]
    public async Task ConfirmEmail_TheSameLinkClickedTwiceAtTheSameMoment_MakesOneRequest_AndBothClicksAnswerTheBooking()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email));
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));

        var responses = await InParallelAsync(
            Enumerable.Range(0, 2).Select(_ => (Func<Task<HttpResponseMessage>>)(() => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token }))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var bodies = new List<JsonElement>();
        foreach (var response in responses)
            bodies.Add(await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(1, bodies.Count(body => !body.GetProperty("alreadyConfirmed").GetBoolean()));
        Assert.Single(bodies.Select(body => body.GetProperty("publicCode").GetString()).Distinct());

        Assert.Equal(1, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());
        Assert.Equal(1, await CountCustomersAsync(supplier.OrgId));
        Assert.Single(factory.EmailsTo(email), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingReceipt);
        Assert.Single(factory.EmailsTo(supplier.ContactEmail), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingNewRequest);
    }

    [PostgresFact]
    public async Task ConfirmEmail_TwoBookingsOfTheSameAddressChecked_AtTheSameMoment_MakeOneCustomer()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var links = new List<(Guid HoldId, string Token)>();
        foreach (var day in new[] { 0, 1 })
        {
            var created = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings",
                PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9.AddDays(day), email));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        foreach (var verification in factory.EmailsTo(email))
            links.Add(PublicBookingTestData.LinkOf(verification));
        Assert.Equal(2, links.Count);

        var responses = await InParallelAsync(
            links.Select(link => (Func<Task<HttpResponseMessage>>)(() => client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings/{link.HoldId}/confirm-email", new { token = link.Token }))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(2, (await supplier.InboxAsync(factory)).GetProperty("total").GetInt32());
        // One address, one customer: the unique index never fired, because the checks went one after the other.
        Assert.Equal(1, await CountCustomersAsync(supplier.OrgId));
    }

    // ─── helpers ───

    /// <summary>Runs every call at the same moment (they all wait at the gate), and returns the answers in order.</summary>
    private static async Task<HttpResponseMessage[]> InParallelAsync(IEnumerable<Func<Task<HttpResponseMessage>>> calls)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = calls.Select(async call =>
        {
            await gate.Task;
            return await call();
        }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<int> CountHoldsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ShowcaseBookingHolds.CountAsync(h => h.OrgId == orgId);
    }

    private async Task<int> CountCustomersAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceCustomers.CountAsync(c => c.OrgId == orgId);
    }
}

/// <summary>
/// SP-10 on PostgreSQL, the check of the row version: the upkeep that cancels a showcase request nobody answered and the supplier
/// who takes it at the same moment. Both read the request as unanswered and are held before saving until both got there; the
/// database lets one in (<c>xmin</c>) and the other leaves the request alone — a conflict, never a double change, never a 500. A
/// class of its own with a database of its own: no other request is overdue, so the two saves of the rendezvous are these two.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingUpkeepRacePostgresTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    [PostgresFact]
    public async Task TheUpkeepAndTheSupplierAtTheSameMoment_OneWinsAndTheOtherLeavesTheRequestAlone()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var requestId = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9, email);

        using var moved = factory.MoveClock(TimeSpan.FromMinutes(181));
        // The upkeep and the take read the request as unanswered and are held before saving until both got there.
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            SaveRendezvous.HoldParallelSaves(services, parties: 2)));
        await using var scope = app.Services.CreateAsyncScope();
        var expiry = scope.ServiceProvider.GetRequiredService<IServiceRequestExpiryService>();

        var runTask = expiry.RunAsync();
        var takeTask = Task.Run(async () =>
        {
            using var asSupplier = AppClient(app, supplier);
            return await asSupplier.PostAsync($"/api/service-requests/{requestId}/take", content: null);
        });

        var run = await runTask;
        var take = await takeTask;

        var stored = await LoadRequestAsync(requestId);
        if (take.StatusCode == HttpStatusCode.OK)
        {
            // The supplier answered first: the upkeep counted a conflict and left the take as it is.
            Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
            Assert.True(run.Conflicts >= 1, $"Conflicts {run.Conflicts}");
            Assert.DoesNotContain(factory.EmailsTo(email), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingExpired);
        }
        else
        {
            // The upkeep cancelled first: the take got 409 and nothing of it was saved.
            Assert.Equal(HttpStatusCode.Conflict, take.StatusCode);
            Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
            Assert.Null(stored.TakenAt);
            Assert.True(run.RequestsCancelled >= 1, $"Cancelled {run.RequestsCancelled}");
            Assert.DoesNotContain(factory.EmailsTo(email), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingAccepted);
        }
    }

    private async Task<Guid> BookAsync(HttpClient client, BookableSupplier supplier, DateTime start, string email)
    {
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var inbox = await supplier.InboxAsync(factory);
        return inbox.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
    }

    private async Task<ServiceRequest> LoadRequestAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static HttpClient AppClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, BookableSupplier supplier)
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", supplier.UserId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Supplier");
        return client;
    }
}

/// <summary>The default integration host (real clock) with the flag <c>SupplierShowcaseBooking</c> on.</summary>
public sealed class ShowcaseRealClockFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:SupplierShowcaseBooking"] = "true" }));
    }
}

/// <summary>
/// SP-10 on PostgreSQL: a customer of the showcase and a host both ask the same supplier for the same slot at the same moment.
/// They are two doors on one calendar — the booking and the request with a time take the same lock and judge with the same
/// planner — so exactly one of them gets the slot and the other gets 409 <c>supplier_slot_unavailable</c>.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingAndHostRequestPostgresTests(ShowcaseRealClockFactory factory) : IClassFixture<ShowcaseRealClockFactory>
{
    [PostgresFact]
    public async Task ABookingFromTheShowcaseAndARequestOfAHost_ForTheSameSlot_NeverBothSucceed()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var service = await MakeBookableAsync(world);
        var slot = ServiceRequestWorlds.Slot(hour: 10, day: DayOfWeek.Wednesday, minDaysAhead: 3);
        using var customer = factory.CreateClient();
        using var host = factory.Host(world);

        var responses = await Task.WhenAll(
            Gated(() => customer.PostAsJsonAsync(
                $"/api/public/suppliers/{service.Slug}/bookings",
                PublicBookingTestData.Body(service.Service, slot, PublicBookingTestData.NewEmail()))),
            Gated(() => host.PostAsJsonAsync("/api/service-requests", new
            {
                propertyId = world.PropertyId,
                bookingId = world.BookingId,
                supplierOrgId = world.SupplierOrgId,
                category = "cleaning",
                serviceListingId = world.ListingId,
                scheduledStartUtc = slot,
            })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var lost = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
        Assert.Equal("supplier_slot_unavailable", (await lost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // ─── helpers ───

    private static async Task<HttpResponseMessage> Gated(Func<Task<HttpResponseMessage>> call)
    {
        await Task.Yield();
        return await call();
    }

    /// <summary>Gives the supplier of the world a showcase slug, the comune the customers write and online bookings; returns its slug and service.</summary>
    private async Task<BookableSupplier> MakeBookableAsync(ServiceRequestWorld world)
    {
        var slug = $"vetrina-{Guid.NewGuid():N}"[..24];
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(sp => sp.OrgId == world.SupplierOrgId);
        profile.ShowcaseSlug = slug;
        profile.ComuniJson = """["H501","Monza"]""";
        var settings = await db.SupplierSettings.SingleAsync(s => s.OrgId == world.SupplierOrgId);
        settings.OnlineBookingEnabled = true;
        var listing = await db.SupplierServiceListings.SingleAsync(l => l.Id == world.ListingId);
        await db.SaveChangesAsync();
        return new BookableSupplier(world.SupplierOrgId, slug, listing.Slug, world.SupplierUserId, profile.Email);
    }
}
