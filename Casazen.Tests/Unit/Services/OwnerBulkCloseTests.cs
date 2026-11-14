using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OwnerMonthEndWorld;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US03/T01 (NF-110, NF-115): the month-end for every owner at once. Each owner is its own transaction and its own result and a run never reads as «all done» while one
/// failed; a flagged version is never approved in bulk; running a step twice changes nothing more; an owner of another org is never served; the permission of a step is the one
/// of the single operation; more than 20 owners are a background job whose progress only the org that queued it reads.
/// </summary>
public class OwnerBulkCloseTests
{
    private static readonly Guid CallerOrg = Guid.NewGuid();
    private const string CallerUser = "auth0|agency-admin";

    // ---------------------------------------------------------------- the steps

    /// <summary>
    /// Money / data: the approval in bulk skips a flagged version with its reason and says what it did for each owner; an owner already approved is left as it is, and running
    /// the step again changes nothing more (no version is made, the flagged one is still flagged).
    /// </summary>
    [Fact]
    public async Task BulkApprove_SkipsFlaggedAndReportsEach_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var clean = await OwnerWithAStayAsync(world);
        var flagged = await OwnerWithAStayAsync(world, withMandate: false);
        var other = await OwnerWithAStayAsync(world);
        var owners = new[] { clean, flagged, other };

        // The posting tells each owner what was kept out of its ledger: a stay without a mandate is held back, never a commission of zero.
        var posted = await world.BulkAsync(OwnerBulkAction.Post, owners);
        Assert.Equal([true, false, true], posted.Select(r => r.Ok));
        Assert.Equal(OwnerReadinessCodes.MandateMissingForStay, posted[1].Code);
        Assert.Equal([OwnerReadinessCodes.MandateMissingForStay], posted[1].Issues);

        // The generation made a version for each owner; the one that came out flagged says why.
        var generated = await world.BulkAsync(OwnerBulkAction.Generate, owners);
        Assert.All(generated, r => Assert.True(r.Ok && !r.Unchanged));
        Assert.Equal([null, OwnerAccountingErrors.VersionFlagged, null], generated.Select(r => r.Code));
        Assert.Equal([OwnerReadinessCodes.MandateMissingForStay], generated[1].Issues);

        var approved = await world.BulkAsync(OwnerBulkAction.Approve, owners);
        Assert.Equal(owners, approved.Select(r => r.OwnerId));
        Assert.Equal([true, false, true], approved.Select(r => r.Ok));
        Assert.Equal(OwnerAccountingErrors.VersionFlagged, approved[1].Code);
        Assert.Equal([OwnerReadinessCodes.MandateMissingForStay], approved[1].Issues);
        Assert.Equal(OwnerStatementVersionStatus.Approved, (await world.VersionAsync(clean)).Status);
        Assert.Equal(OwnerStatementVersionStatus.Flagged, (await world.VersionAsync(flagged)).Status);
        Assert.Equal(OwnerStatementVersionStatus.Approved, (await world.VersionAsync(other)).Status);
        var run = new OwnerBulkRun(null, OwnerBulkAction.Approve, 2026, 10, OwnerBulkRunStatus.Completed, owners.Length, approved);
        Assert.Equal((2, 0, 1, false), (run.Succeeded, run.Unchanged, run.Failed, run.AllDone));

        // Running it again changes nothing more: the approved are unchanged, the flagged is still skipped, no version is made.
        var again = await world.BulkAsync(OwnerBulkAction.Approve, owners);
        Assert.Equal([true, false, true], again.Select(r => r.Ok));
        Assert.True(again[0].Unchanged);
        Assert.True(again[2].Unchanged);
        Assert.Equal(OwnerAccountingErrors.VersionFlagged, again[1].Code);
        Assert.All(owners, owner => Assert.Equal(1, world.Db.OwnerStatementVersions.Count(v => v.PropertyOwnerId == owner)));
        var generatedAgain = await world.BulkAsync(OwnerBulkAction.Generate, owners);
        Assert.All(generatedAgain, r => Assert.True(r.Ok && r.Unchanged));
        Assert.All(owners, owner => Assert.Equal(1, world.Db.OwnerStatementVersions.Count(v => v.PropertyOwnerId == owner)));
    }

    /// <summary>Data: an empty list is never «every owner»; without a list the owners of the month are the ones the readiness knows; a step that does not exist or a close too early is one refusal.</summary>
    [Fact]
    public async Task Bulk_AnEmptyListIsRefusedAndNoListMeansEveryOwnerOfTheMonth_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var first = await OwnerWithAStayAsync(world);
        var second = await OwnerWithAStayAsync(world);

        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 10, OwnerBulkAction.Approve, []));
        Assert.Equal(OwnerAccountingErrors.BulkOwnersEmpty, empty.Code);
        var emptyRun = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.RunBulkAsync(world.OrgId, 2026, 10, OwnerBulkAction.Approve, [], UserId));
        Assert.Equal(OwnerAccountingErrors.BulkOwnersEmpty, emptyRun.Code);
        Assert.Equal(0, world.Db.OwnerStatementVersions.Count());

        var everyone = await world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 10, OwnerBulkAction.Post, null);
        Assert.Equal(new[] { first, second }.Order(), everyone.Order());
        var listed = await world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 10, OwnerBulkAction.Post, [second, second, first]);
        Assert.Equal([second, first], listed);
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 10, OwnerBulkAction.Post, Enumerable.Range(0, OwnerBulk.MaxListedOwners + 1).Select(_ => Guid.NewGuid()).ToList()));
        Assert.Equal(OwnerAccountingErrors.BulkTooManyOwners, tooMany.Code);

        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.RunBulkAsync(world.OrgId, 2026, 10, (OwnerBulkAction)99, [first], UserId));
        Assert.Equal(OwnerAccountingErrors.BulkActionInvalid, unknown.Code);
        var invalidMonth = await Assert.ThrowsAsync<DomainRuleException>(() => world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 13, OwnerBulkAction.Post, null));
        Assert.Equal(OwnerAccountingErrors.PeriodInvalid, invalidMonth.Code);

        // The close of a month that is not over is refused once, for every owner (never one refusal each).
        var early = await Assert.ThrowsAsync<DomainConflictException>(() => world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 11, OwnerBulkAction.Close, [first]));
        Assert.Equal(OwnerAccountingErrors.PeriodNotEnded, early.Code);
    }

    /// <summary>Data (tenant isolation): an owner of another org is the same row as one that does not exist, and nothing is posted or generated for it.</summary>
    [Fact]
    public async Task Bulk_AnOwnerOfAnotherOrgIsNeverServed_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var mine = await OwnerWithAStayAsync(world);
        var stranger = await OwnerMonthEndWorld.CreateAsync(sameDatabaseAs: world);
        var theirs = await OwnerWithAStayAsync(stranger);
        var nobody = Guid.NewGuid();

        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate })
        {
            var rows = await world.Service.RunBulkAsync(world.OrgId, 2026, 10, action, [theirs, mine, nobody], UserId);
            Assert.Equal(OwnerFiscalErrors.OwnerNotFound, rows[0].Code);
            Assert.Equal(OwnerFiscalErrors.OwnerNotFound, rows[2].Code);
            Assert.Equal([false, true, false], rows.Select(r => r.Ok));
        }

        Assert.Empty(world.Db.OwnerLedgerEntries.Where(e => e.PropertyOwnerId == theirs));
        Assert.Empty(world.Db.OwnerStatementVersions.Where(v => v.PropertyOwnerId == theirs));
        Assert.NotEmpty(world.Db.OwnerLedgerEntries.Where(e => e.PropertyOwnerId == mine));
    }

    /// <summary>Data: the posting in bulk posts the lines of the owners asked for and leaves the others as they are; asked again it posts nothing more.</summary>
    [Fact]
    public async Task BulkPost_OnlyPostsTheOwnersAskedFor_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var first = await OwnerWithAStayAsync(world);
        var second = await OwnerWithAStayAsync(world);

        var one = await world.BulkAsync(OwnerBulkAction.Post, first);
        Assert.Equal([true], one.Select(r => r.Ok && !r.Unchanged));
        Assert.NotEmpty(world.Db.OwnerLedgerEntries.Where(e => e.PropertyOwnerId == first));
        Assert.Empty(world.Db.OwnerLedgerEntries.Where(e => e.PropertyOwnerId == second));

        var both = await world.BulkAsync(OwnerBulkAction.Post, first, second);
        Assert.Equal([true, false], both.Select(r => r.Unchanged));
        Assert.Equal([true, true], both.Select(r => r.Ok));
        var lines = world.Db.OwnerLedgerEntries.Count();
        Assert.Equal(3, world.Db.OwnerLedgerEntries.Count(e => e.PropertyOwnerId == second));

        var third = await world.BulkAsync(OwnerBulkAction.Post, first, second);
        Assert.All(third, r => Assert.True(r.Ok && r.Unchanged));
        Assert.Equal(lines, world.Db.OwnerLedgerEntries.Count());
    }

    /// <summary>
    /// Money: the whole month-end in bulk, and again. The close reconciles each owner to what was paid and never waives a difference (that needs a reason and the finance
    /// permission, owner by owner); every step run twice changes nothing more.
    /// </summary>
    [Fact]
    public async Task Bulk_PostGenerateApprovePublishClose_NeverWaivesAndRunsTwiceWithoutChangingMore_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var settled = await OwnerWithAStayAsync(world);
        var unsettled = await OwnerWithAStayAsync(world);
        var owners = new[] { settled, unsettled };

        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate, OwnerBulkAction.Approve, OwnerBulkAction.Publish })
            Assert.All(await world.BulkAsync(action, owners), r => Assert.True(r.Ok && !r.Unchanged, $"{action}: {r.Code}"));
        foreach (var owner in owners)
        {
            var published = await world.VersionAsync(owner);
            Assert.Equal(OwnerStatementVersionStatus.Published, published.Status);
            Assert.NotNull(published.PublishedSnapshot);
        }

        // 500 of revenue less 75 of channel commission and 100 of mandate commission: 325 are owed. One owner was paid in full, the other nothing.
        var settledProperty = world.Db.PropertyOwnerships.Single(o => o.PropertyOwnerId == settled).PropertyId;
        await world.PaidWireAsync(settled, settledProperty, 325m);
        var closed = await world.BulkAsync(OwnerBulkAction.Close, owners);
        Assert.Equal([true, false], closed.Select(r => r.Ok));
        Assert.Equal(OwnerAccountingErrors.CloseUnreconciled, closed[1].Code);
        Assert.Equal((325m, 0m, 325m), ((decimal)closed[1].Details!["due"]!, (decimal)closed[1].Details!["paid"]!, (decimal)closed[1].Details!["difference"]!));

        var periods = await world.Db.OwnerClosePeriods.AsNoTracking().ToListAsync();
        var done = periods.Single(p => p.PropertyOwnerId == settled);
        Assert.Equal(OwnerClosePeriodStatus.Closed, done.Status);
        Assert.Equal(325m, done.DueAmount);
        Assert.Equal(325m, done.PaidAmount);
        Assert.Null(done.WaiverReason);
        var open = periods.Single(p => p.PropertyOwnerId == unsettled);
        Assert.Equal(OwnerClosePeriodStatus.Open, open.Status);
        Assert.Null(open.WaiverReason);

        // Every step again: nothing is made, posted, published or closed twice; the failure is the same failure.
        var versions = world.Db.OwnerStatementVersions.Count();
        var lines = world.Db.OwnerLedgerEntries.Count();
        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate, OwnerBulkAction.Approve, OwnerBulkAction.Publish })
            Assert.All(await world.BulkAsync(action, owners), r => Assert.True(r.Ok && r.Unchanged, $"{action} again: {r.Code}"));
        var closedAgain = await world.BulkAsync(OwnerBulkAction.Close, owners);
        Assert.Equal([true, false], closedAgain.Select(r => r.Ok));
        Assert.True(closedAgain[0].Unchanged);
        Assert.Equal(OwnerAccountingErrors.PeriodClosed, closedAgain[0].Code);
        Assert.Equal(OwnerAccountingErrors.CloseUnreconciled, closedAgain[1].Code);
        Assert.Equal((versions, lines), (world.Db.OwnerStatementVersions.Count(), world.Db.OwnerLedgerEntries.Count()));
    }

    /// <summary>
    /// Data: a month closes on an approved version too, so the publication in bulk is not done for a closed month by the close alone: the approved version is published (the owner
    /// portal and the accountant read only the published ones) and the row says so; asked again, a month closed on a published version is the same unchanged row.
    /// </summary>
    [Fact]
    public async Task Bulk_PublishOfAMonthClosedOnAnApprovedVersionPublishesIt_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var owner = await OwnerWithAStayAsync(world);
        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate, OwnerBulkAction.Approve })
            Assert.All(await world.BulkAsync(action, owner), r => Assert.True(r.Ok && !r.Unchanged, $"{action}: {r.Code}"));

        // 325 are owed and were paid: the month closes on the approved version, which was never published.
        var property = world.Db.PropertyOwnerships.Single(o => o.PropertyOwnerId == owner).PropertyId;
        await world.PaidWireAsync(owner, property, 325m);
        var closed = Assert.Single(await world.BulkAsync(OwnerBulkAction.Close, owner));
        Assert.True(closed.Ok && !closed.Unchanged, closed.Code);
        var approved = await world.VersionAsync(owner);
        Assert.Equal(OwnerStatementVersionStatus.Approved, approved.Status);
        Assert.Null(approved.PublishedSnapshot);

        // The close did not publish it: the publication is not «done» for the closed month, the version is published in it.
        var published = Assert.Single(await world.BulkAsync(OwnerBulkAction.Publish, owner));
        Assert.True(published.Ok && !published.Unchanged, published.Code);
        var version = await world.VersionAsync(owner);
        Assert.Equal((approved.Id, OwnerStatementVersionStatus.Published), (version.Id, version.Status));
        Assert.NotNull(version.PublishedSnapshot);
        Assert.Equal(OwnerClosePeriodStatus.Closed, (await world.Db.OwnerClosePeriods.AsNoTracking().SingleAsync(p => p.PropertyOwnerId == owner)).Status);

        // Asked again, the month closed on a published version is where the step leads: the same unchanged row, nothing published twice.
        var again = Assert.Single(await world.BulkAsync(OwnerBulkAction.Publish, owner));
        Assert.Equal((true, true, OwnerAccountingErrors.PeriodClosed), (again.Ok, again.Unchanged, again.Code));
    }

    /// <summary>
    /// Data: each owner is its own transaction: one whose step fails for a reason that is not a rule (the PDF of its statement cannot be made) is a failed row, leaves nothing
    /// behind (its version is still approved, not half published) and stops nobody.
    /// </summary>
    [Fact]
    public async Task Bulk_AnOwnerThatFailsLeavesNothingBehindAndStopsNobody_Test()
    {
        var files = new Mock<IOwnerStatementDocuments>();
        var world = await OwnerMonthEndWorld.CreateAsync(files.Object);
        var before = await OwnerWithAStayAsync(world);
        var broken = await OwnerWithAStayAsync(world);
        var after = await OwnerWithAStayAsync(world);
        var owners = new[] { before, broken, after };
        files.Setup(f => f.StorePublishedPdfAsync(It.Is<OwnerStatementVersion>(v => v.PropertyOwnerId == broken), It.IsAny<IReadOnlyList<OwnerLedgerEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("TEST the renderer is down"));
        files.Setup(f => f.StorePublishedPdfAsync(It.Is<OwnerStatementVersion>(v => v.PropertyOwnerId != broken), It.IsAny<IReadOnlyList<OwnerLedgerEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredStatementPdf.None);

        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate, OwnerBulkAction.Approve })
            Assert.All(await world.BulkAsync(action, owners), r => Assert.True(r.Ok));
        var published = await world.BulkAsync(OwnerBulkAction.Publish, owners);

        Assert.Equal([true, false, true], published.Select(r => r.Ok));
        Assert.Equal(OwnerAccountingErrors.BulkFailed, published[1].Code);
        Assert.Equal(OwnerStatementVersionStatus.Published, (await world.VersionAsync(before)).Status);
        var stillApproved = await world.VersionAsync(broken);
        Assert.Equal(OwnerStatementVersionStatus.Approved, stillApproved.Status);
        Assert.Null(stillApproved.PublishedSnapshot);
        Assert.Equal(OwnerStatementVersionStatus.Published, (await world.VersionAsync(after)).Status);
        var run = new OwnerBulkRun(null, OwnerBulkAction.Publish, 2026, 10, OwnerBulkRunStatus.Completed, owners.Length, published);
        Assert.False(run.AllDone);
    }

    /// <summary>A step is told after each owner with the results so far (the progress of a long run).</summary>
    [Fact]
    public async Task Bulk_TellsItsProgressAfterEachOwner_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var owners = new[] { await OwnerWithAStayAsync(world), await OwnerWithAStayAsync(world), await OwnerWithAStayAsync(world) };
        await world.BulkAsync(OwnerBulkAction.Post, owners);

        var seen = new List<int>();
        await world.Service.RunBulkAsync(
            world.OrgId, 2026, 10, OwnerBulkAction.Generate, owners, UserId,
            (done, _) =>
            {
                seen.Add(done.Count);
                return Task.CompletedTask;
            });

        Assert.Equal([1, 2, 3], seen);
    }

    /// <summary>
    /// Data: a step out of its order is a failed row with its own code (nothing to approve or publish before a version, nothing to publish or close before the approval); the
    /// generation leaves a draft that is still the picture of the ledger, but makes a new version as soon as a source changed: never a stale statement.
    /// </summary>
    [Fact]
    public async Task Bulk_StepsOutOfOrderAreFailedRowsAndAStaleDraftIsGeneratedAgain_Test()
    {
        var world = await OwnerMonthEndWorld.CreateAsync();
        var changing = await OwnerWithAStayAsync(world);
        var steady = await OwnerWithAStayAsync(world);
        var owners = new[] { changing, steady };
        await world.BulkAsync(OwnerBulkAction.Post, owners);

        // Nothing to approve or publish before there is a version, nothing to close before it is approved.
        foreach (var action in new[] { OwnerBulkAction.Approve, OwnerBulkAction.Publish })
            Assert.All(await world.BulkAsync(action, owners), r => Assert.Equal((false, OwnerAccountingErrors.VersionNotFound), (r.Ok, r.Code)));
        Assert.All(await world.BulkAsync(OwnerBulkAction.Close, owners), r => Assert.Equal((false, OwnerAccountingErrors.VersionNotApproved), (r.Ok, r.Code)));
        Assert.All(await world.BulkAsync(OwnerBulkAction.Generate, owners), r => Assert.True(r.Ok && !r.Unchanged));
        foreach (var action in new[] { OwnerBulkAction.Publish, OwnerBulkAction.Close })
            Assert.All(await world.BulkAsync(action, owners), r => Assert.Equal((false, OwnerAccountingErrors.VersionNotApproved), (r.Ok, r.Code)));

        // The drafts are still the picture of the ledger: generating again leaves them as they are.
        Assert.All(await world.BulkAsync(OwnerBulkAction.Generate, owners), r => Assert.True(r.Ok && r.Unchanged));
        Assert.All(owners, owner => Assert.Equal(1, world.Db.OwnerStatementVersions.Count(v => v.PropertyOwnerId == owner)));

        // A stay is posted for one owner: its draft is stale, so it gets a new version (the old one superseded) and the other owner's draft is left.
        var house = world.Db.PropertyOwnerships.Single(o => o.PropertyOwnerId == changing).PropertyId;
        await world.StayAsync(house, BookingSource.Direct, 80m, Day(10, 25), Day(10, 27));
        await world.BulkAsync(OwnerBulkAction.Post, changing);
        var regenerated = await world.BulkAsync(OwnerBulkAction.Generate, owners);

        Assert.Equal([true, true], regenerated.Select(r => r.Ok));
        Assert.Equal([false, true], regenerated.Select(r => r.Unchanged));
        var current = await world.VersionAsync(changing);
        Assert.Equal((2, OwnerStatementVersionStatus.Draft), (current.VersionNumber, current.Status));
        Assert.Equal(OwnerStatementVersionStatus.Superseded, world.Db.OwnerStatementVersions.Single(v => v.PropertyOwnerId == changing && v.VersionNumber == 1).Status);
        Assert.Equal(1, world.Db.OwnerStatementVersions.Count(v => v.PropertyOwnerId == steady));
    }

    // ---------------------------------------------------------------- the endpoint

    /// <summary>Permission: the publication in bulk needs the permission of the publication, every other step the one of the management of the statements, as the single operations do.</summary>
    [Fact]
    public async Task BulkEndpoint_AskedStepNeedsItsOwnPermission_Test()
    {
        var accounting = new Mock<IOwnerAccountingService>();
        accounting.Setup(a => a.ResolveBulkOwnersAsync(CallerOrg, 2026, 10, It.IsAny<OwnerBulkAction>(), null, It.IsAny<CancellationToken>())).ReturnsAsync([Guid.NewGuid()]);
        accounting.Setup(a => a.RunBulkAsync(CallerOrg, 2026, 10, It.IsAny<OwnerBulkAction>(), It.IsAny<IReadOnlyList<Guid>>(), CallerUser, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OwnerBulkOwnerResult(Guid.NewGuid(), true)]);
        var jobs = new Mock<IOwnerBulkJobs>(MockBehavior.Strict);

        // A caller that may manage the statements and not publish them.
        var manager = Controller(accounting.Object, granted: CasazenPolicies.AgencyStatementManage);
        Assert.IsType<ForbidResult>((await manager.Bulk(2026, 10, new OwnerBulkRequest(OwnerBulkAction.Publish, null), jobs.Object)).Result);
        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate, OwnerBulkAction.Approve, OwnerBulkAction.Close })
            Assert.IsType<OkObjectResult>((await manager.Bulk(2026, 10, new OwnerBulkRequest(action, null), jobs.Object)).Result);

        // A caller that may publish and not manage.
        var publisher = Controller(accounting.Object, granted: CasazenPolicies.AgencyStatementPublish);
        Assert.IsType<OkObjectResult>((await publisher.Bulk(2026, 10, new OwnerBulkRequest(OwnerBulkAction.Publish, null), jobs.Object)).Result);
        foreach (var action in new[] { OwnerBulkAction.Post, OwnerBulkAction.Generate, OwnerBulkAction.Approve, OwnerBulkAction.Close })
            Assert.IsType<ForbidResult>((await publisher.Bulk(2026, 10, new OwnerBulkRequest(action, null), jobs.Object)).Result);

        // No step named: the refusal of the request, after the permission.
        var none = await Assert.ThrowsAsync<DomainRuleException>(() => manager.Bulk(2026, 10, new OwnerBulkRequest(null, null), jobs.Object));
        Assert.Equal(OwnerAccountingErrors.BulkActionInvalid, none.Code);
        accounting.Verify(a => a.RunBulkAsync(CallerOrg, 2026, 10, OwnerBulkAction.Publish, It.IsAny<IReadOnlyList<Guid>>(), CallerUser, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Up to 20 owners the answer is in the request; for more it is a background job and the answer 202 with its id, nothing run in the request.</summary>
    [Fact]
    public async Task BulkEndpoint_MoreThanTwentyOwnersAreAJob_Test()
    {
        var accounting = new Mock<IOwnerAccountingService>();
        var twenty = Enumerable.Range(0, OwnerBulk.SyncMaxOwners).Select(_ => Guid.NewGuid()).ToList();
        var more = twenty.Append(Guid.NewGuid()).ToList();
        accounting.Setup(a => a.ResolveBulkOwnersAsync(CallerOrg, 2026, 10, OwnerBulkAction.Approve, null, It.IsAny<CancellationToken>())).ReturnsAsync(twenty);
        accounting.Setup(a => a.ResolveBulkOwnersAsync(CallerOrg, 2026, 10, OwnerBulkAction.Generate, null, It.IsAny<CancellationToken>())).ReturnsAsync(more);
        accounting.Setup(a => a.RunBulkAsync(CallerOrg, 2026, 10, OwnerBulkAction.Approve, twenty, CallerUser, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(twenty.Select(o => new OwnerBulkOwnerResult(o, true)).ToList());
        var jobs = new Mock<IOwnerBulkJobs>(MockBehavior.Strict);
        jobs.Setup(j => j.Enqueue(CallerOrg, 2026, 10, OwnerBulkAction.Generate, more, CallerUser)).Returns("42");
        var controller = Controller(accounting.Object, granted: CasazenPolicies.AgencyStatementManage);

        var inRequest = Assert.IsType<OkObjectResult>((await controller.Bulk(2026, 10, new OwnerBulkRequest(OwnerBulkAction.Approve, null), jobs.Object)).Result);
        var answered = Assert.IsType<OwnerBulkRun>(inRequest.Value);
        Assert.Null(answered.JobId);
        Assert.Equal((OwnerBulkRunStatus.Completed, 20, 20, true), (answered.Status, answered.Total, answered.Processed, answered.AllDone));

        var queued = Assert.IsType<AcceptedAtActionResult>((await controller.Bulk(2026, 10, new OwnerBulkRequest(OwnerBulkAction.Generate, null), jobs.Object)).Result);
        var run = Assert.IsType<OwnerBulkRun>(queued.Value);
        Assert.Equal(("42", OwnerBulkRunStatus.Queued, 21, 0, false), (run.JobId, run.Status, run.Total, run.Processed, run.AllDone));
        Assert.Equal(nameof(OwnerAccountingController.BulkRun), queued.ActionName);
        jobs.Verify(j => j.Enqueue(CallerOrg, 2026, 10, OwnerBulkAction.Generate, more, CallerUser), Times.Once);
        accounting.Verify(a => a.RunBulkAsync(CallerOrg, 2026, 10, OwnerBulkAction.Generate, It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<string?>(), It.IsAny<OwnerBulkProgress?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Data: leaving the list out on a month the readiness knows no owner in is an empty run (200, no row, nothing run), never the refusal of an empty list: the list was not sent.
    /// </summary>
    [Fact]
    public async Task BulkEndpoint_NoListOnAMonthWithoutOwnersIsAnEmptyRunNotAnError_Test()
    {
        // The service: a month nothing happened in has no owner to run for.
        var world = await OwnerMonthEndWorld.CreateAsync();
        await OwnerWithAStayAsync(world);
        Assert.Empty(await world.Service.ResolveBulkOwnersAsync(world.OrgId, 2026, 9, OwnerBulkAction.Post, null));

        // The endpoint: that is a run with no owner, not 422 owner_bulk_owners_empty.
        var accounting = new Mock<IOwnerAccountingService>(MockBehavior.Strict);
        accounting.Setup(a => a.ResolveBulkOwnersAsync(CallerOrg, 2026, 9, OwnerBulkAction.Post, null, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Guid>());
        var jobs = new Mock<IOwnerBulkJobs>(MockBehavior.Strict);
        var controller = Controller(accounting.Object, granted: CasazenPolicies.AgencyStatementManage);

        var answered = Assert.IsType<OkObjectResult>((await controller.Bulk(2026, 9, new OwnerBulkRequest(OwnerBulkAction.Post, null), jobs.Object)).Result);

        var run = Assert.IsType<OwnerBulkRun>(answered.Value);
        Assert.Null(run.JobId);
        Assert.Equal((OwnerBulkRunStatus.Completed, 0, 0, 0), (run.Status, run.Total, run.Processed, run.Failed));
        accounting.Verify(a => a.RunBulkAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<OwnerBulkAction>(), It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<string?>(), It.IsAny<OwnerBulkProgress?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- the job and its progress

    /// <summary>
    /// Privacy / data: the progress of a run is read only by the org that queued it (the same «not found» for another org, a job of another kind, an id that is not a job), is kept with
    /// the job as it goes, and says «Completed» and «all done» only when every owner was answered and none failed.
    /// </summary>
    [Fact]
    public async Task BulkJob_ProgressIsReadOnlyByTheOrgThatQueuedIt_Test()
    {
        var storage = new InMemoryStorage();
        var jobs = new HangfireOwnerBulkJobs(new BackgroundJobClient(storage), storage);
        var org = Guid.NewGuid();
        var owners = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToList();
        var id = jobs.Enqueue(org, 2026, 10, OwnerBulkAction.Approve, owners, CallerUser);

        var queued = await jobs.GetAsync(org, id);
        Assert.NotNull(queued);
        Assert.Equal((id, OwnerBulkAction.Approve, 2026, 10, OwnerBulkRunStatus.Queued, 25, 0), (queued.JobId, queued.Action, queued.Year, queued.Month, queued.Status, queued.Total, queued.Processed));
        Assert.Null(await jobs.GetAsync(Guid.NewGuid(), id));
        Assert.Null(await jobs.GetAsync(org, "999999"));
        Assert.Null(await jobs.GetAsync(org, "not-a-job"));
        Assert.Null(await jobs.GetAsync(org, string.Empty));

        // The job runs: the progress it keeps is the one the org reads, while it runs and when it is over.
        var accounting = new Mock<IOwnerAccountingService>();
        var seenWhileRunning = new List<(OwnerBulkRunStatus Status, int Processed)>();
        accounting.Setup(a => a.RunBulkAsync(org, 2026, 10, OwnerBulkAction.Approve, It.IsAny<IReadOnlyList<Guid>>(), CallerUser, It.IsAny<OwnerBulkProgress?>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, int, int, OwnerBulkAction, IReadOnlyList<Guid>, string?, OwnerBulkProgress?, CancellationToken>(async (_, _, _, _, ids, _, progress, ct) =>
            {
                var rows = new List<OwnerBulkOwnerResult>();
                foreach (var owner in ids)
                {
                    rows.Add(owner == ids[3] ? new OwnerBulkOwnerResult(owner, false, OwnerAccountingErrors.VersionFlagged, false, [OwnerReadinessCodes.FxMissing]) : new OwnerBulkOwnerResult(owner, true));
                    await progress!(rows, ct);
                    if (rows.Count is 4 or 10)
                    {
                        var now = await jobs.GetAsync(org, id, ct);
                        seenWhileRunning.Add((now!.Status, now.Processed));
                    }
                }

                return rows;
            });
        var tenant = new Mock<IBackgroundTenantScope>();
        await RunJobAsync(new OwnerBulkCloseJob(accounting.Object, tenant.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnerBulkCloseJob>.Instance), storage, id, org, owners);

        tenant.Verify(t => t.EnterOrg(org), Times.Once);
        Assert.Equal([(OwnerBulkRunStatus.Running, 4), (OwnerBulkRunStatus.Running, 10)], seenWhileRunning);
        var done = await jobs.GetAsync(org, id);
        Assert.NotNull(done);
        Assert.Equal((OwnerBulkRunStatus.Completed, 25, 25, 24, 1), (done.Status, done.Total, done.Processed, done.Succeeded, done.Failed));
        Assert.False(done.AllDone);
        var failure = done.Results.Single(r => !r.Ok);
        Assert.Equal((owners[3], OwnerAccountingErrors.VersionFlagged), (failure.OwnerId, failure.Code));
        Assert.Equal([OwnerReadinessCodes.FxMissing], failure.Issues);
        Assert.Null(await jobs.GetAsync(Guid.NewGuid(), id));
    }

    /// <summary>A run that fails as a whole is a failed job with the owners done before it and a code, never a completed one, and the failure is not swallowed.</summary>
    [Fact]
    public async Task BulkJob_ARunThatFailsAsAWholeIsAFailedRunWithWhatWasDone_Test()
    {
        var storage = new InMemoryStorage();
        var jobs = new HangfireOwnerBulkJobs(new BackgroundJobClient(storage), storage);
        var org = Guid.NewGuid();
        var owners = Enumerable.Range(0, 22).Select(_ => Guid.NewGuid()).ToList();
        var id = jobs.Enqueue(org, 2026, 10, OwnerBulkAction.Generate, owners, CallerUser);
        var accounting = new Mock<IOwnerAccountingService>();
        accounting.Setup(a => a.RunBulkAsync(org, 2026, 10, OwnerBulkAction.Generate, It.IsAny<IReadOnlyList<Guid>>(), CallerUser, It.IsAny<OwnerBulkProgress?>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, int, int, OwnerBulkAction, IReadOnlyList<Guid>, string?, OwnerBulkProgress?, CancellationToken>(async (_, _, _, _, ids, _, progress, ct) =>
            {
                await progress!([new OwnerBulkOwnerResult(ids[0], true), new OwnerBulkOwnerResult(ids[1], true)], ct);
                throw new InvalidOperationException("TEST the database went away");
            });
        var job = new OwnerBulkCloseJob(accounting.Object, Mock.Of<IBackgroundTenantScope>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<OwnerBulkCloseJob>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunJobAsync(job, storage, id, org, owners));

        var run = await jobs.GetAsync(org, id);
        Assert.NotNull(run);
        Assert.Equal((OwnerBulkRunStatus.Failed, OwnerAccountingErrors.BulkFailed, 22, 2, false), (run.Status, run.ErrorCode, run.Total, run.Processed, run.AllDone));
    }

    // ---------------------------------------------------------------- the progress endpoint

    /// <summary>The progress is the one of this org and this month: another month, another org or a run that is gone is the same 404.</summary>
    [Fact]
    public async Task BulkRunEndpoint_AnUnknownRunIsNotFound_Test()
    {
        var jobs = new Mock<IOwnerBulkJobs>();
        var run = new OwnerBulkRun("7", OwnerBulkAction.Close, 2026, 10, OwnerBulkRunStatus.Running, 30, [new OwnerBulkOwnerResult(Guid.NewGuid(), true)]);
        jobs.Setup(j => j.GetAsync(CallerOrg, "7", It.IsAny<CancellationToken>())).ReturnsAsync(run);
        var controller = Controller(Mock.Of<IOwnerAccountingService>(), granted: CasazenPolicies.AgencyStatementView);

        var found = Assert.IsType<OkObjectResult>((await controller.BulkRun(2026, 10, "7", jobs.Object)).Result);
        Assert.Same(run, found.Value);
        foreach (var (month, id) in new[] { (11, "7"), (10, "8") })
        {
            var gone = await Assert.ThrowsAsync<NotFoundException>(() => controller.BulkRun(2026, month, id, jobs.Object));
            Assert.Equal(OwnerAccountingErrors.BulkJobNotFound, gone.Code);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An owner paid in francs with a property and a portal stay in October (500 of gross, 75 of commission from the portal), with a mandate of 20% unless said otherwise.</summary>
    private static async Task<Guid> OwnerWithAStayAsync(OwnerMonthEndWorld world, bool withMandate = true)
    {
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync("CHF", (owner, 100m));
        if (withMandate)
            await world.MandateAsync(owner, 20m);
        await world.StayAsync(property, BookingSource.BookingCom, 500m, Day(10, 10), Day(10, 13), touristTax: 6m, otaCommission: 75m);
        return owner;
    }

    /// <summary>Runs the job of a queued run as Hangfire would: with the context of that job.</summary>
    private static async Task RunJobAsync(OwnerBulkCloseJob job, JobStorage storage, string jobId, Guid org, List<Guid> owners)
    {
        using var connection = storage.GetConnection();
        var data = connection.GetJobData(jobId);
        var context = new PerformContext(storage, connection, new BackgroundJob(jobId, data.Job, data.CreatedAt), new JobCancellationToken(false));
        await job.RunAsync(org, (int)data.Job.Args[1], (int)data.Job.Args[2], (OwnerBulkAction)data.Job.Args[3], owners.ToArray(), CallerUser, context, CancellationToken.None);
    }

    /// <summary>The controller of a caller of the whole org who holds the one policy.</summary>
    private static OwnerAccountingController Controller(IOwnerAccountingService accounting, string granted)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CallerOrg);
        var scope = new Mock<IHostScopeResolver>();
        scope.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), CallerOrg, It.IsAny<CancellationToken>())).ReturnsAsync(new HostScope(CallerOrg));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) => policy == granted ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var identity = new ClaimsIdentity([new Claim("sub", CallerUser), new Claim(ClaimTypes.NameIdentifier, CallerUser)], "test");
        return new OwnerAccountingController(accounting, org.Object, scope.Object, authorization.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }
}
