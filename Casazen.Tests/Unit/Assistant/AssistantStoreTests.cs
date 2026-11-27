using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01/T02, the lost race of the assistant (NF-67: no two changes claim the same version of the instructions). <see cref="AssistantStore"/> turns the two ways of losing it, the concurrency
/// token of the rules row and a unique index (23505) of the rows that are written for the first time, into the one 409 <c>assistant_concurrent_change</c>, and lets everything else rise as it is. The
/// 23505 branch cannot be reached without PostgreSQL, so the failure of the database is made by hand, as the rest of the suite does: a context whose save throws what PostgreSQL would.
/// </summary>
public class AssistantStoreTests
{
    private const string User = "auth0|host";

    private static readonly Guid Org = Guid.Parse("11111111-aaaa-4aaa-8aaa-000000000001");

    /// <summary>The four unique indexes of the assistant: the ones a lost race on a first write can hit, which the store knows by name.</summary>
    public static TheoryData<string> LostRaceIndexes => new()
    {
        OrgAssistantRules.OrgIndexName,
        PropertyAssistantSettings.OrgPropertyIndexName,
        AssistantSituationSetting.OrgSituationIndexName,
        AssistantSituationSetting.PropertySituationIndexName,
    };

    /// <summary>What is not the race of the assistant although it looks like it: another index, the name of one of ours on another kind of violation, a unique violation that names nothing.</summary>
    public static TheoryData<string, string?> NotTheRaceOfTheAssistant => new()
    {
        { PostgresErrorCodes.UniqueViolation, "UIX_SomethingElse_OrgId" },
        { PostgresErrorCodes.ForeignKeyViolation, OrgAssistantRules.OrgIndexName },
        { PostgresErrorCodes.CheckViolation, AssistantSituationSetting.OrgSituationIndexName },
        { PostgresErrorCodes.UniqueViolation, null },
    };

    [Theory]
    [MemberData(nameof(LostRaceIndexes))]
    public async Task SaveAsync_ALostRaceOnAUniqueIndexOfTheAssistant_IsThe409OfTheAssistant_AndNothingStaysTracked(string index)
    {
        await using var db = new FailingSaveDbContext(AssistantTestKit.NewStore());
        db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = Org });
        db.Failure = UniqueViolation(index);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => AssistantStore.SaveAsync(db, CancellationToken.None));

        Assert.Equal("assistant_concurrent_change", error.Code);
        Assert.Equal("AssistantConcurrentChange", error.MessageKey);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SaveAsync_ALostConcurrencyToken_IsTheSame409_AndNothingStaysTracked()
    {
        await using var db = new FailingSaveDbContext(AssistantTestKit.NewStore());
        db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = Org });
        db.Failure = new DbUpdateConcurrencyException("the version of the instructions moved");

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => AssistantStore.SaveAsync(db, CancellationToken.None));

        Assert.Equal("assistant_concurrent_change", error.Code);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [MemberData(nameof(NotTheRaceOfTheAssistant))]
    public async Task SaveAsync_AnyOtherFailureOfTheDatabase_RisesAsItIs_NothingIsSwallowedNorTurnedIntoAConflict(string sqlState, string? constraint)
    {
        await using var db = new FailingSaveDbContext(AssistantTestKit.NewStore());
        db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = Org });
        var failure = new DbUpdateException("save failed", new PostgresException("the database refused it", "ERROR", "ERROR", sqlState, constraintName: constraint));
        db.Failure = failure;

        var raised = await Assert.ThrowsAsync<DbUpdateException>(() => AssistantStore.SaveAsync(db, CancellationToken.None));

        Assert.Same(failure, raised);
    }

    [Fact]
    public async Task SaveAsync_AFailureThatIsNotADatabaseErrorOfTheSave_RisesAsItIs()
    {
        await using var db = new FailingSaveDbContext(AssistantTestKit.NewStore());
        db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = Org });
        var withoutDatabaseError = new DbUpdateException("save failed");
        var unexpected = new InvalidOperationException("boom");

        db.Failure = withoutDatabaseError;
        Assert.Same(withoutDatabaseError, await Assert.ThrowsAsync<DbUpdateException>(() => AssistantStore.SaveAsync(db, CancellationToken.None)));

        db.Failure = unexpected;
        Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => AssistantStore.SaveAsync(db, CancellationToken.None)));
    }

    [Fact]
    public async Task SaveAsync_WhenNothingFails_WritesWhatTheContextTracks()
    {
        var store = AssistantTestKit.NewStore();
        await using (var db = new FailingSaveDbContext(store))
        {
            db.OrgAssistantRules.Add(new OrgAssistantRules { OrgId = Org });
            await AssistantStore.SaveAsync(db, CancellationToken.None);
        }

        await using var after = AssistantTestKit.NewDb(store);
        Assert.Equal(Org, (await after.OrgAssistantRules.AsNoTracking().SingleAsync()).OrgId);
    }

    /// <summary>
    /// The services over the same failure: each of the first writes that two requests can make at the same moment is the 409 and writes nothing, and the first change of any kind of an organization
    /// that has no rules row yet (it creates the row to grow its version) is the same race on the index of the organization.
    /// </summary>
    [Fact]
    public async Task TheServices_OverALostRaceOnTheirFirstWrite_AnswerTheSame409_AndWriteNothing()
    {
        var store = AssistantTestKit.NewStore();
        await using var db = new FailingSaveDbContext(store);
        var property = await AssistantTestKit.AddPropertyAsync(db, Org);

        db.Failure = UniqueViolation(OrgAssistantRules.OrgIndexName);
        await AssertConflictAsync(() => AssistantTestKit.Rules(db).SaveAsync(Org, AssistantTestKit.RulesInput(), User));
        await AssertConflictAsync(() => AssistantTestKit.Knowledge(db).CreateEntryAsync(Org, null, AssistantTestKit.Fact(KnowledgeTopic.Contacts, "Chiama Marco."), User));

        db.Failure = UniqueViolation(PropertyAssistantSettings.OrgPropertyIndexName);
        await AssertConflictAsync(() => AssistantTestKit.Rules(db).SavePropertySettingsAsync(Org, property.Id, new PropertyAssistantSettingsInput(false, null, null, null, null, null, null), User));

        db.Failure = UniqueViolation(AssistantSituationSetting.OrgSituationIndexName);
        await AssertConflictAsync(() => AssistantTestKit.Situations(db).SaveAsync(Org, null, AssistantSituation.Parking, AssistantMode.Draft, null, null, User));

        db.Failure = UniqueViolation(AssistantSituationSetting.PropertySituationIndexName);
        await AssertConflictAsync(() => AssistantTestKit.Situations(db).SaveAsync(Org, property.Id, AssistantSituation.Parking, AssistantMode.Draft, null, null, User));

        db.Failure = null;
        await using var after = AssistantTestKit.NewDb(store);
        Assert.Empty(await after.OrgAssistantRules.AsNoTracking().ToListAsync());
        Assert.Empty(await after.PropertyAssistantSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await after.AssistantSituationSettings.AsNoTracking().ToListAsync());
        Assert.Empty(await after.PropertyKnowledgeEntries.AsNoTracking().ToListAsync());
    }

    private static async Task AssertConflictAsync(Func<Task> change)
    {
        var error = await Assert.ThrowsAsync<DomainConflictException>(change);
        Assert.Equal("assistant_concurrent_change", error.Code);
    }

    /// <summary>What PostgreSQL gives a save that loses the race on a unique index: a 23505 that names the index.</summary>
    private static DbUpdateException UniqueViolation(string index) => new(
        "save failed",
        new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: index));

    /// <summary>A context over an InMemory store whose save throws <see cref="Failure"/> while it is set, and saves as any other when it is not.</summary>
    private sealed class FailingSaveDbContext(string store)
        : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store).Options)
    {
        public Exception? Failure { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Failure is { } failure ? Task.FromException<int>(failure) : base.SaveChangesAsync(cancellationToken);
    }
}
