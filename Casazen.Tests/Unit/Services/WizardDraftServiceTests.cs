using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// <see cref="WizardDraftService"/> (ADR-024, E01/F02/US01) on an in-memory database, one context per call as one per request: the version
/// check that never loses an update, the isolation between people and orgs, the 30 days, and the discard that keeps the row without the
/// person. What needs PostgreSQL (the encrypted column, <c>xmin</c>, the unique indexes, two saves at once) is in
/// <c>WizardDraftPostgresTests</c>.
/// </summary>
public class WizardDraftServiceTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrgB = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid BookingId = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private const string Alice = "auth0|alice";
    private const string Bob = "auth0|bob";

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 5, 9, 30, 15, TimeSpan.Zero));

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private async Task<WizardDraftResult> SaveAsync(
        Guid orgId, string userId, WizardType type, Guid? entityId, int clientVersion, string json)
    {
        await using var db = NewDb();
        using var data = JsonDocument.Parse(json);
        return await new WizardDraftService(db, _clock)
            .SaveDraftAsync(orgId, userId, new SaveDraftRequest(type, entityId, clientVersion, data), CancellationToken.None);
    }

    private async Task<WizardDraftDto?> GetAsync(Guid orgId, string userId, WizardType type, Guid? entityId)
    {
        await using var db = NewDb();
        return await new WizardDraftService(db, _clock).GetDraftAsync(orgId, userId, type, entityId, CancellationToken.None);
    }

    private async Task<List<WizardDraft>> RowsAsync()
    {
        await using var db = NewDb();
        return await db.WizardDrafts.AsNoTracking().OrderBy(d => d.UserId).ToListAsync();
    }

    [Fact]
    public async Task SaveDraft_AClientBehindTheServer_GetsTheConflictWithTheServerDraft_AndNothingIsWritten()
    {
        var first = await SaveAsync(OrgA, Alice, WizardType.ListingCreate, null, 0, """{"name":"Casa uno"}""");
        var second = await SaveAsync(OrgA, Alice, WizardType.ListingCreate, null, 1, """{"name":"Casa due"}""");
        Assert.Equal((DraftStatus.Saved, 1), (first.Status, first.ServerVersion));
        Assert.Equal((DraftStatus.Saved, 2), (second.Status, second.ServerVersion));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, second.ServerDraft!.LastSavedAt);

        // Another device still on version 1 saves: the server is ahead, so it is told so and shown the draft it would have overwritten.
        var stale = await SaveAsync(OrgA, Alice, WizardType.ListingCreate, null, 1, """{"name":"Casa tre"}""");

        Assert.Equal(DraftStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.ServerVersion);
        Assert.Equal("Casa due", stale.ServerDraft!.Data.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, stale.ServerDraft.Version);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(2, row.Version);
        Assert.Contains("Casa due", row.DraftJson);

        // The client that took the server's version saves on top of it.
        var next = await SaveAsync(OrgA, Alice, WizardType.ListingCreate, null, 2, """{"name":"Casa tre"}""");
        Assert.Equal((DraftStatus.Saved, 3), (next.Status, next.ServerVersion));
    }

    [Fact]
    public async Task GetDraft_AnotherPersonOrAnotherOrg_FindsNothing_TheOwnerGetsItWithItsPersonalDataPutBack()
    {
        await SaveAsync(
            OrgA, Alice, WizardType.CheckIn, BookingId, 0,
            """{"step":"ospiti","guests":[{"room":1,"guestName":"Giulia Verdi","documentNumber":"CA12345AB"}]}""");

        // The same org and wizard, another person: no draft, the answer of a draft that does not exist.
        Assert.Null(await GetAsync(OrgA, Bob, WizardType.CheckIn, BookingId));
        // The same person in another org, another wizard, another entity: none either.
        Assert.Null(await GetAsync(OrgB, Alice, WizardType.CheckIn, BookingId));
        Assert.Null(await GetAsync(OrgA, Alice, WizardType.CheckOut, BookingId));
        Assert.Null(await GetAsync(OrgA, Alice, WizardType.CheckIn, null));

        var own = await GetAsync(OrgA, Alice, WizardType.CheckIn, BookingId);
        Assert.NotNull(own);
        var guest = own.Data.RootElement.GetProperty("guests")[0];
        Assert.Equal("Giulia Verdi", guest.GetProperty("guestName").GetString());
        Assert.Equal("CA12345AB", guest.GetProperty("documentNumber").GetString());
        Assert.Equal(1, guest.GetProperty("room").GetInt32());

        // A call with no user would match the anonymized rows (they have none): it is refused, not answered.
        await Assert.ThrowsAsync<ArgumentException>(() => GetAsync(OrgA, string.Empty, WizardType.CheckIn, BookingId));
    }

    [Fact]
    public async Task SaveDraft_EverySave_SetsTheExpiryThirtyDaysAfterTheSave()
    {
        var saved = _clock.GetUtcNow().UtcDateTime;
        await SaveAsync(OrgA, Alice, WizardType.BookingCreate, null, 0, """{"step":1}""");
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(saved, row.LastSavedAt);
        Assert.Equal(saved.AddDays(30), row.ExpiresAt);

        _clock.Advance(TimeSpan.FromDays(10));
        await SaveAsync(OrgA, Alice, WizardType.BookingCreate, null, 1, """{"step":2}""");

        row = Assert.Single(await RowsAsync());
        Assert.Equal(saved.AddDays(10), row.LastSavedAt);
        Assert.Equal(saved.AddDays(40), row.ExpiresAt);
    }

    [Fact]
    public async Task SaveDraft_ADraftThatExpired_IsGoneForThePerson_AndTheNextSaveRewritesItNotAConflict()
    {
        await SaveAsync(OrgA, Alice, WizardType.BookingCreate, null, 0, """{"step":1}""");
        await SaveAsync(OrgA, Alice, WizardType.BookingCreate, null, 1, """{"step":2}""");
        _clock.Advance(TimeSpan.FromDays(31));

        Assert.Null(await GetAsync(OrgA, Alice, WizardType.BookingCreate, null));
        var again = await SaveAsync(OrgA, Alice, WizardType.BookingCreate, null, 0, """{"step":9}""");

        // The version keeps counting, so a copy an old device still holds can never be taken for the new draft.
        Assert.Equal((DraftStatus.Saved, 3), (again.Status, again.ServerVersion));
        var row = Assert.Single(await RowsAsync());
        Assert.Contains("9", row.DraftJson);
    }

    [Fact]
    public async Task DeleteDraft_DiscardsOnlyTheCallersDraft_AndKeepsTheRowWithoutTheDataOrThePerson()
    {
        await SaveAsync(OrgA, Alice, WizardType.CheckIn, BookingId, 0, """{"guestName":"Giulia Verdi","step":2}""");
        await SaveAsync(OrgA, Bob, WizardType.CheckIn, BookingId, 0, """{"guestName":"Luca Neri","step":4}""");

        await using (var db = NewDb())
        {
            var service = new WizardDraftService(db, _clock);
            // Another org, another wizard, another entity: nothing of Bob's or Alice's is reached.
            await service.DeleteDraftAsync(OrgB, Alice, WizardType.CheckIn, BookingId, CancellationToken.None);
            await service.DeleteDraftAsync(OrgA, Alice, WizardType.CheckOut, BookingId, CancellationToken.None);
            await service.DeleteDraftAsync(OrgA, Alice, WizardType.CheckIn, null, CancellationToken.None);
        }

        Assert.NotNull(await GetAsync(OrgA, Alice, WizardType.CheckIn, BookingId));

        await using (var db = NewDb())
        {
            await new WizardDraftService(db, _clock).DeleteDraftAsync(OrgA, Alice, WizardType.CheckIn, BookingId, CancellationToken.None);
        }

        Assert.Null(await GetAsync(OrgA, Alice, WizardType.CheckIn, BookingId));
        var bobs = await GetAsync(OrgA, Bob, WizardType.CheckIn, BookingId);
        Assert.Equal("Luca Neri", bobs!.Data.RootElement.GetProperty("guestName").GetString());

        // Both rows are still there: the discarded one has no data and no person (as after the retention), Bob's is intact.
        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        var discarded = Assert.Single(rows, r => r.UserId == string.Empty);
        Assert.Equal("{}", discarded.DraftJson);
        Assert.Null(discarded.EncryptedFieldsJson);
        Assert.Equal((OrgA, WizardType.CheckIn, (Guid?)BookingId), (discarded.OrgId, discarded.WizardType, discarded.EntityId));
        Assert.Equal(Bob, Assert.Single(rows, r => r.UserId != string.Empty).UserId);

        // Discarding what is not there is not an error, and a new draft of the same wizard starts from the beginning.
        await using (var db = NewDb())
        {
            await new WizardDraftService(db, _clock).DeleteDraftAsync(OrgA, Alice, WizardType.CheckIn, BookingId, CancellationToken.None);
        }

        var restarted = await SaveAsync(OrgA, Alice, WizardType.CheckIn, BookingId, 0, """{"step":1}""");
        Assert.Equal((DraftStatus.Saved, 1), (restarted.Status, restarted.ServerVersion));
    }

    [Fact]
    public async Task SaveDraft_DataThatIsNotAnObject_OrIsTooLarge_IsRefused_AndNothingIsWritten()
    {
        await using var db = NewDb();
        var service = new WizardDraftService(db, _clock);

        using var list = JsonDocument.Parse("[1,2]");
        var notAnObject = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.SaveDraftAsync(OrgA, Alice, new SaveDraftRequest(WizardType.ListingCreate, null, 0, list), CancellationToken.None));
        Assert.Equal(WizardDraftErrors.DataInvalid, notAnObject.Code);

        using var huge = JsonDocument.Parse("{\"notes\":\"" + new string('x', WizardDraftLimits.MaxDataBytes) + "\"}");
        var tooLarge = await Assert.ThrowsAsync<DomainPayloadTooLargeException>(() =>
            service.SaveDraftAsync(OrgA, Alice, new SaveDraftRequest(WizardType.ListingCreate, null, 0, huge), CancellationToken.None));
        Assert.Equal(WizardDraftErrors.TooLarge, tooLarge.Code);

        using var ok = JsonDocument.Parse("{}");
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.SaveDraftAsync(OrgA, Alice, new SaveDraftRequest((WizardType)99, null, 0, ok), CancellationToken.None));
        Assert.Equal(WizardDraftErrors.TypeUnknown, unknown.Code);

        Assert.Empty(await RowsAsync());
    }
}
