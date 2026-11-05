using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// ADR-024-R5 on a real PostgreSQL database: the nightly retention (<see cref="IWizardDraftRetentionService"/>, run by
/// <c>GdprDataRetentionJob</c>) anonymizes the drafts past their 30 days and never deletes a row, leaves alone the draft a person saved
/// again, finds nothing the second time, does not trip on the unique indexes when several rows end up with the same empty user (they are
/// left out of both), and never reads the personal half of a draft: one that can no longer be decrypted is anonymized like the others and
/// does not stop the drafts after it.
/// </summary>
public class WizardDraftRetentionPostgresTests
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    // Another key ring: what it protected cannot be read with Provider, as a draft whose Data Protection keys were lost or rotated.
    private static readonly IDataProtectionProvider LostKeys = new EphemeralDataProtectionProvider();

    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid BookingId = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private const string Alice = "auth0|alice";
    private const string Bob = "auth0|bob";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 9, 30, 15, TimeSpan.Zero));

    private async Task SaveAsync(
        PostgresTestDatabase database,
        string userId,
        WizardType type,
        Guid? entityId,
        int clientVersion,
        string guest,
        IDataProtectionProvider? keys = null)
    {
        await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, keys ?? Provider);
        using var data = JsonDocument.Parse($$"""{"step":"ospiti","guestName":"{{guest}}"}""");
        var result = await new WizardDraftService(db, _clock)
            .SaveDraftAsync(Org, userId, new SaveDraftRequest(type, entityId, clientVersion, data), CancellationToken.None);
        Assert.Equal(DraftStatus.Saved, result.Status);
    }

    private async Task<WizardDraftRetentionRun> RunRetentionAsync(PostgresTestDatabase database)
    {
        await using var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider);
        return await new WizardDraftRetentionService(db, NullLogger<WizardDraftRetentionService>.Instance, _clock).ApplyRetentionAsync();
    }

    [PostgresFact]
    public async Task WizardDraft_RetentionJob_AnonymizesPii_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("wzdr");

        // Day 0: two people on the same booking (same org, wizard and entity), one draft of a new entity, and a listing draft.
        await SaveAsync(database, Alice, WizardType.CheckIn, BookingId, 0, "Giulia Verdi");
        await SaveAsync(database, Bob, WizardType.CheckIn, BookingId, 0, "Luca Neri");
        await SaveAsync(database, Alice, WizardType.BookingCreate, null, 0, "Anna Bianchi");
        await SaveAsync(database, Alice, WizardType.ListingCreate, null, 0, "Paolo Gialli");

        // Day 29: Alice goes on with the listing, so its 30 days start again. Day 31: the other three are over, this one is not.
        _clock.Advance(TimeSpan.FromDays(29));
        await SaveAsync(database, Alice, WizardType.ListingCreate, null, 1, "Paolo Gialli");
        _clock.Advance(TimeSpan.FromDays(2));

        var run = await RunRetentionAsync(database);

        Assert.Equal(3, run.Drafts);

        // The table, as stored (a context without Data Protection): no row was deleted.
        await using var stored = database.CreateContext();
        var rows = await stored.WizardDrafts.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);

        // The three that were over have no person and no data any more, and keep what the audit trail needs.
        var anonymized = rows.Where(r => r.UserId == string.Empty).ToList();
        Assert.Equal(3, anonymized.Count);
        Assert.All(anonymized, r =>
        {
            Assert.Equal("{}", r.DraftJson);
            Assert.Null(r.EncryptedFieldsJson);
            Assert.Equal(Org, r.OrgId);
            Assert.Equal(1, r.Version);
        });
        // Two of them are the same org, wizard and entity with the same empty user: the unique indexes leave anonymized rows out.
        Assert.Equal(2, anonymized.Count(r => r.WizardType == WizardType.CheckIn && r.EntityId == BookingId));

        // The one saved again is untouched: its person, its data (the personal half still encrypted).
        var live = Assert.Single(rows, r => r.UserId != string.Empty);
        Assert.Equal((Alice, WizardType.ListingCreate, 2), (live.UserId, live.WizardType, live.Version));
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, live.EncryptedFieldsJson, StringComparison.Ordinal);
        Assert.Contains("ospiti", live.DraftJson);

        // Through the service nobody gets the anonymized ones back, and the live one is Alice's.
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            var service = new WizardDraftService(db, _clock);
            Assert.Null(await service.GetDraftAsync(Org, Alice, WizardType.CheckIn, BookingId, CancellationToken.None));
            Assert.Null(await service.GetDraftAsync(Org, Bob, WizardType.CheckIn, BookingId, CancellationToken.None));
            var kept = await service.GetDraftAsync(Org, Alice, WizardType.ListingCreate, null, CancellationToken.None);
            Assert.Equal("Paolo Gialli", kept!.Data.RootElement.GetProperty("guestName").GetString());
        }

        // Idempotent: a second night finds nothing to do.
        Assert.Equal(0, (await RunRetentionAsync(database)).Drafts);
    }

    [PostgresFact]
    public async Task WizardDraft_RetentionJob_APersonalHalfThatCannotBeDecrypted_IsAnonymizedAllTheSame_AndTheOtherDraftsToo_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("wzdr");

        // Alice's first draft was protected with keys that are lost (LostKeys); the other two can be read with the keys of the job.
        await SaveAsync(database, Alice, WizardType.CheckIn, BookingId, 0, "Giulia Verdi", LostKeys);
        await SaveAsync(database, Bob, WizardType.CheckIn, BookingId, 0, "Luca Neri");
        await SaveAsync(database, Alice, WizardType.BookingCreate, null, 0, "Anna Bianchi");
        _clock.Advance(TimeSpan.FromDays(31));

        // The premise: with the keys of the job the draft of Alice cannot be loaded (a retention that read the drafts would fail on it).
        await using (var reader = new AppDbContext(database.CreateOptions(), tenantContext: null, Provider))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => reader.WizardDrafts.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).ToListAsync());
        }

        var run = await RunRetentionAsync(database);

        // All three are anonymized, the unreadable one included, and none is deleted.
        Assert.Equal(3, run.Drafts);
        await using var stored = database.CreateContext();
        var rows = await stored.WizardDrafts.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Empty(r.UserId);
            Assert.Equal("{}", r.DraftJson);
            Assert.Null(r.EncryptedFieldsJson);
        });

        // Nothing is left to read, so the next night finds nothing and does not fail.
        Assert.Equal(0, (await RunRetentionAsync(database)).Drafts);
    }
}
