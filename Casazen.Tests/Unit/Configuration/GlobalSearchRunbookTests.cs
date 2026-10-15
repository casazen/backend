using System.Reflection;
using Casazen.Core.Features;
using Casazen.Core.Search;
using Casazen.Core.Services;
using Casazen.Infrastructure.Search;
using Casazen.Web.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// UI-13a: the runbook of the global search must say what the code does: the flag, the endpoints, every error code, every kind of
/// result and destination key, the limits, the indexes of the migration, the variables of the rate limit. The pages that list the
/// flags, the rate limits, the variables of a deploy and the runbooks carry the new ones.
/// </summary>
public class GlobalSearchRunbookTests
{
    private static string Runbooks => Path.Combine(FindRepositoryRoot(), "docs", "runbooks");

    private static string Runbook => File.ReadAllText(Path.Combine(Runbooks, "global-search.md"));

    private static string Page(string name) => File.ReadAllText(Path.Combine(Runbooks, name));

    public static TheoryData<string> ErrorCodes() =>
    [
        SearchErrorCodes.QueryTooShort,
        SearchErrorCodes.QueryTooLong,
        LastContextErrors.ContextNotAccessible,
        ProblemCodes.ValidationError,
        ProblemCodes.AccountInactive,
        "rate_limited",
        "not_found",
    ];

    public static TheoryData<string> DestinationKeys()
    {
        var keys = new TheoryData<string>();
        foreach (var field in typeof(SearchDestinations).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
            keys.Add((string)field.GetRawConstantValue()!);
        return keys;
    }

    public static TheoryData<string> Types()
    {
        var types = new TheoryData<string>();
        foreach (var type in SearchTypes.All)
            types.Add(type);
        return types;
    }

    [Theory]
    [MemberData(nameof(ErrorCodes))]
    public void Runbook_EveryErrorCode_IsDocumented(string code) => Assert.Contains($"`{code}`", Runbook);

    [Theory]
    [MemberData(nameof(DestinationKeys))]
    public void Runbook_EveryDestinationKey_IsDocumented(string key) => Assert.Contains($"`{key}`", Runbook);

    [Theory]
    [MemberData(nameof(Types))]
    public void Runbook_EveryKindOfResult_IsDocumented(string type) => Assert.Contains($"`{type}`", Runbook);

    [Fact]
    public void Runbook_NamesTheFlagTheEndpointsAndTheNumbersOfTheCode()
    {
        var runbook = Runbook;

        Assert.Contains($"`Features:{FeatureFlags.GlobalSearch}`", runbook);
        Assert.Contains($"`Features__{FeatureFlags.GlobalSearch}`", runbook);
        Assert.Contains("`GET /api/search?q=&limit=`", runbook);
        Assert.Contains("`PUT /api/me/last-context`", runbook);
        Assert.Contains($"`SearchText.MinQueryLength` ({SearchText.MinQueryLength})", runbook);
        Assert.Contains($"`SearchText.MaxQueryLength` ({SearchText.MaxQueryLength})", runbook);
        Assert.Contains($"`SearchText.MaxTokens` ({SearchText.MaxTokens})", runbook);
        Assert.Contains($"`SearchLimits.DefaultLimit` ({SearchLimits.DefaultLimit})", runbook);
        Assert.Contains($"`SearchLimits.MaxLimit` ({SearchLimits.MaxLimit})", runbook);
        Assert.Contains("private, no-store", runbook);
        Assert.Contains("`GlobalSearch` (`RateLimitPolicies.GlobalSearch`)", runbook);
        Assert.Equal("GlobalSearch", RateLimitPolicies.GlobalSearch);
        Assert.Contains("`RateLimiting__GlobalSearch__PermitLimit`", runbook);
        Assert.Contains("`RateLimiting__GlobalSearch__WindowSeconds`", runbook);
        Assert.Contains("60 requests per minute per user", runbook);
    }

    [Fact]
    public void Runbook_EveryIndexOfTheMigration_IsDocumented_AndTheColumn()
    {
        var runbook = Runbook;

        foreach (var (_, table, _, index) in SearchKeyModel.Keys)
        {
            Assert.Contains($"`{index}`", runbook);
            Assert.Contains($"`{table}`", runbook);
        }

        Assert.Contains($"`{SearchKeyModel.BookingCodePrefixIndex}`", runbook);
        Assert.Contains($"`{SearchKeyModel.KeyProperty}`", runbook);
        Assert.Contains("`AddGlobalSearchKeys`", runbook);
        Assert.Contains($"`{SearchKeyModel.TextSearchConfig}`", runbook);
        Assert.Contains("pg_trgm", runbook);
    }

    [Fact]
    public void OtherPages_ListTheFlagThePolicyTheVariablesTheMigrationAndTheRunbook()
    {
        var flags = Page("feature-flags.md");
        Assert.Contains($"`{FeatureFlags.GlobalSearch}`", flags);
        Assert.Contains($"`Features__{FeatureFlags.GlobalSearch}`", flags);

        var proxy = Page("proxy-ip.md");
        Assert.Contains($"`{RateLimitPolicies.GlobalSearch}`", proxy);
        Assert.Contains("RateLimiting__GlobalSearch__PermitLimit", proxy);

        var checklist = Page("deploy-checklist.md");
        Assert.Contains($"`Features__{FeatureFlags.GlobalSearch}`", checklist);
        Assert.Contains("RateLimiting__GlobalSearch__*", checklist);
        Assert.Contains("`AddGlobalSearchKeys`", checklist);

        Assert.Contains("(global-search.md)", Page("index.md"));
        var technical = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "TECHNICAL.md"));
        Assert.Contains("/api/search", technical);
        Assert.Contains("/api/me/last-context", technical);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
