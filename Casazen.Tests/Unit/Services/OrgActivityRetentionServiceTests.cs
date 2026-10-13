using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02b (decision D17): the lines of the activity log older than 12 months are deleted, for every org, and the retention
/// is idempotent. The months are configurable and a value that makes no sense is ignored (a typo never empties the log or keeps
/// it for ever). The real service over EF InMemory; the same statements on PostgreSQL, with the run lock, are run by
/// <c>OrgActivityPostgresTests</c> (CI). <i>The 12 months are to be confirmed with the legal advisor.</i>
/// </summary>
public class OrgActivityRetentionServiceTests
{
    private readonly OrgInvitationTestKit _kit = new();

    private async Task AddAsync(Guid orgId, DateTime when, string subject = "auth0|anna")
    {
        await using var db = _kit.NewDb();
        db.OrgActivityEntries.Add(new OrgActivityEntry
        {
            OrgId = orgId,
            When = when,
            ActorUserId = "auth0|owner",
            Area = OrgActivityArea.Account,
            Type = OrgActivityType.MemberDeactivated,
            SubjectType = OrgActivitySubjectType.Member,
            SubjectId = subject,
            DetailsJson = "{}",
        });
        await db.SaveChangesAsync();
    }

    private async Task<OrgActivityRetentionResult> RunAsync()
    {
        await using var db = _kit.NewDb();
        return await _kit.Retention(db).RunAsync();
    }

    private async Task<List<string>> SubjectsAsync()
    {
        await using var db = _kit.NewDb();
        return await db.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking().OrderBy(e => e.SubjectId).Select(e => e.SubjectId).ToListAsync();
    }

    [Fact]
    public async Task Run_DeletesTheLinesOlderThanTwelveMonths_AndKeepsTheOthers()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await AddAsync(org.Id, _kit.Now.AddMonths(-12).AddMinutes(-1), "old");
        await AddAsync(org.Id, _kit.Now.AddMonths(-13), "older");
        await AddAsync(org.Id, _kit.Now.AddMonths(-12).AddMinutes(1), "just-in-time");
        await AddAsync(org.Id, _kit.Now.AddDays(-1), "recent");

        var result = await RunAsync();

        Assert.Equal((false, 2, _kit.Now.AddMonths(-12)), (result.Skipped, result.Deleted, result.Cutoff));
        Assert.Equal(["just-in-time", "recent"], await SubjectsAsync());
    }

    [Fact]
    public async Task Run_AtTheCutoffItself_KeepsTheLine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await AddAsync(org.Id, _kit.Now.AddMonths(-12), "exactly-twelve-months");

        var result = await RunAsync();

        Assert.Equal(0, result.Deleted);
        Assert.Equal(["exactly-twelve-months"], await SubjectsAsync());
    }

    [Fact]
    public async Task Run_IsIdempotent_TheSecondRunFindsNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await AddAsync(org.Id, _kit.Now.AddMonths(-14), "old");
        await AddAsync(org.Id, _kit.Now.AddDays(-3), "recent");

        var first = await RunAsync();
        var second = await RunAsync();
        var third = await RunAsync();

        Assert.Equal((1, 0, 0), (first.Deleted, second.Deleted, third.Deleted));
        Assert.Equal(["recent"], await SubjectsAsync());
    }

    [Fact]
    public async Task Run_WorksOnEveryOrg()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await AddAsync(orgA.Id, _kit.Now.AddMonths(-20), "a-old");
        await AddAsync(orgB.Id, _kit.Now.AddMonths(-20), "b-old");
        await AddAsync(orgB.Id, _kit.Now.AddDays(-2), "b-recent");

        var result = await RunAsync();

        Assert.Equal(2, result.Deleted);
        Assert.Equal(["b-recent"], await SubjectsAsync());
    }

    [Fact]
    public async Task Run_ABacklogLargerThanABatch_IsDeletedInSeveralBatchesOfTheSameRun()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var old = _kit.Now.AddMonths(-18);
        var total = OrgActivityRetentionService.BatchSize * 2 + 37;
        await using (var db = _kit.NewDb())
        {
            for (var i = 0; i < total; i++)
            {
                db.OrgActivityEntries.Add(new OrgActivityEntry
                {
                    OrgId = org.Id,
                    When = old.AddMinutes(i),
                    Area = OrgActivityArea.Account,
                    Type = OrgActivityType.MemberDeactivated,
                    SubjectType = OrgActivitySubjectType.Member,
                    SubjectId = $"auth0|p{i}",
                });
            }

            await db.SaveChangesAsync();
        }

        await AddAsync(org.Id, _kit.Now.AddDays(-1), "recent");

        var result = await RunAsync();

        Assert.Equal(total, result.Deleted);
        Assert.Equal(["recent"], await SubjectsAsync());
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("6", 6)]
    [InlineData("24", 24)]
    public async Task Run_TheMonthsAreConfigurable(string configured, int months)
    {
        _kit.Settings[OrgActivityRules.RetentionMonthsConfigKey] = configured;
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await AddAsync(org.Id, _kit.Now.AddMonths(-months).AddDays(-2), "past");
        await AddAsync(org.Id, _kit.Now.AddMonths(-months).AddDays(2), "inside");

        var result = await RunAsync();

        Assert.Equal(_kit.Now.AddMonths(-months), result.Cutoff);
        Assert.Equal(["inside"], await SubjectsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public async Task Run_AValueThatMakesNoSense_IsIgnored_AndTheTwelveMonthsApply(string configured)
    {
        _kit.Settings[OrgActivityRules.RetentionMonthsConfigKey] = configured;
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await AddAsync(org.Id, _kit.Now.AddMonths(-13), "old");
        await AddAsync(org.Id, _kit.Now.AddMonths(-11), "recent");

        var result = await RunAsync();

        Assert.Equal(_kit.Now.AddMonths(-12), result.Cutoff);
        Assert.Equal(["recent"], await SubjectsAsync());
    }

    [Fact]
    public async Task Run_AnAbsurdlyLongRetention_IsCutToTenYears()
    {
        _kit.Settings[OrgActivityRules.RetentionMonthsConfigKey] = "100000";

        var result = await RunAsync();

        Assert.Equal(_kit.Now.AddMonths(-OrgActivityRules.MaxRetentionMonths), result.Cutoff);
    }

    [Fact]
    public async Task Run_WithNothingToDelete_DeletesNothing()
    {
        var result = await RunAsync();

        Assert.Equal((false, 0), (result.Skipped, result.Deleted));
    }
}
