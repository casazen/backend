using System.Net;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Casazen.Tests.Integration.LeaseDraftHttp;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-12 on real PostgreSQL, what memory cannot show: two tabs saving the same version at the same moment (the row lock and the
/// version let exactly one through), a confirmation of the review racing a save (a confirmed review is never of values that
/// changed), the personal data of a draft stored encrypted in the table, the foreign keys of the deletion, the retention job over
/// every org with its session lock. The same host as <see cref="LeaseDraftApiIntegrationTests"/>, "today"
/// 2026-10-09. Few parallel requests on purpose (each holds a connection while it waits for the row lock).
/// </summary>
public class LeaseDraftPostgresTests(LeaseDraftFactory factory) : IClassFixture<LeaseDraftFactory>, IAsyncLifetime
{
    private LongRentWorld _world = null!;

    private Guid _draftProperty;

    public async Task InitializeAsync()
    {
        _world = await LongRentWorld.SeedAsync(factory);
        _draftProperty = await factory.AddReadyPropertyAsync(_world.OrgId, _world.OwnerId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Owner() => _world.OwnerClient(factory);

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<LeaseContract> LoadAsync(Guid id) =>
        WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().AsNoTracking().Include(l => l.Parties).Include(l => l.Events).SingleAsync(l => l.Id == id));

    [PostgresFact]
    public async Task Save_SeveralTabsOnTheSameVersionAtTheSameMoment_OneWins_TheOthersGet409_NoneGets500()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _draftProperty);
        var rents = new[] { 810, 820, 830, 840 };

        var responses = await InParallelAsync(
            rents.Select(rent => (Func<Task<HttpResponseMessage>>)(() =>
                client.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 3, $$"""{"monthlyRent":{{rent}}}"""))))
            .ToArray());

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var lost = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(rents.Length - 1, lost.Count);
        foreach (var response in lost)
            Assert.Equal("lease_draft_conflict", (await JsonAsync(response)).GetProperty("code").GetString());

        var winner = rents[responses.ToList().FindIndex(r => r.StatusCode == HttpStatusCode.OK)];
        var draft = await JsonAsync(await client.GetAsync($"/api/leases/{id}/draft"));
        Assert.Equal(version + 1, draft.GetProperty("version").GetInt32());
        Assert.Equal(winner, draft.GetProperty("values").GetProperty("monthlyRent").GetDecimal());
        Assert.Equal(version + 1, (await LoadAsync(id)).DraftVersion);
    }

    [PostgresFact]
    public async Task Save_WhileAnotherTransactionHoldsTheRow_WaitsAFewSeconds_ThenAnswers409_NeverAGatewayTimeout()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _draftProperty);

        await using var scope = factory.Services.CreateAsyncScope();
        var holder = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {id} FOR UPDATE""").ToListAsync();

        var started = DateTime.UtcNow;
        var response = await client.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 3, """{"monthlyRent":900}"""));
        var waited = DateTime.UtcNow - started;

        await ExpectProblemAsync(response, HttpStatusCode.Conflict, "lease_draft_conflict");
        Assert.InRange(waited, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(25));
        await transaction.RollbackAsync();

        Assert.Equal(version, (await LoadAsync(id)).DraftVersion);
        await PatchAsync(client, id, version, 3, """{"monthlyRent":900}""");
    }

    [PostgresFact]
    public async Task SavesOfDifferentDrafts_AtTheSameMoment_AreAllApplied()
    {
        using var client = Owner();
        var drafts = new List<(Guid Id, int Version)>();
        for (var i = 0; i < 4; i++)
            drafts.Add(await CreateAsync(client, _draftProperty));

        var responses = await InParallelAsync(
            drafts.Select((draft, i) => (Func<Task<HttpResponseMessage>>)(() =>
                client.PatchAsync($"/api/leases/{draft.Id}/draft", SaveBody(draft.Version, 3, $$"""{"monthlyRent":{{900 + i}}}"""))))
            .ToArray());

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = await JsonAsync(await client.GetAsync($"/api/leases/{drafts[i].Id}/draft"));
            Assert.Equal(900 + i, draft.GetProperty("values").GetProperty("monthlyRent").GetDecimal());
        }
    }

    [PostgresFact]
    public async Task Confirm_RacingASave_NeverLeavesAConfirmedReviewOfValuesThatChanged()
    {
        using var client = Owner();

        for (var round = 0; round < 4; round++)
        {
            var (id, version) = await CompleteAsync(client, _draftProperty);

            var responses = await InParallelAsync(
            [
                () => client.PostAsync($"/api/leases/{id}/review/confirm", Body("{}")),
                () => client.PatchAsync($"/api/leases/{id}/draft", SaveBody(version, 3, """{"monthlyRent":950}""")),
            ]);

            Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
            Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
            Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            var lease = await LoadAsync(id);
            Assert.NotNull(lease.ReviewConfirmedAt);
            Assert.Equal(LeaseDraftFingerprint.Compute(LeaseDraftProjection.ReadValues(lease)), lease.ReviewFingerprint);
            Assert.Equal(responses[1].StatusCode == HttpStatusCode.OK ? 950m : 900m, lease.MonthlyRent);
        }
    }

    [PostgresFact]
    public async Task TheDraftAndTheResidence_AreStoredEncrypted_TheTableNeverHoldsTheValuesInClear()
    {
        using var client = Owner();
        var (id, version) = await CreateAsync(client, _draftProperty);
        await PatchAsync(client, id, version, 2, """
            {"landlords":[{"firstName":"Zefiro","lastName":"Quarantaquattro","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Via Segretissima 7, Pozzuoli","contactEmail":"zefiro@riservato.example"}],
             "tenants":[{"firstName":"Arianna","lastName":"Boldrini","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Corso Nascosto 12, Taranto"},
                        {"firstName":"Incompleto"}]}
            """);

        var (draft, residences) = await WithDbAsync(async db =>
        {
            Assert.True(EncryptedColumns.IsConfigured(db), "the host encrypts: Data Protection is configured");
            var raw = await db.Database.SqlQuery<string>($"""SELECT "DraftData" AS "Value" FROM "LeaseContracts" WHERE "Id" = {id}""").SingleAsync();
            var rows = await db.Database
                .SqlQuery<string>($"""SELECT "Residence" AS "Value" FROM "Parties" WHERE "LeaseContractId" = {id} AND "Residence" IS NOT NULL""")
                .ToListAsync();
            return (raw, rows);
        });

        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, draft, StringComparison.Ordinal);
        foreach (var secret in new[] { "Zefiro", "Quarantaquattro", "Arianna", "Boldrini", "RSSMRA80A01H501U", "Segretissima", "Pozzuoli", "Nascosto", "Taranto", "riservato", "Incompleto" })
            Assert.DoesNotContain(secret, draft, StringComparison.OrdinalIgnoreCase);

        Assert.NotEmpty(residences);
        Assert.All(residences, residence =>
        {
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, residence, StringComparison.Ordinal);
            Assert.DoesNotContain("Segretissima", residence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Nascosto", residence, StringComparison.OrdinalIgnoreCase);
        });

        var read = await JsonAsync(await client.GetAsync($"/api/leases/{id}/draft"));
        Assert.Equal("Via Segretissima 7, Pozzuoli", read.GetProperty("values").GetProperty("landlords")[0].GetProperty("residence").GetString());
    }

    [PostgresFact]
    public async Task ACompleteDraft_KeepsNoJson_TheRowsAreTheOnlyCopy_AndTheResidenceIsStillEncrypted()
    {
        using var client = Owner();
        var (id, _) = await CompleteAsync(client, _draftProperty);

        var (draft, residences) = await WithDbAsync(async db => (
            await db.Database.SqlQuery<string>($"""SELECT "DraftData" AS "Value" FROM "LeaseContracts" WHERE "Id" = {id} AND "DraftData" IS NOT NULL""").ToListAsync(),
            await db.Database.SqlQuery<string>($"""SELECT "Residence" AS "Value" FROM "Parties" WHERE "LeaseContractId" = {id}""").ToListAsync()));

        Assert.Empty(draft);
        Assert.Equal(2, residences.Count);
        Assert.All(residences, residence => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, residence, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Delete_TakesWithItThePartiesTheSignersTheRegistrationTheAuthorizationsAndTheEvents()
    {
        using var client = Owner();
        var (id, _) = await CompleteAsync(client, _draftProperty);
        var partyIds = (await JsonAsync(await client.GetAsync($"/api/leases/{id}/draft"))).GetProperty("values")
            .GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        await WithDbAsync(async db =>
        {
            db.LeaseSigners.Add(new LeaseSigner { OrgId = _world.OrgId, LeaseContractId = id, PartyId = partyIds[0], Method = LeaseSignatureMethod.Offline });
            db.LeaseRegistrations.Add(new LeaseRegistration { LeaseContractId = id, Status = RegistrationStatus.Pending, Channel = RegistrationChannel.Manual });
            db.LeaseRegistrationAuthorizations.Add(new LeaseRegistrationAuthorization
            {
                OrgId = _world.OrgId,
                LeaseContractId = id,
                AuthorizerUserId = _world.OwnerId,
                TosVersion = "2026-10",
                AttestationAccepted = true,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await client.DeleteAsync($"/api/leases/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var left = await WithDbAsync(async db => new[]
        {
            await db.LeaseContracts.IgnoreQueryFilters().CountAsync(l => l.Id == id),
            await db.Parties.IgnoreQueryFilters().CountAsync(p => p.LeaseContractId == id),
            await db.LeaseEvents.IgnoreQueryFilters().CountAsync(e => e.LeaseContractId == id),
            await db.LeaseSigners.IgnoreQueryFilters().CountAsync(s => s.LeaseContractId == id),
            await db.LeaseRegistrations.IgnoreQueryFilters().CountAsync(r => r.LeaseContractId == id),
            await db.LeaseRegistrationAuthorizations.IgnoreQueryFilters().CountAsync(a => a.LeaseContractId == id),
        });
        Assert.Equal([0, 0, 0, 0, 0, 0], left);
        Assert.True(await WithDbAsync(db => db.Properties.IgnoreQueryFilters().AnyAsync(p => p.Id == _draftProperty)));
        Assert.True(await WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().AnyAsync(l => l.Id == _world.Active)));
    }

    [PostgresFact]
    public async Task Delete_ALeaseWithARentPlan_IsRefused_AndNothingIsDeleted()
    {
        using var client = Owner();
        var (id, _) = await CompleteAsync(client, _draftProperty);
        await WithDbAsync(async db =>
        {
            db.RentSchedules.Add(new RentSchedule
            {
                OrgId = _world.OrgId,
                LeaseContractId = id,
                Cadence = RentCadence.Monthly,
                BillingDayOfMonth = 5,
                Amount = 900m,
                NextRunDate = new DateOnly(2026, 11, 5),
                IsActive = true,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await ExpectProblemAsync(await client.DeleteAsync($"/api/leases/{id}"), HttpStatusCode.Conflict, "lease_not_deletable");

        var lease = await LoadAsync(id);
        Assert.Equal(2, lease.Parties.Count);
    }

    private Task IdleAsync(Guid id, int idleDays) => WithDbAsync(async db =>
    {
        var at = LeaseDraftFactory.Now.UtcDateTime.AddDays(-idleDays);
        await db.Database.ExecuteSqlAsync($"""UPDATE "LeaseContracts" SET "UpdatedAt" = {at} WHERE "Id" = {id}""");
        return 0;
    });

    private async Task<LeaseDraftRetentionResult> RunRetentionAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILeaseDraftRetentionService>().RunAsync();
    }

    private Task<bool> ExistsAsync(Guid id) => WithDbAsync(db => db.LeaseContracts.IgnoreQueryFilters().AnyAsync(l => l.Id == id));

    [PostgresFact]
    public async Task Retention_DeletesTheAbandonedDraftsOfEveryOrg_NeverASignedCopyNorALeaseThatIsNotADraft_AndASecondRunFindsNothing()
    {
        var other = await LongRentWorld.SeedAsync(factory);
        await factory.MakeReadyAsync(other.P1);
        using var mine = Owner();
        using var theirs = other.OwnerClient(factory);
        var (abandoned, _) = await CompleteAsync(mine, _draftProperty);
        var (abandonedElsewhere, _) = await CompleteAsync(theirs, other.P1);
        var (withSignedCopy, _) = await CompleteAsync(mine, _draftProperty);
        var (recent, _) = await CompleteAsync(mine, _draftProperty);
        var (incomplete, _) = await CreateAsync(mine, _draftProperty);
        foreach (var id in new[] { abandoned, abandonedElsewhere, withSignedCopy, incomplete })
            await IdleAsync(id, 181);
        await IdleAsync(recent, 179);
        await WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlAsync($"""UPDATE "LeaseContracts" SET "SignedPdfStoragePath" = 'leases/signed.pdf' WHERE "Id" = {withSignedCopy}""");
            return 0;
        });
        await IdleAsync(_world.Active, 900);

        var run = await RunRetentionAsync();

        Assert.False(run.Skipped);
        Assert.True(run.Deleted >= 3, $"deleted {run.Deleted}");
        Assert.False(await ExistsAsync(abandoned));
        Assert.False(await ExistsAsync(abandonedElsewhere));
        Assert.False(await ExistsAsync(incomplete));
        Assert.True(await ExistsAsync(withSignedCopy));
        Assert.True(await ExistsAsync(recent));
        Assert.True(await ExistsAsync(_world.Active));
        Assert.False(await WithDbAsync(db => db.Parties.IgnoreQueryFilters().AnyAsync(p => p.LeaseContractId == abandoned)));

        var again = await RunRetentionAsync();

        Assert.False(again.Skipped);
        Assert.Equal(0, again.Deleted);
    }

    [PostgresFact]
    public async Task Retention_WhileAnotherRunHoldsTheLock_IsSkipped_AndTheNextRunDoesTheWork()
    {
        using var client = Owner();
        var (abandoned, _) = await CompleteAsync(client, _draftProperty);
        await IdleAsync(abandoned, 200);

        LeaseDraftRetentionResult skipped;
        await using (var holderScope = factory.Services.CreateAsyncScope())
        {
            var holderDb = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var held = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
                holderDb, PostgresAdvisoryLocks.Scope.LeaseDraftRetentionRun, LeaseDraftRetentionService.RunLockKey, CancellationToken.None);
            Assert.NotNull(held);

            skipped = await RunRetentionAsync();
        }

        Assert.True(skipped.Skipped);
        Assert.Equal(0, skipped.Deleted);
        Assert.True(await ExistsAsync(abandoned));

        var run = await RunRetentionAsync();
        Assert.False(run.Skipped);
        Assert.False(await ExistsAsync(abandoned));
    }
}
