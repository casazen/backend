using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-04 on PostgreSQL, where it counts: the slot of a request is judged by the planner <b>under the supplier's calendar lock</b>
/// (<c>SupplierCalendarSync</c>, the lock of the agenda writes and of the iCal sync), after taking it, so two requests for the
/// same hour never both get it: parallel creates, takes and acceptances of a proposal for one slot have exactly one winner and
/// the others get 409 <c>supplier_slot_unavailable</c>. A change with no time does not take the lock, and the lock of one
/// supplier never holds back another. Skipped locally when no PostgreSQL is available and always run on CI.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServiceRequestSchedulePostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    // ─── Parallel changes for one slot: exactly one winner ───

    [PostgresFact]
    public async Task Create_ParallelRequestsForTheSameSlot_ExactlyOneGetsItAndTheOthersGet409()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        const int attempts = 6;

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
        {
            using var host = factory.Host(world);
            return await host.PostAsJsonAsync("/api/service-requests", CreateBody(world, slot));
        }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("supplier_slot_unavailable", (await ReadAsync(refused)).GetProperty("code").GetString());
        }

        Assert.Equal(1, await CountAtAsync(world, slot));
    }

    [PostgresFact]
    public async Task Create_ParallelRequestsForDifferentDays_AllGetTheirSlot()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slots = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday }
            .Select(day => ServiceRequestWorlds.Slot(day: day))
            .ToList();

        var responses = await Task.WhenAll(slots.Select(async slot =>
        {
            using var host = factory.Host(world);
            return await host.PostAsJsonAsync("/api/service-requests", CreateBody(world, slot));
        }));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        foreach (var slot in slots)
            Assert.Equal(1, await CountAtAsync(world, slot));
    }

    [PostgresFact]
    public async Task Take_TwoNewRequestsInParallelAtTheSameTime_ExactlyOneGetsTheSlot()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var first = await factory.CreateRequestAsync(world);
        var second = await factory.CreateRequestAsync(world);
        var slot = ServiceRequestWorlds.Slot();

        var responses = await Task.WhenAll(
            TakeAsync(world, first, new { scheduledStartUtc = slot }),
            TakeAsync(world, second, new { scheduledStartUtc = slot }));

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(r => r.StatusCode).Order().ToArray());
        var refused = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("supplier_slot_unavailable", (await ReadAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(1, await CountAtAsync(world, slot));
        // The one that lost is still new and still without a time.
        var loser = await factory.LoadRequestAsync(first == await WinnerAsync(world, slot) ? second : first);
        Assert.Equal(ServiceRequestStatus.Richiesto, loser.Status);
        Assert.Null(loser.ScheduledStartUtc);
    }

    [PostgresFact]
    public async Task CreateWithATimeAndTakeWithTheSameTime_InParallel_ExactlyOneWins()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var waiting = await factory.CreateRequestAsync(world);
        var slot = ServiceRequestWorlds.Slot();

        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                using var host = factory.Host(world);
                return await host.PostAsJsonAsync("/api/service-requests", CreateBody(world, slot));
            }),
            TakeAsync(world, waiting, new { scheduledStartUtc = slot }));

        var statuses = results.Select(r => r.StatusCode).ToList();
        Assert.Single(statuses, status => status is HttpStatusCode.Created or HttpStatusCode.OK);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        Assert.Equal(1, await CountAtAsync(world, slot));
    }

    [PostgresFact]
    public async Task AcceptingAProposalAndCreatingForTheSameSlot_InParallel_ExactlyOneWins()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var waiting = await factory.CreateRequestAsync(world);
        var slot = ServiceRequestWorlds.Slot(hour: 14);
        using (var supplier = factory.Supplier(world))
        {
            var proposal = await supplier.PostAsJsonAsync($"/api/service-requests/{waiting}/propose-time", new { startUtc = slot });
            Assert.Equal(HttpStatusCode.OK, proposal.StatusCode);
        }

        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                using var host = factory.Host(world);
                return await host.PostAsync($"/api/service-requests/{waiting}/proposal/accept", content: null);
            }),
            Task.Run(async () =>
            {
                using var host = factory.Host(world);
                return await host.PostAsJsonAsync("/api/service-requests", CreateBody(world, slot));
            }));

        var statuses = results.Select(r => r.StatusCode).ToList();
        Assert.Single(statuses, status => status is HttpStatusCode.Created or HttpStatusCode.OK);
        Assert.Single(statuses, status => status == HttpStatusCode.Conflict);
        Assert.Equal(1, await CountAtAsync(world, slot));
    }

    [PostgresFact]
    public async Task Create_TheSecondRequestAfterTheFirstCommitted_SeesItAndIsRefused()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var slot = ServiceRequestWorlds.Slot();
        await factory.CreateRequestAsync(world, slot);
        using var host = factory.Host(world);

        var response = await host.PostAsJsonAsync("/api/service-requests", CreateBody(world, slot));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ─── The lock itself ───

    [PostgresTheory]
    [InlineData("create")]
    [InlineData("take")]
    [InlineData("propose")]
    public async Task ARequestWithATime_WaitsForTheCalendarLockOfItsSupplier(string operation)
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var waiting = await factory.CreateRequestAsync(world);
        var slot = ServiceRequestWorlds.Slot();

        await using var holder = await HoldTheLockAsync(world.SupplierOrgId);
        var pending = Task.Run(async () => operation switch
        {
            "create" => await PostAsHostAsync(world, "/api/service-requests", CreateBody(world, slot)),
            "take" => await TakeAsync(world, waiting, new { scheduledStartUtc = slot }),
            _ => await PostAsSupplierAsync(world, $"/api/service-requests/{waiting}/propose-time", new { startUtc = slot }),
        });
        await WaitForAdvisoryLockWaitersAsync(1);
        await Task.Delay(300);
        Assert.False(pending.IsCompleted, $"{operation} with a time did not wait for the lock of the supplier's calendar");

        await holder.ReleaseAsync();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(response.IsSuccessStatusCode, $"{operation}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    [PostgresFact]
    public async Task ARequestWithoutATime_DoesNotWaitForTheLock()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var waiting = await factory.CreateRequestAsync(world);
        await using var holder = await HoldTheLockAsync(world.SupplierOrgId);

        // Nothing here is a slot: the host asks without a time and the supplier takes without one.
        var created = await PostAsHostAsync(world, "/api/service-requests", CreateBody(world, slot: null)).WaitAsync(TimeSpan.FromSeconds(30));
        var taken = await TakeAsync(world, waiting, new { }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    [PostgresFact]
    public async Task TheLockOfOneSupplier_NeverHoldsBackTheRequestsOfAnother()
    {
        var busy = await ServiceRequestWorlds.SeedAsync(factory);
        var other = await ServiceRequestWorlds.SeedAsync(factory);
        await using var holder = await HoldTheLockAsync(busy.SupplierOrgId);

        var created = await PostAsHostAsync(other, "/api/service-requests", CreateBody(other, ServiceRequestWorlds.Slot()))
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [PostgresFact]
    public async Task ARefusedSlot_LeavesNoRowAndReleasesTheLockForTheNextOne()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var sunday = ServiceRequestWorlds.Slot(day: DayOfWeek.Sunday);
        var free = ServiceRequestWorlds.Slot();

        var refused = await PostAsHostAsync(world, "/api/service-requests", CreateBody(world, sunday));
        var accepted = await PostAsHostAsync(world, "/api/service-requests", CreateBody(world, free)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(0, await CountAtAsync(world, sunday));
        Assert.Equal(1, await CountAtAsync(world, free));
    }

    // ─── helpers ───

    private static object CreateBody(ServiceRequestWorld world, DateTime? slot) => new
    {
        propertyId = world.PropertyId,
        bookingId = world.BookingId,
        supplierOrgId = world.SupplierOrgId,
        category = "cleaning",
        serviceListingId = world.ListingId,
        scheduledStartUtc = slot,
    };

    private async Task<HttpResponseMessage> PostAsHostAsync(ServiceRequestWorld world, string url, object body)
    {
        using var host = factory.Host(world);
        return await host.PostAsJsonAsync(url, body);
    }

    private async Task<HttpResponseMessage> PostAsSupplierAsync(ServiceRequestWorld world, string url, object body)
    {
        using var supplier = factory.Supplier(world);
        return await supplier.PostAsJsonAsync(url, body);
    }

    private Task<HttpResponseMessage> TakeAsync(ServiceRequestWorld world, Guid id, object body) =>
        Task.Run(() => PostAsSupplierAsync(world, $"/api/service-requests/{id}/take", body));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private async Task<int> CountAtAsync(ServiceRequestWorld world, DateTime slot)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.SupplierOrgId == world.SupplierOrgId && r.ScheduledStartUtc == slot);
    }

    /// <summary>The id of the request of the supplier that holds the slot.</summary>
    private async Task<Guid> WinnerAsync(ServiceRequestWorld world, DateTime slot)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters()
            .Where(r => r.SupplierOrgId == world.SupplierOrgId && r.ScheduledStartUtc == slot)
            .Select(r => r.Id)
            .SingleAsync();
    }

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    /// <summary>Another connection holding the calendar lock of a supplier, as an iCal sync that is writing its days would.</summary>
    private async Task<LockHolder> HoldTheLockAsync(Guid supplierOrgId)
    {
        var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        var (scope, key) = CalendarSyncService.AvailabilityLock(supplierOrgId);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@scope, @key)", connection, transaction))
        {
            command.Parameters.AddWithValue("scope", (int)scope);
            command.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash(key));
            await command.ExecuteNonQueryAsync();
        }

        return new LockHolder(connection, transaction);
    }

    private async Task WaitForAdvisoryLockWaitersAsync(int count)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid = l.database " +
                "WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()",
                connection);
            if ((long)(await command.ExecuteScalarAsync())! >= count)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fewer than {count} sessions waited on the lock of the supplier's calendar.");
    }

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool _released;

        public async Task ReleaseAsync()
        {
            if (_released)
                return;

            _released = true;
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
