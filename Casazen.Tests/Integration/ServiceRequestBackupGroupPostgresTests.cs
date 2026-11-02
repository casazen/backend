using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>The default integration factory with the flag <c>SupplierRequestBackups</c> on (it is off by default).</summary>
public sealed class BackupGroupsEnabledFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:SupplierRequestBackups"] = "true" }));
    }
}

/// <summary>
/// SP-BKP on PostgreSQL: the backup suppliers of a request, over the real pipeline and the real database. The group and its requests are
/// written in one save, the take and the pre-approval run under the group's advisory lock, the recurring job takes its session lock and
/// follows the group to its end, and every query that tells a backup that waits from a request the host follows is translated by Npgsql.
/// Each test has a world of its own (a host, the supplier it chose and two more suppliers of a comune nobody else covers), so the groups of
/// one test are never the backups of another. Skipped locally when no PostgreSQL is available and always run on CI.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServiceRequestBackupGroupPostgresTests(BackupGroupsEnabledFactory factory) : IClassFixture<BackupGroupsEnabledFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [PostgresFact]
    public async Task PrimaryTakes_TheBackupsAreClosed_AndTheHostSeesOnlyItsOwnRequestWithTheGroup()
    {
        var world = await SeedAsync();
        var id = await factory.CreateRequestAsync(world.Base);

        var groupId = await GroupIdOfAsync(id);
        var members = await MembersAsync(groupId);
        Assert.Equal(3, members.Count);
        Assert.Equal(ServiceRequestGroupRole.Primary, members.Single(m => m.Id == id).GroupRole);
        Assert.Equal(
            new[] { world.BackupA.OrgId, world.BackupB.OrgId }.Order(),
            members.Where(m => m.GroupRole == ServiceRequestGroupRole.Backup).Select(m => m.SupplierOrgId).Order());
        var group = await LoadGroupAsync(groupId);
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, group.Phase);
        Assert.All(members, m => Assert.Equal(group.PrimaryDueAt, m.ResponseDueAt));

        // The host sees the request it made, and the group inside it: never a backup as a request of its own.
        using (var host = factory.Host(world.Base))
        {
            var list = await host.GetFromJsonAsync<JsonElement>($"/api/service-requests?propertyId={world.Base.PropertyId}", JsonOptions);
            var visible = Assert.Single(list.GetProperty("items").EnumerateArray());
            Assert.Equal(id, visible.GetProperty("id").GetGuid());
            var shown = visible.GetProperty("group");
            Assert.Equal(nameof(ServiceRequestGroupPhase.PrimaryWindow), shown.GetProperty("phase").GetString());
            Assert.Equal(
                new[] { nameof(ServiceRequestGroupRole.Primary), nameof(ServiceRequestGroupRole.Backup), nameof(ServiceRequestGroupRole.Backup) },
                shown.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToList());
            Assert.Equal(1, list.GetProperty("total").GetInt32());
        }

        // A backup sees its request as a backup: the comune, the time and the price, never the property or the host's notes (decision D9).
        var requestA = members.Single(m => m.SupplierOrgId == world.BackupA.OrgId);
        using (var backup = factory.CreateAuthenticatedClient(world.BackupA.UserId, "Supplier"))
        {
            var detail = await backup.GetAsync($"/api/supplier/inbox/{requestA.Id}");
            var text = await detail.Content.ReadAsStringAsync();
            Assert.True(detail.StatusCode == HttpStatusCode.OK, text);
            Assert.DoesNotContain(ServiceRequestWorlds.HostNotes, text);
            Assert.DoesNotContain(ServiceRequestWorlds.PropertyName, text);
            Assert.DoesNotContain(ServiceRequestWorlds.PropertyAddress, text);
            var shown = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("backup");
            Assert.Equal(SupplierBackupRoles.Backup, shown.GetProperty("role").GetString());
            Assert.Equal(SupplierBackupStates.Waiting, shown.GetProperty("state").GetString());

            // Within the primary's hour a backup can only say it would take the job.
            var early = await backup.PostAsJsonAsync($"/api/service-requests/{requestA.Id}/take", new { });
            await AssertProblemAsync(early, HttpStatusCode.UnprocessableEntity, ServiceRequestGroupErrorCodes.BackupWaitsForPrimary);
        }

        using (var primary = factory.Supplier(world.Base))
        {
            var take = await primary.PostAsync($"/api/service-requests/{id}/take", content: null);
            Assert.True(take.StatusCode == HttpStatusCode.OK, await take.Content.ReadAsStringAsync());
        }

        var after = await MembersAsync(groupId);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, after.Single(m => m.Id == id).Status);
        var closed = after.Where(m => m.GroupRole == ServiceRequestGroupRole.Backup).ToList();
        Assert.Equal(2, closed.Count);
        Assert.All(closed, m =>
        {
            Assert.Equal(ServiceRequestStatus.Annullato, m.Status);
            Assert.Equal(ServiceRequestActorParty.System, m.CancelledBy);
            Assert.Equal(ServiceRequestCancellationReasons.PrimaryTookIt, m.CancellationReason);
        });
        var ended = await LoadGroupAsync(groupId);
        Assert.Equal(ServiceRequestGroupPhase.Closed, ended.Phase);
        Assert.NotNull(ended.ClosedAt);
    }

    [PostgresFact]
    public async Task SilentPrimary_TheBackupThatAcceptedFirst_ClosesTheRace_AndGetsTheJobWhenTheJobRuns()
    {
        var world = await SeedAsync();
        var id = await factory.CreateRequestAsync(world.Base);
        var groupId = await GroupIdOfAsync(id);
        var members = await MembersAsync(groupId);
        var requestA = members.Single(m => m.SupplierOrgId == world.BackupA.OrgId);
        var requestB = members.Single(m => m.SupplierOrgId == world.BackupB.OrgId);

        using (var a = factory.CreateAuthenticatedClient(world.BackupA.UserId, "Supplier"))
        using (var b = factory.CreateAuthenticatedClient(world.BackupB.UserId, "Supplier"))
        {
            await AcceptAsBackupAsync(a, requestA.Id);

            // The first backup that accepts closes the race: the other one finds it closed (409 with its own code), and it loses nothing.
            var late = await b.PostAsync($"/api/service-requests/{requestB.Id}/accept-as-backup", content: null);
            await AssertProblemAsync(late, HttpStatusCode.Conflict, ServiceRequestGroupErrorCodes.BackupRaceClosed);
            var take = await b.PostAsJsonAsync($"/api/service-requests/{requestB.Id}/take", new { });
            await AssertProblemAsync(take, HttpStatusCode.Conflict, ServiceRequestGroupErrorCodes.BackupRaceClosed);

            // Saying it again is not an error and changes nothing.
            await AcceptAsBackupAsync(a, requestA.Id);
        }

        var accepted = await MembersAsync(groupId);
        Assert.NotNull(accepted.Single(m => m.Id == requestA.Id).BackupAcceptedAt);
        Assert.Equal(ServiceRequestStatus.Richiesto, accepted.Single(m => m.Id == requestA.Id).Status);
        var raced = accepted.Single(m => m.Id == requestB.Id);
        Assert.Null(raced.BackupAcceptedAt);
        Assert.Equal(ServiceRequestStatus.Annullato, raced.Status);
        Assert.Equal(ServiceRequestActorParty.System, raced.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, raced.CancellationReason);

        // Within the primary's hour the job leaves the group as it is.
        await RunJobAsync();
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, (await LoadGroupAsync(groupId)).Phase);

        // The hour passes and the chosen supplier has not answered.
        await ChangeGroupAsync(groupId, g => g.PrimaryDueAt = DateTime.UtcNow.AddMinutes(-2));
        var run = await RunJobAsync();
        Assert.False(run.Skipped);
        Assert.True(run.Closed >= 1, $"Closed {run.Closed}, failed {run.Failed}, conflicts {run.Conflicts}");

        var after = await MembersAsync(groupId);
        var promoted = after.Single(m => m.GroupRole == ServiceRequestGroupRole.PromotedBackup);
        Assert.Equal(requestA.Id, promoted.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, promoted.Status);
        Assert.NotNull(promoted.TakenAt);
        Assert.Equal(world.BackupA.UserId, promoted.TakenByUserId);

        var replaced = after.Single(m => m.GroupRole == ServiceRequestGroupRole.ReplacedPrimary);
        Assert.Equal(id, replaced.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, replaced.Status);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, replaced.CancellationReason);

        // Beta stays as the race left it: closed when Alfa accepted, not by the take.
        var other = after.Single(m => m.Id == requestB.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, other.Status);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, other.CancellationReason);

        // The host follows the backup that took the job, and the group tells how it came to it.
        using (var host = factory.Host(world.Base))
        {
            var list = await host.GetFromJsonAsync<JsonElement>($"/api/service-requests?propertyId={world.Base.PropertyId}", JsonOptions);
            var visible = Assert.Single(list.GetProperty("items").EnumerateArray());
            Assert.Equal(requestA.Id, visible.GetProperty("id").GetGuid());
            Assert.Equal("PresoInCarico", visible.GetProperty("status").GetString());
            var shown = visible.GetProperty("group");
            Assert.Equal(nameof(ServiceRequestGroupPhase.Closed), shown.GetProperty("phase").GetString());
            var first = shown.GetProperty("members").EnumerateArray().First();
            Assert.Equal(nameof(ServiceRequestGroupRole.PromotedBackup), first.GetProperty("role").GetString());
            Assert.True(first.GetProperty("tookTheJob").GetBoolean());
        }

        // Idempotent: a group that ended is not touched again.
        var closedAt = (await LoadGroupAsync(groupId)).ClosedAt;
        await RunJobAsync();
        Assert.Equal(closedAt, (await LoadGroupAsync(groupId)).ClosedAt);
    }

    [PostgresFact]
    public async Task TwoBackupsAcceptingAtTheSameMoment_OneClosesTheRaceAndTheOtherFindsItClosed()
    {
        var world = await SeedAsync();
        var id = await factory.CreateRequestAsync(world.Base);
        var groupId = await GroupIdOfAsync(id);
        var members = await MembersAsync(groupId);
        var requestA = members.Single(m => m.SupplierOrgId == world.BackupA.OrgId);
        var requestB = members.Single(m => m.SupplierOrgId == world.BackupB.OrgId);

        using var a = factory.CreateAuthenticatedClient(world.BackupA.UserId, "Supplier");
        using var b = factory.CreateAuthenticatedClient(world.BackupB.UserId, "Supplier");
        var responses = await Task.WhenAll(
            a.PostAsync($"/api/service-requests/{requestA.Id}/accept-as-backup", content: null),
            b.PostAsync($"/api/service-requests/{requestB.Id}/accept-as-backup", content: null));

        // The group's lock serializes the acceptances: one has it, and the other finds its request closed by the race (409, its own code).
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblemAsync(
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, ServiceRequestGroupErrorCodes.BackupRaceClosed);

        var after = await MembersAsync(groupId);
        var accepted = Assert.Single(after, m => m.GroupRole == ServiceRequestGroupRole.Backup && m.BackupAcceptedAt != null);
        Assert.Equal(ServiceRequestStatus.Richiesto, accepted.Status);
        var closed = Assert.Single(after, m => m.GroupRole == ServiceRequestGroupRole.Backup && m.BackupAcceptedAt == null);
        Assert.Equal(ServiceRequestStatus.Annullato, closed.Status);
        Assert.Equal(ServiceRequestCancellationReasons.BackupRaceClosed, closed.CancellationReason);
        Assert.Equal(ServiceRequestGroupPhase.PrimaryWindow, (await LoadGroupAsync(groupId)).Phase);
    }

    [PostgresFact]
    public async Task SecondWindow_TwoBackupsTakingAtTheSameMoment_OneWinsAndTheOtherIsRefused()
    {
        var world = await SeedAsync();
        var id = await factory.CreateRequestAsync(world.Base);
        var groupId = await GroupIdOfAsync(id);
        var members = await MembersAsync(groupId);
        var requestA = members.Single(m => m.SupplierOrgId == world.BackupA.OrgId);
        var requestB = members.Single(m => m.SupplierOrgId == world.BackupB.OrgId);

        // Nobody accepted in the hour: the job opens the second window, where the first to take the job has it.
        await ChangeGroupAsync(groupId, g => g.PrimaryDueAt = DateTime.UtcNow.AddMinutes(-2));
        var run = await RunJobAsync();
        Assert.True(run.BackupWindowsOpened >= 1, $"Windows opened {run.BackupWindowsOpened}, failed {run.Failed}, conflicts {run.Conflicts}");
        var open = await LoadGroupAsync(groupId);
        Assert.Equal(ServiceRequestGroupPhase.BackupWindow, open.Phase);
        Assert.True(open.BackupDueAt > DateTime.UtcNow, "The second window runs until a moment in the future.");

        using var a = factory.CreateAuthenticatedClient(world.BackupA.UserId, "Supplier");
        using var b = factory.CreateAuthenticatedClient(world.BackupB.UserId, "Supplier");
        var responses = await Task.WhenAll(
            a.PostAsJsonAsync($"/api/service-requests/{requestA.Id}/take", new { }),
            b.PostAsJsonAsync($"/api/service-requests/{requestB.Id}/take", new { }));

        // The group's lock serializes the takes; the loser finds the job gone (409 state changed, or 422 when its own request was closed first).
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        var won = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity);
        var wonId = (await won.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetGuid();

        var after = await MembersAsync(groupId);
        Assert.Equal(1, after.Count(m => m.Status == ServiceRequestStatus.PresoInCarico));
        var promoted = after.Single(m => m.GroupRole == ServiceRequestGroupRole.PromotedBackup);
        Assert.Equal(wonId, promoted.Id);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, promoted.Status);
        Assert.Equal(ServiceRequestStatus.Annullato, after.Single(m => m.GroupRole == ServiceRequestGroupRole.ReplacedPrimary).Status);
        var lost = after.Single(m => m.GroupRole == ServiceRequestGroupRole.Backup);
        Assert.NotEqual(wonId, lost.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, lost.Status);
        Assert.Equal(ServiceRequestCancellationReasons.TakenByBackup, lost.CancellationReason);
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await LoadGroupAsync(groupId)).Phase);
    }

    [PostgresFact]
    public async Task HostCancels_TheBackupsAreClosedAndTheGroupEnds()
    {
        var world = await SeedAsync();
        var id = await factory.CreateRequestAsync(world.Base);
        var groupId = await GroupIdOfAsync(id);

        using (var host = factory.Host(world.Base))
        {
            var cancel = await host.PostAsJsonAsync($"/api/service-requests/{id}/cancel", new { reason = "Non serve più" });
            Assert.True(cancel.StatusCode == HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
        }

        var after = await MembersAsync(groupId);
        Assert.All(after, m => Assert.Equal(ServiceRequestStatus.Annullato, m.Status));
        Assert.All(
            after.Where(m => m.GroupRole == ServiceRequestGroupRole.Backup),
            m => Assert.Equal(ServiceRequestCancellationReasons.RequestCancelledByHost, m.CancellationReason));
        Assert.Equal(ServiceRequestGroupPhase.Closed, (await LoadGroupAsync(groupId)).Phase);
    }

    [PostgresFact]
    public async Task Run_WhileAnotherRunHoldsTheSessionLock_IsSkipped()
    {
        await using (var holderScope = factory.Services.CreateAsyncScope())
        {
            var holderDb = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var held = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                holderDb, PostgresAdvisoryLocks.Scope.ServiceRequestGroupRun, ServiceRequestGroupRunService.RunLockKey, CancellationToken.None);
            Assert.NotNull(held);

            var skipped = await RunJobAsync();

            Assert.True(skipped.Skipped);
            Assert.Equal(0, skipped.Examined);
        }

        // The lock is released with the handle: the next run takes it.
        Assert.False((await RunJobAsync()).Skipped);
    }

    // ─── helpers ───

    private sealed record BackupWorld(ServiceRequestWorld Base, (string UserId, Guid OrgId) BackupA, (string UserId, Guid OrgId) BackupB);

    /// <summary>
    /// A host with a property in a comune of its own, the supplier it chooses, and two more suppliers that cover that comune and the
    /// cleaning category: the backups the rule picks. The comune is written as a name that is not in the official list, so the suppliers are
    /// matched by it and by nothing else, and no supplier of another test is ever one of these backups.
    /// </summary>
    private async Task<BackupWorld> SeedAsync()
    {
        var comune = $"Borgo{Guid.NewGuid():N}";
        var baseWorld = await ServiceRequestWorlds.SeedAsync(factory, propertyCity: comune);
        var a = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var b = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgIds = new[] { baseWorld.SupplierOrgId, a.OrgId, b.OrgId };
        foreach (var profile in await db.SupplierProfiles.IgnoreQueryFilters().Where(p => orgIds.Contains(p.OrgId)).ToListAsync())
            profile.ComuniJson = JsonSerializer.Serialize(new[] { comune });
        await db.SaveChangesAsync();

        return new BackupWorld(baseWorld, a, b);
    }

    private async Task<Guid> GroupIdOfAsync(Guid requestId) =>
        (await factory.LoadRequestAsync(requestId)).GroupId
        ?? throw new InvalidOperationException("The request got no group: the backup suppliers were not chosen (see the log of the host).");

    private async Task<List<ServiceRequest>> MembersAsync(Guid groupId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.GroupId == groupId).ToListAsync();
    }

    private async Task<ServiceRequestGroup> LoadGroupAsync(Guid groupId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequestGroups.AsNoTracking().SingleAsync(g => g.Id == groupId);
    }

    /// <summary>Writes a change straight to a group (a deadline in the past: the state the API reaches in an hour).</summary>
    private async Task ChangeGroupAsync(Guid groupId, Action<ServiceRequestGroup> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var group = await db.ServiceRequestGroups.SingleAsync(g => g.Id == groupId);
        change(group);
        await db.SaveChangesAsync();
    }

    private async Task<ServiceRequestGroupRun> RunJobAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IServiceRequestGroupRunService>().RunAsync();
    }

    private static async Task AcceptAsBackupAsync(HttpClient supplier, Guid requestId)
    {
        var response = await supplier.PostAsync($"/api/service-requests/{requestId}/accept-as-backup", content: null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        Assert.Equal(code, JsonSerializer.Deserialize<JsonElement>(text, JsonOptions).GetProperty("code").GetString());
    }
}
