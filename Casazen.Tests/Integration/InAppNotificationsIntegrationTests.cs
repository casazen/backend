using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Tests.Integration.Postgres;
using Hangfire.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// UI-12a over the real pipeline with <c>Features:InAppNotifications</c> on: the four endpoints of the bell with the token of
/// the user, the isolation between users and orgs, and the whole chain from a push queued by a service to the notification the
/// user reads (decorator, job, endpoint), once. The updates (<c>read</c>, <c>read-all</c>) are single SQL statements the in-memory
/// fallback does not run, so those tests are <c>[PostgresFact]</c>. With the flag off see
/// <see cref="InAppNotificationsFlagOffIntegrationTests"/>.
/// </summary>
public class InAppNotificationsIntegrationTests : IClassFixture<InAppNotificationsIntegrationTests.EnabledFactory>
{
    private static readonly DateTime Day = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    private readonly EnabledFactory _factory;

    public InAppNotificationsIntegrationTests(EnabledFactory factory) => _factory = factory;

    // ─── Access ───

    [Fact]
    public async Task Endpoints_WithoutAToken_Are401()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me/notifications/unread-count")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/me/notifications/{Guid.NewGuid()}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/me/notifications/read-all", null)).StatusCode);
    }

    [Fact]
    public async Task PublicFeatures_WithTheFlagOn_ExposesIt()
    {
        using var anonymous = _factory.CreateClient();

        var features = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/features");

        Assert.True(features.GetProperty("inAppNotifications").GetBoolean());
    }

    // ─── The list and the count ───

    [Fact]
    public async Task List_ReturnsTheCallersNotificationsNewestFirst_NothingOfOtherUsersOrOrgs_AndNoTextOrInternalField()
    {
        var (me, myOrg) = await SeedHostAsync("bell-me");
        var (other, otherOrg) = await SeedHostAsync("bell-other");
        var oldest = await AddAsync(me, myOrg, Day.AddMinutes(-30), PushTypes.NewBooking, readAt: Day.AddMinutes(-20));
        var newest = await AddAsync(me, myOrg, Day.AddMinutes(-5), PushTypes.ServiceRequestCompleted);
        var middle = await AddAsync(me, myOrg, Day.AddMinutes(-10), PushTypes.AlloggiatiOverdue);
        await AddAsync(other, otherOrg, Day.AddMinutes(-1), PushTypes.NewBooking);
        // A row of mine in an org I do not belong to: not mine to read.
        await AddAsync(me, otherOrg, Day.AddMinutes(-2), PushTypes.NewBooking);
        using var client = _factory.CreateAuthenticatedClient(me, "PropertyOwner");

        var response = await client.GetAsync("/api/me/notifications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPrivateAndNeverCached(response);
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([newest.Id, middle.Id, oldest.Id], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(3, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, body.RootElement.GetProperty("pageSize").GetInt32());
        var first = items[0];
        Assert.Equal(PushTypes.ServiceRequestCompleted, first.GetProperty("type").GetString());
        Assert.Equal(newest.EntityId, first.GetProperty("entityId").GetGuid());
        Assert.Equal(Day.AddMinutes(-5), first.GetProperty("createdAt").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("readAt").ValueKind);
        Assert.Equal(Day.AddMinutes(-20), items[2].GetProperty("readAt").GetDateTime().ToUniversalTime());
        // The client writes the text from the type: the answer carries the id, the type, the entity and two instants, no more.
        Assert.Equal(
            ["createdAt", "entityId", "id", "readAt", "type"],
            first.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(me, raw);
        Assert.DoesNotContain(other, raw);
        Assert.DoesNotContain("deliveryKey", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_UnreadAndPages()
    {
        var (me, org) = await SeedHostAsync("bell-pages");
        var rows = new List<InAppNotification>();
        for (var i = 1; i <= 5; i++)
            rows.Add(await AddAsync(me, org, Day.AddMinutes(-i), PushTypes.NewBooking, readAt: i == 5 ? Day : null));
        using var client = _factory.CreateAuthenticatedClient(me, "PropertyOwner");

        var unread = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications?unread=true");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications?page=2&pageSize=2");
        var clamped = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications?pageSize=1000&page=0");

        Assert.Equal(4, unread.GetProperty("totalCount").GetInt32());
        Assert.Equal(rows.Take(4).Select(r => r.Id), unread.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.Equal(rows[2].Id, second.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(50, clamped.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, clamped.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task UnreadCount_CountsTheUnreadOnesOfTheCallerOnly()
    {
        var (me, myOrg) = await SeedHostAsync("count-me");
        var (other, otherOrg) = await SeedHostAsync("count-other");
        await AddAsync(me, myOrg, Day.AddMinutes(-3), PushTypes.NewBooking);
        await AddAsync(me, myOrg, Day.AddMinutes(-2), PushTypes.NewBooking);
        await AddAsync(me, myOrg, Day.AddMinutes(-1), PushTypes.NewBooking, readAt: Day);
        await AddAsync(other, otherOrg, Day.AddMinutes(-1), PushTypes.NewBooking);
        using var mine = _factory.CreateAuthenticatedClient(me, "PropertyOwner");
        using var theirs = _factory.CreateAuthenticatedClient(other, "PropertyOwner");

        var response = await mine.GetAsync("/api/me/notifications/unread-count");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPrivateAndNeverCached(response);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt32());
        Assert.Equal(1, (await theirs.GetFromJsonAsync<JsonElement>("/api/me/notifications/unread-count")).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task List_ASupplierOnlyAccount_ReadsTheNotificationsOfItsSupplierOrg()
    {
        // No host org: the tenant filter would match nothing. The rows are found through the account's supplier link.
        var supplierId = $"auth0|bell-supplier-{Guid.NewGuid():N}";
        var supplierOrg = new OrgEntity
        {
            Name = "Bell Supplier",
            Slug = $"bell-sup-{Guid.NewGuid():N}"[..24],
            DisplayName = "Bell Supplier",
            ContactEmail = $"bell-supplier-{Guid.NewGuid():N}@example.com",
            OrgType = Casazen.Core.Entities.Enums.OrgType.Supplier,
            PlanTier = Casazen.Core.Entities.Enums.PlanTier.Starter,
        };
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Orgs.Add(supplierOrg);
            db.Users.Add(new User
            {
                Id = supplierId,
                Email = $"{Guid.NewGuid():N}@example.com",
                FirstName = "Sara",
                LastName = "Fornitore",
                OrgId = null,
                SupplierOrgId = supplierOrg.Id,
                Role = UserRole.Supplier,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var row = await AddAsync(supplierId, supplierOrg.Id, Day, PushTypes.ServiceRequestCreated);
        using var client = _factory.CreateAuthenticatedClient(supplierId, "Supplier");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications");
        var count = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications/unread-count");

        Assert.Equal([row.Id], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(1, count.GetProperty("count").GetInt32());
    }

    // ─── Read ───

    [PostgresFact]
    public async Task MarkRead_MarksOnlyThatNotification_AndAnotherCallIsStillNoContent()
    {
        var (me, org) = await SeedHostAsync("read-one");
        var target = await AddAsync(me, org, Day.AddMinutes(-2), PushTypes.NewBooking);
        var untouched = await AddAsync(me, org, Day.AddMinutes(-1), PushTypes.NewBooking);
        using var client = _factory.CreateAuthenticatedClient(me, "PropertyOwner");

        var first = await client.PostAsync($"/api/me/notifications/{target.Id}/read", null);
        var again = await client.PostAsync($"/api/me/notifications/{target.Id}/read", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var rows = await RowsAsync(me);
        Assert.NotNull(rows.Single(r => r.Id == target.Id).ReadAt);
        Assert.Null(rows.Single(r => r.Id == untouched.Id).ReadAt);
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/me/notifications/unread-count")).GetProperty("count").GetInt32());
    }

    [PostgresFact]
    public async Task MarkRead_ANotificationOfAnotherUserOrOneThatDoesNotExist_Is404_AndTheRowIsUntouched()
    {
        var (me, myOrg) = await SeedHostAsync("read-mine");
        var (other, otherOrg) = await SeedHostAsync("read-theirs");
        var theirs = await AddAsync(other, otherOrg, Day.AddMinutes(-1), PushTypes.NewBooking);
        var inForeignOrg = await AddAsync(me, otherOrg, Day.AddMinutes(-2), PushTypes.NewBooking);
        await AddAsync(me, myOrg, Day.AddMinutes(-3), PushTypes.NewBooking);
        using var client = _factory.CreateAuthenticatedClient(me, "PropertyOwner");

        foreach (var id in new[] { theirs.Id, inForeignOrg.Id, Guid.NewGuid() })
        {
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.PostAsync($"/api/me/notifications/{id}/read", null));
        }

        Assert.Null((await RowsAsync(other)).Single().ReadAt);
        Assert.Null((await RowsAsync(me)).Single(r => r.Id == inForeignOrg.Id).ReadAt);
    }

    [PostgresFact]
    public async Task ReadAll_MarksEveryNotificationOfTheCallerAndNoOtherUsers_AndRepeatingIsNoContent()
    {
        var (me, myOrg) = await SeedHostAsync("readall-me");
        var (other, otherOrg) = await SeedHostAsync("readall-other");
        await AddAsync(me, myOrg, Day.AddMinutes(-3), PushTypes.NewBooking);
        await AddAsync(me, myOrg, Day.AddMinutes(-2), PushTypes.AlloggiatiOverdue);
        var alreadyRead = await AddAsync(me, myOrg, Day.AddMinutes(-1), PushTypes.NewBooking, readAt: Day.AddMinutes(-1));
        await AddAsync(other, otherOrg, Day.AddMinutes(-1), PushTypes.NewBooking);
        using var client = _factory.CreateAuthenticatedClient(me, "PropertyOwner");

        var first = await client.PostAsync("/api/me/notifications/read-all", null);
        var again = await client.PostAsync("/api/me/notifications/read-all", null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.All(await RowsAsync(me), row => Assert.NotNull(row.ReadAt));
        // The one that was read already keeps the time it was read at.
        Assert.Equal(Day.AddMinutes(-1), (await RowsAsync(me)).Single(r => r.Id == alreadyRead.Id).ReadAt);
        Assert.Null((await RowsAsync(other)).Single().ReadAt);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/me/notifications/unread-count")).GetProperty("count").GetInt32());
    }

    // ─── The whole chain ───

    [Fact]
    public async Task APushQueuedByAService_GivesTheUserOneNotification_WhateverTheNumberOfRunsOfTheJob()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|chain-{Guid.NewGuid():N}");
        var requestId = Guid.NewGuid();
        var key = PushDeliveryKeys.ServiceRequestStatus(requestId, Casazen.Core.Entities.Enums.ServiceRequestStatus.Completato);

        // What a service does (ServiceRequestNotifier, BookingNotifier, ...): ask for the push. Nothing else.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IPushNotificationService>().Enqueue(
                key,
                PushAudience.PropertyHosts(property.Id),
                new PushNotificationPayload(
                    "Lavoro completato", "Pulizie presso Casa Test.", PushTypes.ServiceRequestCompleted, null, PushRoutes.Properties, requestId));
        }

        var jobs = QueuedJobs(key);
        Assert.Equal([typeof(PushDeliveryJob), typeof(InAppNotificationJob)], jobs.Select(j => j.Type));

        // The worker runs the notification job, and once more (a Hangfire retry): still one row.
        var notification = jobs.Single(j => j.Type == typeof(InAppNotificationJob));
        for (var run = 0; run < 2; run++)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<InAppNotificationJob>()
                .CreateAsync((string)notification.Args[0], (QueuedInAppNotification)notification.Args[1], CancellationToken.None);
        }

        using var client = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(PushTypes.ServiceRequestCompleted, item.GetProperty("type").GetString());
        Assert.Equal(requestId, item.GetProperty("entityId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/me/notifications/unread-count")).GetProperty("count").GetInt32());
        // The push text is not in the answer or in the table.
        Assert.DoesNotContain("Pulizie", list.GetRawText());
    }

    // ─── Helpers ───

    /// <summary>The answers about a user are private and never cached (the bell polls them).</summary>
    private static void AssertPrivateAndNeverCached(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    private async Task<(string UserId, Guid OrgId)> SeedHostAsync(string prefix)
    {
        var userId = $"auth0|{prefix}-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);
        return (userId, org.Id);
    }

    private async Task<InAppNotification> AddAsync(string userId, Guid orgId, DateTime createdAt, string type, DateTime? readAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new InAppNotification
        {
            UserId = userId,
            OrgId = orgId,
            Type = type,
            EntityId = Guid.NewGuid(),
            DeliveryKey = $"test:{Guid.NewGuid():N}",
            CreatedAt = createdAt,
            ReadAt = readAt,
        };
        db.InAppNotifications.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task<List<InAppNotification>> RowsAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.InAppNotifications.IgnoreQueryFilters().AsNoTracking().Where(n => n.UserId == userId).ToListAsync();
    }

    private List<Job> QueuedJobs(string deliveryKey) =>
        _factory.BackgroundJobClientMock.Invocations
            .Where(i => i.Method.Name == nameof(Hangfire.IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Where(j => j.Args.Count > 0 && j.Args[0] is string key && key == deliveryKey)
            .ToList();

    /// <summary>The integration host with <c>Features:InAppNotifications</c> on.</summary>
    public sealed class EnabledFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:InAppNotifications"] = "true" }));
        }
    }
}

/// <summary>
/// UI-12a with the default configuration (<c>Features:InAppNotifications</c> off): the four endpoints answer 404 like a route
/// that does not exist, before authentication, a push queues the push and nothing else, and nothing is written.
/// </summary>
public class InAppNotificationsFlagOffIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public InAppNotificationsFlagOffIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Endpoints_FlagOff_Answer404ToTheOwnerAndToAnonymous()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|bell-off-{Guid.NewGuid():N}");
        using var owner = _factory.CreateAuthenticatedClient(property.OwnerId, "PropertyOwner");
        using var anonymous = _factory.CreateClient();

        foreach (var client in new[] { owner, anonymous })
        {
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.GetAsync("/api/me/notifications"));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.GetAsync("/api/me/notifications/unread-count"));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(
                await client.PostAsync($"/api/me/notifications/{Guid.NewGuid()}/read", null));
            await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(await client.PostAsync("/api/me/notifications/read-all", null));
        }
    }

    [Fact]
    public async Task APush_FlagOff_QueuesThePushAndNothingElse_AndNothingIsWritten()
    {
        var property = await _factory.SeedPropertyAsync($"auth0|bell-off-push-{Guid.NewGuid():N}");
        var key = $"service-request:{Guid.NewGuid():N}:Completato";

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IPushNotificationService>().Enqueue(
                key,
                PushAudience.PropertyHosts(property.Id),
                new PushNotificationPayload("t", "b", PushTypes.ServiceRequestCompleted, null, PushRoutes.Properties, Guid.NewGuid()));
        }

        var jobs = _factory.BackgroundJobClientMock.Invocations
            .Where(i => i.Method.Name == nameof(Hangfire.IBackgroundJobClient.Create))
            .Select(i => (Job)i.Arguments[0])
            .Where(j => j.Args.Count > 0 && j.Args[0] is string queuedKey && queuedKey == key)
            .ToList();
        Assert.Equal([typeof(PushDeliveryJob)], jobs.Select(j => j.Type));
        using var check = _factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().InAppNotifications.IgnoreQueryFilters().AnyAsync(n => n.DeliveryKey == key));
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsInAppNotificationsOff()
    {
        using var anonymous = _factory.CreateClient();

        var features = await anonymous.GetFromJsonAsync<JsonElement>("/api/public/features");

        Assert.False(features.GetProperty("inAppNotifications").GetBoolean());
    }
}
