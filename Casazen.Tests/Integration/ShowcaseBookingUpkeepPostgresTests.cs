using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-10 on PostgreSQL: the three jobs of the booking on the real database — the upkeep every five minutes (the holds that lapsed
/// are deleted, the requests nobody answered are cancelled and the customer told), the reminders of the day before at 18:00 in
/// Rome, and the nightly retention of the customers (off until a period and its source are configured). Each runs one at a time (a
/// session advisory lock: a second run while one holds it is skipped), is idempotent, and finds the rows through the statements the
/// tests of the SQL translation read without a server. The database is shared by the tests of the class, so the counts of a run are
/// "at least" and the rest is checked on the rows of the test's own supplier.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingUpkeepPostgresTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    // ─── The upkeep every five minutes ───

    [PostgresFact]
    public async Task Expiry_DeletesTheHoldsThatLapsed_AndCancelsTheRequestsNobodyAnswered()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var unchecked_ = PublicBookingTestData.NewEmail();
        var answered = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        // A booking nobody checks, and a request nobody answers.
        var created = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, unchecked_));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1), answered);
        Assert.Equal(2, await CountHoldsAsync(supplier.OrgId));

        ServiceRequestExpiryRun run;
        using (factory.MoveClock(TimeSpan.FromMinutes(181)))
            run = await RunExpiryAsync();

        Assert.False(run.Skipped);
        Assert.True(run.HoldsDeleted >= 2, $"HoldsDeleted {run.HoldsDeleted}");
        Assert.True(run.RequestsCancelled >= 1, $"RequestsCancelled {run.RequestsCancelled}");
        Assert.Equal(0, await CountHoldsAsync(supplier.OrgId));
        var stored = await LoadRequestAsync(requestId);
        Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
        Assert.Equal(ServiceRequestActorParty.System, stored.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, stored.CancellationReason);
        Assert.Null(stored.ResponseDueAt);
        // The customer whose request lapsed is told; the one who never checked the address hears nothing more.
        Assert.Single(factory.EmailsTo(answered), e => e.Template == EmailTemplates.Names.SupplierBookingExpired);
        Assert.DoesNotContain(factory.EmailsTo(unchecked_), e => e.Template == EmailTemplates.Names.SupplierBookingExpired);
    }

    [PostgresFact]
    public async Task Expiry_ARequestTheSupplierTookInTime_IsLeftAlone_AndASecondRunFindsNothingMoreOfIt()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var requestId = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9, email);
        using (var asSupplier = supplier.Client(factory))
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{requestId}/take", content: null)).StatusCode);

        using (factory.MoveClock(TimeSpan.FromMinutes(181)))
        {
            await RunExpiryAsync();
            await RunExpiryAsync();
        }

        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await LoadRequestAsync(requestId)).Status);
        Assert.DoesNotContain(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierBookingExpired);
    }

    [PostgresFact]
    public async Task Expiry_WhileAnotherRunHoldsTheSessionLock_IsSkipped_AndTheNextOneDoesTheWork()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, PublicBookingTestData.NewEmail()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using (factory.MoveClock(TimeSpan.FromMinutes(31)))
        {
            await using (var holderScope = factory.Services.CreateAsyncScope())
            {
                var holderDb = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var held = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                    holderDb, PostgresAdvisoryLocks.Scope.ServiceRequestExpiryRun, ServiceRequestExpiryService.RunLockKey, CancellationToken.None);
                Assert.NotNull(held);

                var skipped = await RunExpiryAsync();

                Assert.True(skipped.Skipped);
                Assert.Equal(0, skipped.HoldsDeleted);
                Assert.Equal(1, await CountHoldsAsync(supplier.OrgId));
            }

            // The lock is released with the handle: the next run takes it and does the work.
            var run = await RunExpiryAsync();
            Assert.False(run.Skipped);
            Assert.True(run.HoldsDeleted >= 1);
            Assert.Equal(0, await CountHoldsAsync(supplier.OrgId));
        }
    }

    // ─── The reminders of the day before ───

    [PostgresFact]
    public async Task Reminders_FromEighteenTheDayBefore_QueueOneEmailToTheCustomer_Once()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        // The work is on Tuesday 13 October at 09:00 in Rome; the host clock is Monday 12 October at 08:45 in Rome.
        var requestId = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9, email);
        using (var asSupplier = supplier.Client(factory))
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{requestId}/take", content: null)).StatusCode);

        // 17:59 in Rome: not yet.
        using (factory.MoveClock(TimeSpan.FromHours(9) + TimeSpan.FromMinutes(14)))
        {
            await RunRemindersAsync();
            Assert.DoesNotContain(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierBookingReminder);
            Assert.Null((await LoadRequestAsync(requestId)).ReminderSentAt);
        }

        // 18:00 in Rome: the reminder, once.
        ServiceRequestReminderRun first;
        using (factory.MoveClock(TimeSpan.FromHours(9) + TimeSpan.FromMinutes(15)))
        {
            first = await RunRemindersAsync();
            await RunRemindersAsync();
        }

        Assert.False(first.Skipped);
        Assert.True(first.Sent >= 1, $"Sent {first.Sent}");
        var reminder = Assert.Single(factory.EmailsTo(email), e => e.Template == EmailTemplates.Names.SupplierBookingReminder);
        Assert.Contains("Domani:", reminder.Content.Subject);
        Assert.NotNull((await LoadRequestAsync(requestId)).ReminderSentAt);
    }

    [PostgresFact]
    public async Task Reminders_WhileAnotherRunHoldsTheSessionLock_AreSkipped()
    {
        await using var holderScope = factory.Services.CreateAsyncScope();
        var holderDb = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var held = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            holderDb, PostgresAdvisoryLocks.Scope.ServiceRequestRemindersRun, ServiceRequestReminderService.RunLockKey, CancellationToken.None);
        Assert.NotNull(held);

        var skipped = await RunRemindersAsync();

        Assert.True(skipped.Skipped);
        Assert.Equal(0, skipped.Sent);
    }

    // ─── The retention, off by default ───

    [PostgresFact]
    public async Task Retention_WithoutAPeriodAndItsSource_AnonymizesNothing()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9, email);

        ServiceCustomerRetentionRun run;
        using (factory.MoveClock(TimeSpan.FromDays(365 * 10)))
            run = await RunRetentionAsync();

        Assert.False(run.RetentionConfigured);
        Assert.Equal(0, run.Customers);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await db.ServiceCustomers.AsNoTracking().SingleAsync(c => c.OrgId == supplier.OrgId);
        Assert.Null(customer.AnonymizedAt);
        Assert.Equal(PublicBookingTestData.FullName, customer.FullName);
    }

    // ─── helpers ───

    private async Task<Guid> BookAsync(HttpClient client, BookableSupplier supplier, DateTime start, string email)
    {
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var inbox = await supplier.InboxAsync(factory);
        return inbox.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("status").GetString() == "Richiesto")
            .GetProperty("id").GetGuid();
    }

    private async Task<ServiceRequestExpiryRun> RunExpiryAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceRequestExpiryService>().RunAsync();
    }

    private async Task<ServiceRequestReminderRun> RunRemindersAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceRequestReminderService>().SendDueAsync();
    }

    private async Task<ServiceCustomerRetentionRun> RunRetentionAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceCustomerPrivacyService>().ApplyRetentionAsync();
    }

    private async Task<ServiceRequest> LoadRequestAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<int> CountHoldsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ShowcaseBookingHolds.CountAsync(h => h.OrgId == orgId);
    }
}

/// <summary>The showcase host with the retention of the customers configured: two years, with a source.</summary>
public sealed class PublicBookingRetentionFactory : PublicBookingFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings() =>
    [
        new("Gdpr:Retention:SupplierCustomers:Years", "2"),
        new("Gdpr:Retention:SupplierCustomers:Source", "Art. 2220 c.c. (test)"),
    ];
}

/// <summary>
/// SP-10 on PostgreSQL: the nightly retention of the customers with a period configured (<c>Gdpr:Retention:SupplierCustomers</c>):
/// a customer none of whose requests is open and whose last one ended more than the period ago is anonymized, the place of its
/// request loses the street, the floor and the access notes, and a customer with an open request keeps everything. Idempotent.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingRetentionPostgresTests(PublicBookingRetentionFactory factory) : IClassFixture<PublicBookingRetentionFactory>
{
    [PostgresFact]
    public async Task Retention_AnonymizesTheCustomerOfACompletedRequest_AfterThePeriod_AndKeepsTheOneWithAnOpenRequest()
    {
        var supplier = await PublicBookingTestData.SeedAsync(factory);
        var done = PublicBookingTestData.NewEmail();
        var open = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        var completed = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9, done);
        var pending = await BookAsync(client, supplier, PublicBookingTestData.TuesdayAt9.AddDays(1), open);
        using (var asSupplier = supplier.Client(factory))
        {
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{completed}/take", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{completed}/start", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await asSupplier.PostAsync($"/api/service-requests/{completed}/complete", content: null)).StatusCode);
        }

        ServiceCustomerRetentionRun first;
        ServiceCustomerRetentionRun second;
        using (factory.MoveClock(TimeSpan.FromDays((365 * 2) + 60)))
        {
            first = await RunAsync();
            second = await RunAsync();
        }

        Assert.True(first.RetentionConfigured);
        Assert.True(first.Customers >= 1, $"Customers {first.Customers}");
        Assert.True(first.Requests >= 1, $"Requests {first.Requests}");
        Assert.Equal(0, second.Customers);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customers = await db.ServiceCustomers.AsNoTracking().Where(c => c.OrgId == supplier.OrgId).ToListAsync();
        var anonymized = Assert.Single(customers, c => c.AnonymizedAt is not null);
        Assert.Equal(ServiceCustomerPrivacyService.AnonymizedValue, anonymized.FullName);
        Assert.Equal(ServiceCustomerPrivacyService.AnonymizedEmail(anonymized.Id), anonymized.Email);
        Assert.Null(anonymized.Phone);
        var kept = Assert.Single(customers, c => c.AnonymizedAt is null);
        Assert.Equal(PublicBookingTestData.FullName, kept.FullName);
        Assert.Equal(open, kept.Email);

        var completedRequest = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == completed);
        Assert.Null(completedRequest.LocationAddress);
        Assert.Null(completedRequest.LocationFloor);
        Assert.Null(completedRequest.LocationAccessNotes);
        Assert.Equal(PublicBookingTestData.City, completedRequest.LocationCity);
        var openRequest = await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == pending);
        Assert.Equal(PublicBookingTestData.Address, openRequest.LocationAddress);
    }

    // ─── helpers ───

    private async Task<Guid> BookAsync(HttpClient client, BookableSupplier supplier, DateTime start, string email)
    {
        var created = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, start, email));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{supplier.Slug}/bookings/{holdId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        // The request that has just been created: the oldest one of the supplier that nobody has an id for yet.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hold = await db.ShowcaseBookingHolds.AsNoTracking().SingleAsync(h => h.Id == holdId);
        return hold.ServiceRequestId!.Value;
    }

    private async Task<ServiceCustomerRetentionRun> RunAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceCustomerPrivacyService>().ApplyRetentionAsync();
    }
}

/// <summary>
/// SP-10 on PostgreSQL: a request from the showcase and a request of a host can have the same org — the supplier org that is also
/// a host org — and still never meet. Every read and every action of the host answers for its own requests only, as if the
/// showcase ones were not there; the supplier reads its showcase requests and not the host's ones.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ShowcaseBookingTenancyPostgresTests(PublicBookingFactory factory) : IClassFixture<PublicBookingFactory>
{
    [PostgresFact]
    public async Task AnOrgThatIsAHostAndASupplier_KeepsItsTwoKindsOfRequestsApart()
    {
        var dual = await PublicBookingTestData.SeedAsync(factory);
        var other = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            $"/api/public/suppliers/{dual.Slug}/bookings", PublicBookingTestData.Body(dual.Service, PublicBookingTestData.TuesdayAt9, email));
        var holdId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (_, token) = PublicBookingTestData.LinkOf(Assert.Single(factory.EmailsTo(email)));
        var confirmed = await client.PostAsJsonAsync($"/api/public/suppliers/{dual.Slug}/bookings/{holdId}/confirm-email", new { token });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        // The same org also asks another supplier for a job on one of its own properties.
        var hostRequestId = await SeedHostRequestAsync(dual.OrgId, other.OrgId);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceRequestService>();
        var showcaseId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().ShowcaseBookingHolds
            .AsNoTracking().SingleAsync(h => h.Id == holdId)).ServiceRequestId!.Value;
        var scopeOfTheOrg = new HostScope(dual.OrgId, null);

        var (hostItems, hostTotal) = await service.ListForHostAsync(scopeOfTheOrg, ServiceRequestRentalContext.ShortRent, null, null, null, 1, 100);
        var (showcaseItems, showcaseTotal) = await service.ListForHostAsync(scopeOfTheOrg, ServiceRequestRentalContext.Showcase, null, null, null, 1, 100);
        var (asSupplier, asSupplierTotal) = await service.ListForSupplierAsync(dual.OrgId, openOnly: false, 1, 100);

        Assert.Equal(hostRequestId, Assert.Single(hostItems).Id);
        Assert.Equal(1, hostTotal);
        Assert.Empty(showcaseItems);
        Assert.Equal(0, showcaseTotal);
        Assert.Equal(showcaseId, Assert.Single(asSupplier).Id);
        Assert.Equal(1, asSupplierTotal);
        Assert.NotNull(await service.GetByIdForHostAsync(hostRequestId, scopeOfTheOrg, ServiceRequestRentalContext.ShortRent));
        Assert.Null(await service.GetByIdForHostAsync(showcaseId, scopeOfTheOrg, ServiceRequestRentalContext.ShortRent));
        Assert.Null(await service.GetByIdForHostAsync(showcaseId, scopeOfTheOrg, ServiceRequestRentalContext.Showcase));

        // The actions of the host answer 404 for the showcase request, whatever the org.
        await Assert.ThrowsAsync<NotFoundException>(() => service.CancelAsHostAsync(showcaseId, dual.OrgId, "Prova"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.MarkPaidAsync(showcaseId, dual.OrgId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemindAsync(showcaseId, dual.OrgId));
        Assert.Equal(ServiceRequestStatus.Richiesto, (await service.GetByIdForSupplierAsync(showcaseId, dual.OrgId))!.Status);

        // The inbox of the supplier shows the showcase request and not the one it made as a host.
        var inbox = await dual.InboxAsync(factory);
        Assert.Equal(showcaseId, Assert.Single(inbox.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [PostgresFact]
    public async Task TwoSuppliers_NeverSeeEachOthersCustomersHoldsOrRequests()
    {
        var mine = await PublicBookingTestData.SeedAsync(factory);
        var theirs = await PublicBookingTestData.SeedAsync(factory);
        var email = PublicBookingTestData.NewEmail();
        using var client = factory.CreateClient();
        foreach (var supplier in new[] { mine, theirs })
        {
            var created = await client.PostAsJsonAsync(
                $"/api/public/suppliers/{supplier.Slug}/bookings", PublicBookingTestData.Body(supplier.Service, PublicBookingTestData.TuesdayAt9, email));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var links = factory.EmailsTo(email).Select(PublicBookingTestData.LinkOf).ToList();
        Assert.Equal(2, links.Count);
        // A link belongs to the supplier of the booking: the other supplier's page answers the same 404 as a wrong token.
        foreach (var (holdId, token) in links)
        {
            var crossed = await client.PostAsJsonAsync($"/api/public/suppliers/{theirs.Slug}/bookings/{holdId}/confirm-email", new { token });
            var own = await client.PostAsJsonAsync($"/api/public/suppliers/{mine.Slug}/bookings/{holdId}/confirm-email", new { token });
            Assert.True(
                crossed.StatusCode == HttpStatusCode.NotFound || own.StatusCode == HttpStatusCode.NotFound,
                "a link must not open on the wrong supplier");
            Assert.True(crossed.StatusCode == HttpStatusCode.OK ^ own.StatusCode == HttpStatusCode.OK, "a link opens on exactly one supplier");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customerOfMine = Assert.Single(await db.ServiceCustomers.AsNoTracking().Where(c => c.OrgId == mine.OrgId).ToListAsync());
        var customerOfTheirs = Assert.Single(await db.ServiceCustomers.AsNoTracking().Where(c => c.OrgId == theirs.OrgId).ToListAsync());
        Assert.NotEqual(customerOfMine.Id, customerOfTheirs.Id);
        Assert.Equal(1, (await mine.InboxAsync(factory)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await theirs.InboxAsync(factory)).GetProperty("total").GetInt32());
    }

    // ─── helpers ───

    /// <summary>A property of <paramref name="hostOrgId"/> and a short-rent request on it for <paramref name="supplierOrgId"/>; returns the request.</summary>
    private async Task<Guid> SeedHostRequestAsync(Guid hostOrgId, Guid supplierOrgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = $"auth0|sp10-dual-{Guid.NewGuid():N}",
            OrgId = hostOrgId,
            Name = "Casa Riservata",
            Address = $"Via Segretissima {Guid.NewGuid():N}",
            City = "Monza",
            PostalCode = "20900",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            CinCode = "IT058091C27G5FFZDZ",
            IsActive = true,
        };
        var request = new ServiceRequest
        {
            OrgId = hostOrgId,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = ServiceCategories.Cleaning,
            Status = ServiceRequestStatus.Richiesto,
        };
        db.AddRange(property, request);
        await db.SaveChangesAsync();
        return request.Id;
    }
}
