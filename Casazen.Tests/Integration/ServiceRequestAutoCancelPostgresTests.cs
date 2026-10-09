using System.Net;
using System.Net.Http.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Hangfire.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>The default integration factory with the flag <c>SupplierRequestAutoCancel</c> on (it is off by default).</summary>
public sealed class AutoCancelEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:SupplierRequestAutoCancel"] = "true" }));
    }
}

/// <summary>
/// SP-04 (decision D8) on PostgreSQL: the run that cancels the requests nobody answered, on the real database. One run at a
/// time (a session advisory lock), idempotent, and the loser of a race with the supplier's take (<c>xmin</c>) leaves the take
/// alone. Skipped locally when no PostgreSQL is available and always run on CI.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServiceRequestAutoCancelPostgresTests(AutoCancelEnabledFactory factory) : IClassFixture<AutoCancelEnabledFactory>
{
    [PostgresFact]
    public async Task Run_CancelsTheOverdueRequestAndQueuesTheMessagesOfBothParties()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        await factory.ChangeRequestAsync(id, r => r.ResponseDueAt = DateTime.UtcNow.AddMinutes(-1));

        var run = await RunAsync();

        Assert.True(run.Cancelled >= 1, $"Cancelled {run.Cancelled}");
        var stored = await factory.LoadRequestAsync(id);
        Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
        Assert.Equal(ServiceRequestActorParty.System, stored.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, stored.CancellationReason);
        Assert.NotNull(stored.CancelledAt);
        Assert.Null(stored.ResponseDueAt);

        // One push for the host and one for the supplier, queued for the Hangfire worker.
        var key = PushDeliveryKeys.ServiceRequestStatus(id, ServiceRequestStatus.Annullato);
        Assert.Equal(2, QueuedPushJobs().Count(job => (string)job.Args[0] == key));
    }

    [PostgresFact]
    public async Task Run_ARequestThatIsNotYetDue_OrWasAnswered_IsLeftAlone()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var notDue = await factory.CreateRequestAsync(world);
        var taken = await factory.CreateRequestAsync(world);
        var withProposal = await factory.CreateRequestAsync(world);
        using (var supplier = factory.Supplier(world))
        {
            Assert.Equal(HttpStatusCode.OK, (await supplier.PostAsync($"/api/service-requests/{taken}/take", content: null)).StatusCode);
            var proposal = await supplier.PostAsJsonAsync(
                $"/api/service-requests/{withProposal}/propose-time", new { startUtc = ServiceRequestWorlds.Slot(hour: 14) });
            Assert.Equal(HttpStatusCode.OK, proposal.StatusCode);
        }

        // Past the deadline of every one of them; only the one nobody answered is cancelled.
        var past = DateTime.UtcNow.AddMinutes(-5);
        await factory.ChangeRequestAsync(taken, r => r.ResponseDueAt = past);
        await factory.ChangeRequestAsync(withProposal, r => r.ResponseDueAt = past);

        await RunAsync();

        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(notDue)).Status);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await factory.LoadRequestAsync(taken)).Status);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(withProposal)).Status);
    }

    [PostgresFact]
    public async Task Run_TwiceInARow_TheSecondOneCancelsNothingAndQueuesNothingMore()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        await factory.ChangeRequestAsync(id, r => r.ResponseDueAt = DateTime.UtcNow.AddMinutes(-1));
        await RunAsync();
        var key = PushDeliveryKeys.ServiceRequestStatus(id, ServiceRequestStatus.Annullato);
        var queued = QueuedPushJobs().Count(job => (string)job.Args[0] == key);

        var second = await RunAsync();

        Assert.Equal(0, second.Cancelled);
        Assert.Equal(queued, QueuedPushJobs().Count(job => (string)job.Args[0] == key));
        Assert.Equal(ServiceRequestStatus.Annullato, (await factory.LoadRequestAsync(id)).Status);
    }

    [PostgresFact]
    public async Task Run_WhileAnotherRunHoldsTheSessionLock_IsSkippedAndCancelsNothing()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        await factory.ChangeRequestAsync(id, r => r.ResponseDueAt = DateTime.UtcNow.AddMinutes(-1));

        await using (var holderScope = factory.Services.CreateAsyncScope())
        {
            var holderDb = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var held = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                holderDb, PostgresAdvisoryLocks.Scope.ServiceRequestAutoCancelRun, ServiceRequestAutoCancelService.RunLockKey, CancellationToken.None);
            Assert.NotNull(held);

            var skipped = await RunAsync();

            Assert.True(skipped.Skipped);
            Assert.False(skipped.Disabled);
            Assert.Equal(0, skipped.Cancelled);
            Assert.Equal(ServiceRequestStatus.Richiesto, (await factory.LoadRequestAsync(id)).Status);
        }

        // The lock is released with the handle: the next run takes it and does the work.
        var run = await RunAsync();
        Assert.False(run.Skipped);
        Assert.True(run.Cancelled >= 1);
        Assert.Equal(ServiceRequestStatus.Annullato, (await factory.LoadRequestAsync(id)).Status);
    }

    [PostgresFact]
    public async Task Run_ATakeAndTheRunAtTheSameMoment_OneWinsAndTheOtherLeavesTheRequestAlone()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        var id = await factory.CreateRequestAsync(world);
        await factory.ChangeRequestAsync(id, r => r.ResponseDueAt = DateTime.UtcNow.AddMinutes(-1));
        // The run and the take read the request as unanswered and are held before saving until both got there.
        await using var app = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            SaveRendezvous.HoldParallelSaves(services, parties: 2)));

        await using var scope = app.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<IServiceRequestAutoCancelService>();
        var runTask = job.CancelUnansweredAsync();
        var takeTask = Task.Run(async () =>
        {
            using var client = AppClient(app, world);
            return await client.PostAsJsonAsync($"/api/service-requests/{id}/take", new { });
        });

        var run = await runTask;
        var take = await takeTask;

        var stored = await factory.LoadRequestAsync(id);
        if (take.StatusCode == HttpStatusCode.OK)
        {
            // The supplier answered first: the run counted a conflict and left the take as it is.
            Assert.Equal(ServiceRequestStatus.PresoInCarico, stored.Status);
            Assert.NotNull(stored.TakenAt);
            Assert.True(run.Conflicts >= 1, $"Conflicts {run.Conflicts}");
        }
        else
        {
            // The run cancelled first: the take got 409 and nothing of it was saved.
            Assert.Equal(HttpStatusCode.Conflict, take.StatusCode);
            Assert.Equal(ServiceRequestStatus.Annullato, stored.Status);
            Assert.Null(stored.TakenAt);
            Assert.True(run.Cancelled >= 1, $"Cancelled {run.Cancelled}");
        }
    }

    // ─── helpers ───

    private async Task<ServiceRequestAutoCancelRun> RunAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceRequestAutoCancelService>().CancelUnansweredAsync();
    }

    private IReadOnlyList<Job> QueuedPushJobs() =>
        factory.BackgroundJobClientMock.Invocations
            .Where(invocation => invocation.Method.Name == nameof(Hangfire.IBackgroundJobClient.Create))
            .Select(invocation => (Job)invocation.Arguments[0])
            .Where(job => job.Type == typeof(PushDeliveryJob))
            .ToList();

    private static HttpClient AppClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, ServiceRequestWorld world)
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", world.SupplierUserId);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Supplier");
        return client;
    }
}
