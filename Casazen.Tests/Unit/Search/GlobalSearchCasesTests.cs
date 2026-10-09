using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Xunit;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// UI-13a: every question of <see cref="SearchCases"/> on the in-memory provider. The same questions, with the same expected ids, run
/// on PostgreSQL in <c>GlobalSearchPostgresTests</c>: what is found must not depend on the provider (the full-text index there, the
/// words compared in C# here).
/// </summary>
public class GlobalSearchCasesTests : IAsyncLifetime
{
    private AppDbContext _db = null!;
    private SearchWorld _world = null!;

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var searchCase in SearchCases.All)
            data.Add(searchCase.Name);
        return data;
    }

    public async Task InitializeAsync()
    {
        _db = SearchScenario.NewInMemoryDb();
        _world = await SearchScenario.SeedAsync(_db, computeKeys: true);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public void TheCases_HaveDistinctNames_AndCoverEveryKindOfCaller()
    {
        Assert.Equal(SearchCases.All.Count, SearchCases.All.Select(c => c.Name).Distinct().Count());
        Assert.Equal(
            Enum.GetValues<SearchCaller>().Order(),
            SearchCases.All.Select(c => c.Caller).Distinct().Order());
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Case_FindsExactlyWhatIsExpected(string name)
    {
        var failure = await SearchCases.RunAsync(_db, _world, SearchCases.Named(name));

        Assert.True(failure is null, failure);
    }

    [Fact]
    public async Task TheWorld_IsSoundForPostgreSql_NoDanglingKeyNoDuplicateInAUniqueIndex()
    {
        // The in-memory provider checks neither foreign keys nor unique indexes; PostgreSQL does. The same rows are written to it in CI.
        await HostScopeScenario.AssertReferentialIntegrityAsync(_db);
    }
}
