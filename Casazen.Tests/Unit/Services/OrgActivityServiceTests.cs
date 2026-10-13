using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02b: reading the activity log. The filters (period, event, area, person, «system»), the paging and its order (newest first,
/// by id among the lines of one instant, so no page repeats or skips a line), and that an org only ever reads its own lines. The
/// real service over EF InMemory; the same queries on PostgreSQL are run by <c>OrgActivityPostgresTests</c> (CI).
/// </summary>
public class OrgActivityServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<Guid> OrgAsync(string ownerId = "auth0|owner")
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(ownerId);
        return org.Id;
    }

    private async Task<Guid> AddAsync(
        Guid orgId,
        OrgActivityType type,
        DateTime when,
        string? actor = "auth0|owner",
        string subject = "auth0|anna",
        OrgActivityArea? area = null)
    {
        var info = OrgActivityCatalog.Describe(type);
        var entry = new OrgActivityEntry
        {
            OrgId = orgId,
            When = when,
            ActorUserId = actor,
            Area = area ?? info.DefaultArea,
            Type = type,
            SubjectType = info.SubjectType,
            SubjectId = subject,
            DetailsJson = "{}",
        };
        await using var db = _kit.NewDb();
        db.OrgActivityEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<OrgActivityPage> ListAsync(Guid orgId, OrgActivityFilter? filter = null, int page = 1, int pageSize = 50)
    {
        await using var db = _kit.NewDb();
        return await _kit.ActivityReader(db).ListAsync(orgId, filter ?? new OrgActivityFilter(), page, pageSize);
    }

    private async Task<List<OrgActivityItem>> StreamAsync(Guid orgId, OrgActivityFilter? filter = null)
    {
        await using var db = _kit.NewDb();
        var items = new List<OrgActivityItem>();
        await foreach (var item in _kit.ActivityReader(db).StreamAsync(orgId, filter ?? new OrgActivityFilter()))
            items.Add(item);
        return items;
    }

    // ─── Order and paging ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_NewestFirst_WithTheTotalAndThePageAsked()
    {
        var org = await OrgAsync();
        for (var i = 0; i < 5; i++)
            await AddAsync(org, OrgActivityType.MemberDeactivated, T0.AddMinutes(i), subject: $"auth0|p{i}");

        var first = await ListAsync(org, pageSize: 2);
        var third = await ListAsync(org, page: 3, pageSize: 2);
        var beyond = await ListAsync(org, page: 9, pageSize: 2);

        Assert.Equal((5, 1, 2), (first.TotalCount, first.Page, first.PageSize));
        Assert.Equal(["auth0|p4", "auth0|p3"], first.Items.Select(i => i.SubjectId));
        Assert.Equal(["auth0|p0"], third.Items.Select(i => i.SubjectId));
        Assert.Empty(beyond.Items);
        Assert.Equal(5, beyond.TotalCount);
    }

    [Fact]
    public async Task List_LinesOfTheSameInstant_AreOrderedById_SoNoPageRepeatsOrSkipsALine()
    {
        var org = await OrgAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
            ids.Add(await AddAsync(org, OrgActivityType.MemberDeactivated, T0, subject: $"auth0|p{i}"));

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
            seen.AddRange((await ListAsync(org, page: page, pageSize: 2)).Items.Select(i => i.Id));

        Assert.Equal(ids.OrderByDescending(id => id), seen);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task List_PagesCountFromOne_AndAPageBelowOneIsTheFirst(int asked, int expected)
    {
        var org = await OrgAsync();
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0);

        var page = await ListAsync(org, page: asked);

        Assert.Equal(expected, page.Page);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(100000, 100)]
    public async Task List_ThePageSizeIsKeptBetweenOneAndAHundred(int asked, int expected)
    {
        var org = await OrgAsync();

        Assert.Equal(expected, (await ListAsync(org, pageSize: asked)).PageSize);
    }

    [Fact]
    public async Task List_AnEnormousPage_IsAnEmptyPage_NotAnOverflow()
    {
        var org = await OrgAsync();
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0);

        var page = await ListAsync(org, page: int.MaxValue, pageSize: 100);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_AnEmptyLog_IsAnEmptyPage()
    {
        var org = await OrgAsync();

        var page = await ListAsync(org);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Stream_IsEveryMatchingLineInTheOrderOfThePages()
    {
        var org = await OrgAsync();
        for (var i = 0; i < 6; i++)
            await AddAsync(org, OrgActivityType.MemberDeactivated, T0.AddMinutes(i % 3), subject: $"auth0|p{i}");

        var streamed = await StreamAsync(org);
        var paged = new List<OrgActivityItem>();
        for (var page = 1; page <= 3; page++)
            paged.AddRange((await ListAsync(org, page: page, pageSize: 2)).Items);

        Assert.Equal(paged.Select(i => i.Id), streamed.Select(i => i.Id));
        Assert.Equal(6, streamed.Count);
    }

    // ─── Filters ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_FromAndTo_AreBothIncluded()
    {
        var org = await OrgAsync();
        for (var i = 0; i < 5; i++)
            await AddAsync(org, OrgActivityType.MemberDeactivated, T0.AddHours(i), subject: $"auth0|p{i}");

        var between = await ListAsync(org, new OrgActivityFilter(From: T0.AddHours(1), To: T0.AddHours(3)));
        var from = await ListAsync(org, new OrgActivityFilter(From: T0.AddHours(3)));
        var to = await ListAsync(org, new OrgActivityFilter(To: T0.AddHours(1)));

        Assert.Equal(["auth0|p3", "auth0|p2", "auth0|p1"], between.Items.Select(i => i.SubjectId));
        Assert.Equal(["auth0|p4", "auth0|p3"], from.Items.Select(i => i.SubjectId));
        Assert.Equal(["auth0|p1", "auth0|p0"], to.Items.Select(i => i.SubjectId));
        Assert.Equal(3, between.TotalCount);
    }

    [Fact]
    public async Task List_ByEvent_OneOrSeveral()
    {
        var org = await OrgAsync();
        await AddAsync(org, OrgActivityType.MemberInvited, T0, subject: "inv-1");
        await AddAsync(org, OrgActivityType.MemberRoleChanged, T0.AddMinutes(1));
        await AddAsync(org, OrgActivityType.PlanChanged, T0.AddMinutes(2), actor: null, subject: "org");
        await AddAsync(org, OrgActivityType.MemberRoleChanged, T0.AddMinutes(3));

        var one = await ListAsync(org, new OrgActivityFilter(Types: [OrgActivityType.MemberRoleChanged]));
        var several = await ListAsync(org, new OrgActivityFilter(Types: [OrgActivityType.MemberInvited, OrgActivityType.PlanChanged]));

        Assert.Equal([OrgActivityType.MemberRoleChanged, OrgActivityType.MemberRoleChanged], one.Items.Select(i => i.Type));
        Assert.Equal([OrgActivityType.PlanChanged, OrgActivityType.MemberInvited], several.Items.Select(i => i.Type));
    }

    [Fact]
    public async Task List_ByArea()
    {
        var org = await OrgAsync();
        await AddAsync(org, OrgActivityType.MemberInvited, T0);
        await AddAsync(org, OrgActivityType.PropertyModeChanged, T0.AddMinutes(1), actor: null, subject: "prop", area: OrgActivityArea.LongRent);
        await AddAsync(org, OrgActivityType.TrustedSupplierAdded, T0.AddMinutes(2), subject: "sup");

        Assert.Equal(
            [OrgActivityType.PropertyModeChanged],
            (await ListAsync(org, new OrgActivityFilter(Area: OrgActivityArea.LongRent))).Items.Select(i => i.Type));
        Assert.Equal(
            [OrgActivityType.TrustedSupplierAdded],
            (await ListAsync(org, new OrgActivityFilter(Area: OrgActivityArea.Supplier))).Items.Select(i => i.Type));
        Assert.Empty((await ListAsync(org, new OrgActivityFilter(Area: OrgActivityArea.ShortRent))).Items);
    }

    [Fact]
    public async Task List_ByThePersonWhoActed_OrByNoPerson()
    {
        var org = await OrgAsync();
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0, actor: "auth0|owner", subject: "auth0|a");
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0.AddMinutes(1), actor: "auth0|admin", subject: "auth0|b");
        await AddAsync(org, OrgActivityType.PlanChanged, T0.AddMinutes(2), actor: null, subject: "org");

        var admin = await ListAsync(org, new OrgActivityFilter(ActorUserId: "auth0|admin"));
        var system = await ListAsync(org, new OrgActivityFilter(SystemActor: true));
        var systemWins = await ListAsync(org, new OrgActivityFilter(ActorUserId: "auth0|admin", SystemActor: true));

        Assert.Equal(["auth0|b"], admin.Items.Select(i => i.SubjectId));
        Assert.Equal([OrgActivityType.PlanChanged], system.Items.Select(i => i.Type));
        Assert.Equal([OrgActivityType.PlanChanged], systemWins.Items.Select(i => i.Type));
    }

    [Fact]
    public async Task List_TheFiltersCombine()
    {
        var org = await OrgAsync();
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0, actor: "auth0|admin", subject: "auth0|a");
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0.AddDays(2), actor: "auth0|admin", subject: "auth0|b");
        await AddAsync(org, OrgActivityType.MemberReactivated, T0.AddDays(2), actor: "auth0|admin", subject: "auth0|c");
        await AddAsync(org, OrgActivityType.MemberDeactivated, T0.AddDays(2), actor: "auth0|owner", subject: "auth0|d");

        var page = await ListAsync(org, new OrgActivityFilter(
            From: T0.AddDays(1),
            Types: [OrgActivityType.MemberDeactivated],
            Area: OrgActivityArea.Account,
            ActorUserId: "auth0|admin"));

        Assert.Equal(["auth0|b"], page.Items.Select(i => i.SubjectId));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_TheDetailsComeBackAsAnObject_AndEveryFieldOfTheLine()
    {
        var org = await OrgAsync();
        var id = Guid.NewGuid();
        await using (var db = _kit.NewDb())
        {
            db.OrgActivityEntries.Add(new OrgActivityEntry
            {
                Id = id,
                OrgId = org,
                When = T0,
                ActorUserId = "auth0|owner",
                Area = OrgActivityArea.Account,
                Type = OrgActivityType.MemberRoleChanged,
                SubjectType = OrgActivitySubjectType.Member,
                SubjectId = "auth0|anna",
                DetailsJson = "{\"fromRole\":\"Collaborator\",\"toRole\":\"Admin\"}",
            });
            await db.SaveChangesAsync();
        }

        var item = Assert.Single((await ListAsync(org)).Items);

        Assert.Equal(
            (id, T0, "auth0|owner", OrgActivityArea.Account, OrgActivityType.MemberRoleChanged, OrgActivitySubjectType.Member, "auth0|anna"),
            (item.Id, item.When, item.ActorUserId, item.Area, item.Type, item.SubjectType, item.SubjectId));
        Assert.Equal(new Dictionary<string, string> { ["fromRole"] = "Collaborator", ["toRole"] = "Admin" }, item.Details);
    }

    // ─── Whose lines ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_AnOrgReadsItsOwnLinesOnly()
    {
        var orgA = await OrgAsync("auth0|owner-a");
        var orgB = await OrgAsync("auth0|owner-b");
        await AddAsync(orgA, OrgActivityType.MemberDeactivated, T0, subject: "auth0|in-a");
        await AddAsync(orgB, OrgActivityType.MemberDeactivated, T0, subject: "auth0|in-b");

        Assert.Equal(["auth0|in-a"], (await ListAsync(orgA)).Items.Select(i => i.SubjectId));
        Assert.Equal(["auth0|in-b"], (await StreamAsync(orgB)).Select(i => i.SubjectId));
        Assert.Empty((await ListAsync(Guid.NewGuid())).Items);
    }

    [Fact]
    public async Task List_WhenTheRequestIsInAnotherOrg_TheTenantFilterAnswersNothing()
    {
        var orgA = await OrgAsync("auth0|owner-a");
        var orgB = await OrgAsync("auth0|owner-b");
        await AddAsync(orgA, OrgActivityType.MemberDeactivated, T0, subject: "auth0|in-a");
        await AddAsync(orgB, OrgActivityType.MemberDeactivated, T0, subject: "auth0|in-b");

        // The caller is a member of org A; asking for the lines of org B (a mistake in a controller) finds nothing.
        await using var asA = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_kit.Database).Options,
            new FixedTenantContext(orgA));
        var service = new OrgActivityService(asA);

        Assert.Empty((await service.ListAsync(orgB, new OrgActivityFilter(), 1, 50)).Items);
        Assert.Equal(["auth0|in-a"], (await service.ListAsync(orgA, new OrgActivityFilter(), 1, 50)).Items.Select(i => i.SubjectId));
    }

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
