using System.Net;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Casazen.Tests.Integration.LeaseDraftHttp;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-OVERLAP over the real pipeline: two leases of one property cannot cover the same days. A lease that follows another one is fine,
/// so is the same days on another property and a lease that confirms again; the wizard tells the lease in the way as soon as the property
/// and the dates are there, and the steps that make the lease a contract (here the confirmation of the review) answer 409
/// <c>lease_period_overlaps</c> with the id and the days of the other lease and nobody's name. On PostgreSQL the check is made under the
/// lock of the property, so it sees what a request that was ahead of it committed. The host is the one of the guided draft, "today"
/// 2026-10-09.
/// </summary>
public class LeasePeriodOverlapIntegrationTests(LeaseDraftFactory factory) : IClassFixture<LeaseDraftFactory>, IAsyncLifetime
{
    private LongRentWorld _world = null!;

    public async Task InitializeAsync() => _world = await LongRentWorld.SeedAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task ConfirmAsync(HttpClient client, Guid id) =>
        await ExpectAsync(await client.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")), HttpStatusCode.OK);

    [Fact]
    public async Task TwoLeasesOfOneProperty_CannotCoverTheSameDays_TheDaysThatFollowAreFree_AndSoIsAnotherProperty()
    {
        using var client = _world.OwnerClient(factory);
        var property = await factory.AddReadyPropertyAsync(_world.OrgId, _world.OwnerId);
        var anotherProperty = await factory.AddReadyPropertyAsync(_world.OrgId, _world.OwnerId);

        // The first lease takes 1 November 2026 to 31 October 2030, and confirming it again changes nothing: it is not in its own way.
        var (first, _) = await CompleteAsync(client, property, startDate: "2026-11-01");
        await ConfirmAsync(client, first);
        await ConfirmAsync(client, first);

        // The same days on another property, and the days that start the day after the first one ends: both go through.
        var (elsewhere, _) = await CompleteAsync(client, anotherProperty, startDate: "2026-11-01");
        await ConfirmAsync(client, elsewhere);
        var (following, _) = await CompleteAsync(client, property, startDate: "2030-11-01");
        await ConfirmAsync(client, following);

        // A lease that starts in the middle of the first: the wizard says so as soon as the dates are there...
        var (second, secondVersion) = await CompleteAsync(client, property, startDate: "2027-06-01");
        var draft = await JsonAsync(await client.GetAsync($"/api/leases/{second}/draft"));
        var warning = draft.GetProperty("flow").GetProperty("periodConflict");
        Assert.Equal(first, warning.GetProperty("conflictingLeaseId").GetGuid());
        Assert.Equal("2026-11-01", warning.GetProperty("conflictingFrom").GetString());
        Assert.Equal("2030-10-31", warning.GetProperty("conflictingTo").GetString());

        // ...the review lists it as a block, with the same facts...
        var review = await JsonAsync(await client.GetAsync($"/api/leases/{second}/review"));
        var block = Assert.Single(review.GetProperty("checks").EnumerateArray(), c => c.GetProperty("code").GetString() == "lease_overlap");
        Assert.Equal("Block", block.GetProperty("severity").GetString());
        Assert.Equal(first, block.GetProperty("params").GetProperty("conflictingLeaseId").GetGuid());

        // ...and the confirmation is refused: 409, the other lease's id and days, no name of a person, nothing written.
        var problem = await ExpectProblemAsync(
            await client.PostAsync($"/api/leases/{second}/review/confirm", Body("{}")), HttpStatusCode.Conflict, "lease_period_overlaps");
        Assert.Equal(first, problem.GetProperty("conflictingLeaseId").GetGuid());
        Assert.Equal("2026-11-01", problem.GetProperty("conflictingFrom").GetString());
        Assert.Equal("2030-10-31", problem.GetProperty("conflictingTo").GetString());
        Assert.DoesNotContain("Verdi", problem.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Giulia", problem.GetRawText(), StringComparison.Ordinal);
        var stillOpen = await JsonAsync(await client.GetAsync($"/api/leases/{second}/draft"));
        Assert.Equal(secondVersion, stillOpen.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, stillOpen.GetProperty("reviewConfirmedAt").ValueKind);
    }

    [PostgresFact]
    public async Task Confirm_WhileARequestAheadOfItHoldsTheDaysOfTheProperty_WaitsForIt_AndThenSeesWhatItCommitted()
    {
        using var client = _world.OwnerClient(factory);
        var property = await factory.AddReadyPropertyAsync(_world.OrgId, _world.OwnerId);
        var (mine, _) = await CompleteAsync(client, property, startDate: "2026-11-01");

        // A request ahead of ours holds the lock of the days of the property and has written a lease on them that is not committed yet.
        await using var scope = factory.Services.CreateAsyncScope();
        var holder = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var held = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            holder, CancellationToken.None, (PostgresAdvisoryLocks.Scope.LeasePeriod, property.ToString("N")));
        Assert.NotNull(held);
        var ahead = new LeaseContract
        {
            PropertyId = property,
            OrgId = _world.OrgId,
            Status = LeaseStatus.Registered,
            StartDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 800m,
        };
        holder.LeaseContracts.Add(ahead);
        await holder.SaveChangesAsync();

        var confirmation = client.PostAsync($"/api/leases/{mine}/review/confirm", Body("{}"));
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // It waits for the lock: without it the lease that is not committed would not be seen and the confirmation would be over by now.
        Assert.False(confirmation.IsCompleted, "the confirmation waits for the lock of the days of the property");

        await held.CommitAsync();

        // Once the request ahead has committed, ours sees its lease and is refused.
        var problem = await ExpectProblemAsync(await confirmation, HttpStatusCode.Conflict, "lease_period_overlaps");
        Assert.Equal(ahead.Id, problem.GetProperty("conflictingLeaseId").GetGuid());
        Assert.Equal("2027-01-01", problem.GetProperty("conflictingFrom").GetString());
        Assert.Equal("2030-12-31", problem.GetProperty("conflictingTo").GetString());
    }
}
