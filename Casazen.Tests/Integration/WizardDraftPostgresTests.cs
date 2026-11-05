using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// ADR-024 (E01/F02/US01) on a real PostgreSQL database, where it counts: the personal half of a wizard draft is stored encrypted — the
/// table never holds a personal field in clear, nor the name of one in the plain column — and read back through the service; and two
/// saves at the same moment can never both win (<c>xmin</c> on the row, the unique indexes for the first save). The rest of the service is
/// in <c>WizardDraftServiceTests</c> (in-memory); the retention is in <c>WizardDraftRetentionPostgresTests</c>.
/// </summary>
public class WizardDraftPostgresTests
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private const string UserId = "auth0|alice";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 9, 30, 15, TimeSpan.Zero));

    private async Task<WizardDraftResult> SaveAsync(
        PostgresTestDatabase database,
        WizardType type,
        Guid? entityId,
        int clientVersion,
        string json,
        DraftSaveRendezvous? rendezvous = null)
    {
        var options = rendezvous is null
            ? database.CreateOptions()
            : new DbContextOptionsBuilder<AppDbContext>(database.CreateOptions()).AddInterceptors(rendezvous).Options;
        await using var db = new AppDbContext(options, tenantContext: null, Provider);
        using var data = JsonDocument.Parse(json);
        return await new WizardDraftService(db, _clock)
            .SaveDraftAsync(Org, UserId, new SaveDraftRequest(type, entityId, clientVersion, data), CancellationToken.None);
    }

    /// <summary>
    /// Holds every save of a draft until two have arrived, so that both read the same state before either writes: the race of two
    /// devices made deterministic. One instance is shared by the two contexts of a race (a new one per context would wait alone).
    /// </summary>
    internal sealed class DraftSaveRendezvous : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var savesADraft = eventData.Context?.ChangeTracker.Entries<WizardDraft>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified) == true;
            if (!savesADraft)
                return result;

            if (Interlocked.Increment(ref _arrived) >= 2)
                _bothArrived.TrySetResult();

            await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    [PostgresFact]
    public async Task WizardDraft_PersonalData_EncryptedAtRest_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("wzd");
        const string typed = """
            {
              "step": "ospiti",
              "propertyName": "Villa del Lago (demo)",
              "guestName": "Mario Rossi",
              "guestEmail": "mario.rossi@example.com",
              "address": { "street": "Via Segretissima 7", "city": "Como" },
              "guests": [ { "room": 1, "guestName": "Giulia Verdi", "documentNumber": "CA12345AB" } ]
            }
            """;
        string[] secrets = ["Mario Rossi", "mario.rossi@example.com", "Via Segretissima 7", "Giulia Verdi", "CA12345AB"];

        var saved = await SaveAsync(database, WizardType.BookingCreate, null, 0, typed);
        Assert.Equal(DraftStatus.Saved, saved.Status);

        // The table itself, whatever EF would have made of it: a context without Data Protection reads the stored text.
        await using var stored = database.CreateContext();
        var plain = await stored.Database
            .SqlQueryRaw<string>("SELECT \"DraftJson\"::text AS \"Value\" FROM \"WizardDrafts\"")
            .SingleAsync();
        var encrypted = await stored.Database
            .SqlQueryRaw<string>("SELECT \"EncryptedFieldsJson\" AS \"Value\" FROM \"WizardDrafts\"")
            .SingleAsync();

        // Neither a value nor the name of a personal field is in the plain column; what is not personal is.
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, plain, StringComparison.OrdinalIgnoreCase);
        foreach (var name in WizardDraftPiiFields.PII_FIELD_NAMES)
            Assert.DoesNotContain(name, plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Villa del Lago (demo)", plain);

        // The personal column is a Data Protection payload, with none of the values in it.
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, encrypted, StringComparison.Ordinal);
        foreach (var secret in secrets)
            Assert.DoesNotContain(secret, encrypted, StringComparison.OrdinalIgnoreCase);

        // Read through the service (with Data Protection) the two halves come back together, as typed.
        await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
        var draft = await new WizardDraftService(db, _clock).GetDraftAsync(Org, UserId, WizardType.BookingCreate, null, CancellationToken.None);
        Assert.NotNull(draft);
        var root = draft.Data.RootElement;
        Assert.Equal("Mario Rossi", root.GetProperty("guestName").GetString());
        Assert.Equal("mario.rossi@example.com", root.GetProperty("guestEmail").GetString());
        Assert.Equal("Via Segretissima 7", root.GetProperty("address").GetProperty("street").GetString());
        Assert.Equal("CA12345AB", root.GetProperty("guests")[0].GetProperty("documentNumber").GetString());
        Assert.Equal("Villa del Lago (demo)", root.GetProperty("propertyName").GetString());
    }

    [PostgresFact]
    public async Task SaveDraft_TwoSavesAtTheSameTime_OnlyOneWins_TheOtherGetsTheConflictWithTheWinnersDraft()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("wzd");

        // The draft of a new entity (no EntityId) and the draft of an existing one have an unique index each.
        foreach (Guid? entityId in new Guid?[] { null, Guid.NewGuid() })
        {
            // The first save of a draft that does not exist yet: both read nothing, both insert, the unique index refuses the second.
            var firstRace = new DraftSaveRendezvous();
            var firsts = await Task.WhenAll(
                SaveAsync(database, WizardType.BookingEdit, entityId, 0, """{"who":"a"}""", firstRace),
                SaveAsync(database, WizardType.BookingEdit, entityId, 0, """{"who":"b"}""", firstRace));
            Assert.Equal(new[] { DraftStatus.Saved, DraftStatus.Conflict }, firsts.Select(r => r.Status).Order());
            var firstWinner = firsts.Single(r => r.Status == DraftStatus.Saved);
            var firstLoser = firsts.Single(r => r.Status == DraftStatus.Conflict);
            Assert.Equal(1, firstWinner.ServerVersion);
            Assert.Equal(1, firstLoser.ServerVersion);
            Assert.Equal(Who(firstWinner), Who(firstLoser));

            // Both hold version 1 and save on top of it: one update wins, the other finds the row changed since it read it (xmin).
            var secondRace = new DraftSaveRendezvous();
            var seconds = await Task.WhenAll(
                SaveAsync(database, WizardType.BookingEdit, entityId, 1, """{"who":"c"}""", secondRace),
                SaveAsync(database, WizardType.BookingEdit, entityId, 1, """{"who":"d"}""", secondRace));
            Assert.Equal(new[] { DraftStatus.Saved, DraftStatus.Conflict }, seconds.Select(r => r.Status).Order());
            var secondWinner = seconds.Single(r => r.Status == DraftStatus.Saved);
            var secondLoser = seconds.Single(r => r.Status == DraftStatus.Conflict);
            Assert.Equal(2, secondWinner.ServerVersion);
            Assert.Equal(2, secondLoser.ServerVersion);
            Assert.Equal(Who(secondWinner), Who(secondLoser));

            // What is stored is the winner's draft, once, at version 2.
            await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
            var draft = await new WizardDraftService(db, _clock).GetDraftAsync(Org, UserId, WizardType.BookingEdit, entityId, CancellationToken.None);
            Assert.NotNull(draft);
            Assert.Equal(2, draft.Version);
            Assert.Equal(Who(secondWinner), draft.Data.RootElement.GetProperty("who").GetString());
            Assert.Equal(1, await db.WizardDrafts.CountAsync(d => d.WizardType == WizardType.BookingEdit && d.EntityId == entityId));
        }
    }

    private static string? Who(WizardDraftResult result) => result.ServerDraft!.Data.RootElement.GetProperty("who").GetString();
}
