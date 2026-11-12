using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using PropertyEntity = Casazen.Core.Entities.Property;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US02/T02 (NF-36, NF-78): a manual block and its change are saved together (<see cref="CalendarBlockService"/>) and the changes are read and sent again only by who
/// reaches every property of them (<see cref="CalendarChangeQueries"/>, <see cref="CalendarChangesController"/>). The two proof tests are the data and permission rules of the
/// brief. EF InMemory, the channel manager mocked.
/// </summary>
public class CalendarChangeProofTests
{
    private static readonly Guid OrgId = Guid.Parse("2c4e6a80-1b3d-4f5a-8c7e-9d0b2a4c6e01");

    // 9 October 2026, 10:00 UTC: the 9th in Rome.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 9);

    // --- T02: the block and its change -------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule. A manual block and the process that tells the channel manager about it are saved together (one save): the block, a <c>Queued</c> process of kind
    /// <c>Block</c> with its chunk, and one run enqueued once the save is committed; the removal of the block records its own change. A save that fails leaves neither the block nor
    /// the process. A block of a property that is not on the channel manager records an <c>Applied</c> process and nothing else: no chunk, no run.
    /// </summary>
    [Fact]
    public async Task ManualBlock_CreateRecordsChangeInSameTransaction_Test()
    {
        var h = new Blocks();
        var start = Today.AddDays(10).ToDateTime(TimeOnly.MinValue);
        var end = Today.AddDays(13).ToDateTime(TimeOnly.MinValue);

        var block = await h.Service().CreateAsync(new ManualBlockRequest(h.PropertyId, start, end, CalendarBlockReason.Other, "una nota"));

        var check = h.NewDb();
        Assert.Equal(block.Id, (await check.CalendarBlocks.AsNoTracking().SingleAsync()).Id);
        var process = await check.CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((CalendarChangeKind.Block, CalendarChangeState.Queued, 1, 3), (process.Kind, process.State, process.PropertyCount, process.DayCount));
        var chunk = await check.CalendarChangeChunks.AsNoTracking().SingleAsync();
        Assert.Equal(
            (process.Id, h.PropertyId, Today.AddDays(10), Today.AddDays(12), CalendarChunkState.Pending),
            (chunk.ProcessId, chunk.PropertyId, chunk.FromDate, chunk.ToDate, chunk.State));
        // The summary names the action and the reason, never the note of the host.
        Assert.DoesNotContain("una nota", process.SummaryJson);
        Assert.Contains("\"action\":\"created\"", process.SummaryJson);
        h.VerifyRuns(Times.Once());

        // The removal of the block is a change of its own, and a run.
        await h.Service().DeleteAsync(block.Id);
        var after = h.NewDb();
        Assert.Equal(0, await after.CalendarBlocks.CountAsync());
        Assert.Equal(2, await after.CalendarChangeProcesses.CountAsync(p => p.Kind == CalendarChangeKind.Block));
        Assert.Contains(await after.CalendarChangeProcesses.AsNoTracking().ToListAsync(), p => p.SummaryJson.Contains("\"action\":\"removed\""));
        h.VerifyRuns(Times.Exactly(2));

        // A property that is not on the channel manager: an applied process and nothing else.
        var other = new Blocks();
        other.Targets.Setup(t => t.LookupAsync(other.PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.NotMirrored);
        await other.Service().CreateAsync(new ManualBlockRequest(other.PropertyId, start, end, CalendarBlockReason.Maintenance, null));
        var notSent = await other.NewDb().CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((CalendarChangeState.Applied, 0), (notSent.State, await other.NewDb().CalendarChangeChunks.CountAsync()));
        other.VerifyRuns(Times.Never());

        // A save that fails leaves neither the block nor its change.
        var failing = new Blocks(failingSave: true);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => failing.Service().CreateAsync(new ManualBlockRequest(failing.PropertyId, start, end, CalendarBlockReason.Owner, null)));
        var none = failing.NewDb();
        Assert.Equal(0, await none.CalendarBlocks.CountAsync());
        Assert.Equal(0, await none.CalendarChangeProcesses.CountAsync());
        Assert.Equal(0, await none.CalendarChangeChunks.CountAsync());
        failing.VerifyRuns(Times.Never());
    }

    /// <summary>
    /// PROOF TEST: permission rule. A caller limited to some properties, and a caller of another org, never see or touch a change that is not wholly theirs: the list and the detail
    /// leave out a change of another org, one that touches a property the caller does not reach (alone, or with one it does), one that only names such a property in its summary
    /// (nothing was sent for it) and one that names no property at all; the whole org sees what is its own and never another org's. The retry of such a change is a 404 and sends
    /// nothing, and a retry needs the write right on every property of the change.
    /// </summary>
    [Fact]
    public async Task CalendarChanges_OtherOrgOrUnreachableProcess_Is404_Test()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var own = ProcessOf(world.OrgId, [world.Granted.Id], CalendarChangeState.Failed, CalendarChunkState.Failed);
        var hiddenOnly = ProcessOf(world.OrgId, [world.Hidden.Id], CalendarChangeState.Failed, CalendarChunkState.Failed);
        var mixed = ProcessOf(world.OrgId, [world.Granted.Id, world.Hidden.Id], CalendarChangeState.Failed, CalendarChunkState.Failed);
        var summaryOnly = ProcessOf(world.OrgId, [world.Hidden.Id], CalendarChangeState.Applied, chunkState: null);
        var nameless = new CalendarChangeProcess { OrgId = world.OrgId, State = CalendarChangeState.Applied, SummaryJson = "{}" };
        var foreign = ProcessOf(world.OtherOrgId, [world.OtherOrgProperty.Id], CalendarChangeState.Failed, CalendarChunkState.Failed);
        foreach (var (process, chunks) in new[] { own, hiddenOnly, mixed, summaryOnly, foreign })
        {
            db.CalendarChangeProcesses.Add(process);
            db.CalendarChangeChunks.AddRange(chunks);
        }

        db.CalendarChangeProcesses.Add(nameless);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var queries = new CalendarChangeQueries(db);

        // The collaborator «Solo alcuni»: only what is wholly of its properties.
        var restricted = await queries.ListAsync(world.Restricted, new CalendarChangeListQuery());
        Assert.Equal([own.Process.Id], restricted.Select(v => v.Id));
        foreach (var hidden in new[] { hiddenOnly.Process.Id, mixed.Process.Id, summaryOnly.Process.Id, nameless.Id, foreign.Process.Id })
            Assert.Null(await queries.GetAsync(world.Restricted, hidden));

        var visible = await queries.GetAsync(world.Restricted, own.Process.Id);
        Assert.Equal(
            [(world.Granted.Id, CalendarChangePropertyState.Failed, "octorate_unavailable")],
            visible!.Properties.Select(p => (p.PropertyId, p.State, p.ErrorCode)));

        // The whole org: its own changes, never another org's.
        var orgWide = await queries.ListAsync(world.OrgWide, new CalendarChangeListQuery());
        Assert.Equal(
            new[] { own.Process.Id, hiddenOnly.Process.Id, mixed.Process.Id, summaryOnly.Process.Id, nameless.Id }.Order(),
            orgWide.Select(v => v.Id).Order());
        Assert.Null(await queries.GetAsync(world.OrgWide, foreign.Process.Id));
        Assert.Equal(CalendarChangePropertyState.Skipped, Assert.Single((await queries.GetAsync(world.OrgWide, summaryOnly.Process.Id))!.Properties).State);

        // The other org reads its own and nothing of this one.
        var theirs = await queries.ListAsync(new HostScope(world.OtherOrgId), new CalendarChangeListQuery());
        Assert.Equal([foreign.Process.Id], theirs.Select(v => v.Id));
        Assert.Null(await queries.GetAsync(new HostScope(world.OtherOrgId), own.Process.Id));

        // The state narrows the list, the limit bounds it.
        Assert.Empty(await queries.ListAsync(world.OrgWide, new CalendarChangeListQuery(CalendarChangeState.Sent)));
        Assert.Single(await queries.ListAsync(world.OrgWide, new CalendarChangeListQuery(CalendarChangeState.Applied, 1)));

        // The retry: a change the caller does not reach is a 404 and nothing is sent.
        var runner = new Mock<ICalendarChangeRunner>();
        var asCollaborator = Controller(db, runner, world.Restricted, world, deniedProperty: null);
        foreach (var id in new[] { hiddenOnly.Process.Id, mixed.Process.Id, foreign.Process.Id, Guid.NewGuid() })
        {
            var refused = Assert.IsType<ObjectResult>((await asCollaborator.Retry(id, CancellationToken.None)).Result);
            Assert.Equal(StatusCodes.Status404NotFound, refused.StatusCode);
            Assert.Equal("calendar_change_not_found", ((ProblemDetails)refused.Value!).Extensions["code"]);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await asCollaborator.Get(id, CancellationToken.None)).Result).StatusCode);
        }

        runner.Verify(r => r.RetryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        // A retry needs the write right on every property of the change: the whole org sees the mixed change, and the property it may not write refuses all of it.
        var owner = Controller(db, runner, world.OrgWide, world, deniedProperty: world.Hidden.Id);
        Assert.IsType<ForbidResult>((await owner.Retry(mixed.Process.Id, CancellationToken.None)).Result);
        runner.Verify(r => r.RetryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        // Allowed on every property: the change is sent again, and the answer is the change (202).
        var allowed = Controller(db, runner, world.OrgWide, world, deniedProperty: null);
        var accepted = Assert.IsType<AcceptedResult>((await allowed.Retry(own.Process.Id, CancellationToken.None)).Result);
        Assert.Equal(own.Process.Id, Assert.IsType<CalendarChangeDto>(accepted.Value).Id);
        runner.Verify(r => r.RetryAsync(own.Process.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------------------

    /// <summary>A change of one or several properties with its chunks (none when <paramref name="chunkState"/> is <c>null</c>: the change stayed in CasaZen).</summary>
    private static (CalendarChangeProcess Process, List<CalendarChangeChunk> Chunks) ProcessOf(
        Guid orgId,
        IReadOnlyList<Guid> propertyIds,
        CalendarChangeState state,
        CalendarChunkState? chunkState)
    {
        var process = new CalendarChangeProcess
        {
            OrgId = orgId,
            Kind = propertyIds.Count > 1 ? CalendarChangeKind.Bulk : CalendarChangeKind.DayRules,
            State = state,
            PropertyCount = propertyIds.Count,
            DayCount = 3 * propertyIds.Count,
            CreatedAt = Now.UtcDateTime,
            SummaryJson = CalendarChangeSummary.Build([.. propertyIds.Select(id => new CalendarWindow(id, Today.AddDays(1), Today.AddDays(3)))], null),
        };
        var chunks = chunkState is null
            ? []
            : propertyIds
                .Select(id => new CalendarChangeChunk
                {
                    OrgId = orgId,
                    ProcessId = process.Id,
                    PropertyId = id,
                    FromDate = Today.AddDays(1),
                    ToDate = Today.AddDays(3),
                    State = chunkState.Value,
                    ErrorCode = chunkState == CalendarChunkState.Failed ? "octorate_unavailable" : null,
                })
                .ToList();
        return (process, chunks);
    }

    /// <summary>The controller over the real read of the changes, with the authorization of each property decided by <paramref name="deniedProperty"/> (every other one is allowed).</summary>
    private static CalendarChangesController Controller(
        AppDbContext db,
        Mock<ICalendarChangeRunner> runner,
        HostScope scope,
        HostScopeWorld world,
        Guid? deniedProperty)
    {
        var orgContext = new Mock<IOrgContextResolver>();
        orgContext.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(world.OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), world.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var resources = new Mock<IHostResourceLookup>();
        var properties = new Dictionary<Guid, PropertyEntity> { [world.Granted.Id] = world.Granted, [world.Hidden.Id] = world.Hidden };
        resources
            .Setup(r => r.ForPropertyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => properties.TryGetValue(id, out var property) ? HostResource.ForProperty(property) : null);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync((ClaimsPrincipal _, object? resource, IEnumerable<IAuthorizationRequirement> _) =>
                resource is HostResource { PropertyId: { } id } && id == deniedProperty ? AuthorizationResult.Failed() : AuthorizationResult.Success());
        var provider = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return new CalendarChangesController(
            new CalendarChangeQueries(db),
            runner.Object,
            orgContext.Object,
            scopes.Object,
            resources.Object,
            authorization.Object,
            provider.GetRequiredService<IStringLocalizer<SharedResources>>(),
            NullLogger<CalendarChangesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|host")], "test")),
                    RequestServices = provider,
                },
            },
        };
    }

    private static bool IsRun(Job job) =>
        job.Type == typeof(CalendarChangeProcessJob) && job.Method.Name == nameof(CalendarChangeProcessJob.RunAsync);

    /// <summary>Fails every save, so a unit of work that was staged is shown to leave nothing behind.</summary>
    private sealed class ThrowOnSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("The save fails.");
        }
    }

    /// <summary>One in-memory database of the test, a clean context on it and one that fails its saves: a unit of work that fails is judged by what a clean context finds.</summary>
    private abstract class Database(string name, bool failingSave)
    {
        protected bool FailingSave { get; } = failingSave;

        /// <summary>A context on the database that saves (what is stored is read with it).</summary>
        public AppDbContext NewDb() => Create(failing: false);

        /// <summary>The context the code under test works with: it fails its saves when the test says so.</summary>
        protected AppDbContext Work() => Create(FailingSave);

        private AppDbContext Create(bool failing) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(name)
                    .AddInterceptors(failing ? [new ThrowOnSave()] : [])
                    .Options,
                NullTenantContext.Instance);
    }

    /// <summary>The manual blocks over the real recorder: one property of the org, the channel manager answering that it books there.</summary>
    private sealed class Blocks : Database
    {
        public Blocks(bool failingSave = false)
            : base($"calendar-blocks-{Guid.NewGuid():N}", failingSave)
        {
            var property = HostScopeScenario.NewProperty(OrgId, "auth0|owner", "Casa del Lago");
            using (var seed = NewDb())
            {
                seed.Properties.Add(property);
                seed.SaveChanges();
            }

            Db = Work();
            PropertyId = property.Id;
            Targets.Setup(t => t.LookupAsync(PropertyId, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")));
            Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
        }

        public AppDbContext Db { get; }

        public Guid PropertyId { get; }

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IFeatureFlags> Flags { get; } = new();

        public Mock<IBackgroundJobClient> Jobs { get; } = new();

        public CalendarBlockService Service()
        {
            var recorder = new CalendarChangeRecorder(
                Db,
                Targets.Object,
                Flags.Object,
                Microsoft.Extensions.Options.Options.Create(new OctorateCalendarOptions()),
                new FixedTimeProvider(Now),
                NullLogger<CalendarChangeRecorder>.Instance);
            return new CalendarBlockService(
                Db, Mock.Of<ICheckoutHoldExpiryService>(), NullLogger<CalendarBlockService>.Instance, new FixedTimeProvider(Now), recorder, Jobs.Object);
        }

        public void VerifyRuns(Times times) => Jobs.Verify(j => j.Create(It.Is<Job>(job => IsRun(job)), It.IsAny<IState>()), times);
    }
}
