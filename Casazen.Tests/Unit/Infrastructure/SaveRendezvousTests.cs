using System.Collections.Concurrent;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The harness of the parallel-transition tests of SP-04 (<see cref="SaveRendezvous"/>), tried without PostgreSQL: that it holds the
/// saves of two requests, which live in two scopes of one host, until both have arrived, and why the five tests that use it failed
/// the first time they ran in CI (an interceptor made inside the <c>ConfigureDbContext</c> lambda is a new object for every scope).
/// The race itself, and the 409 the loser gets, need the <c>xmin</c> of PostgreSQL and are in <c>ServiceRequestTransitionsPostgresTests</c>.
/// </summary>
public class SaveRendezvousTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task TwoSavesInTwoScopes_AreHeldUntilBothHaveArrived_ThenBothGoThrough()
    {
        var services = NewServices();
        var rendezvous = SaveRendezvous.HoldParallelSaves(services, parties: 2);
        await using var provider = services.BuildServiceProvider();
        var id = await SeedRequestAsync(provider);

        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await firstDb.ServiceRequests.SingleAsync(r => r.Id == id)).Notes = "first";
        (await secondDb.ServiceRequests.SingleAsync(r => r.Id == id)).Notes = "second";

        var firstSave = firstDb.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.False(firstSave.IsCompleted); // alone, it waits for the other one
        Assert.Equal(1, rendezvous.Arrived);

        var secondSave = secondDb.SaveChangesAsync();
        await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, rendezvous.Arrived);
    }

    [Fact]
    public async Task AnInterceptorBuiltInsideTheLambda_IsANewObjectForEveryScope_SoTheSavesOfTwoRequestsNeverMeet()
    {
        var services = NewServices();
        var built = new ConcurrentBag<SaveRendezvous>();
        // What the five parallel-transition tests did: the rendezvous is made where the options are built, which happens for every scope.
        services.ConfigureDbContext<AppDbContext>(options =>
        {
            var rendezvous = new SaveRendezvous(parties: 2, ShortTimeout);
            built.Add(rendezvous);
            options.AddInterceptors(rendezvous);
        });
        await using var provider = services.BuildServiceProvider();
        var id = await SeedRequestAsync(provider);

        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await firstDb.ServiceRequests.SingleAsync(r => r.Id == id)).Notes = "first";
        (await secondDb.ServiceRequests.SingleAsync(r => r.Id == id)).Notes = "second";

        var failures = await Task.WhenAll(FailureOf(firstDb.SaveChangesAsync()), FailureOf(secondDb.SaveChangesAsync()));

        // Each save waited alone for a second party that was in another object, and the time ran out: the API answers that with 500.
        Assert.All(failures, failure =>
        {
            var timeout = Assert.IsType<TimeoutException>(failure);
            Assert.Contains("1 of 2", timeout.Message);
            Assert.Contains(nameof(SaveRendezvous.HoldParallelSaves), timeout.Message);
        });
        Assert.Equal(2, built.Count(rendezvous => rendezvous.Arrived == 1));
        Assert.DoesNotContain(built, rendezvous => rendezvous.Arrived >= 2);
    }

    [Fact]
    public async Task TheInsertOfARequest_IsNotHeld()
    {
        var services = NewServices();
        var rendezvous = SaveRendezvous.HoldParallelSaves(services, parties: 2, ShortTimeout);
        await using var provider = services.BuildServiceProvider();

        await SeedRequestAsync(provider); // would fail with a TimeoutException after half a second if it were held

        Assert.Equal(0, rendezvous.Arrived);
    }

    private static ServiceCollection NewServices()
    {
        // The name is made once: the lambda below runs for every scope, and each scope must see the same database.
        var databaseName = $"save-rendezvous-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        return services;
    }

    private static async Task<Guid> SeedRequestAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new ServiceRequest
        {
            OrgId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            SupplierOrgId = Guid.NewGuid(),
            Category = "cleaning",
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private static async Task<Exception?> FailureOf(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
