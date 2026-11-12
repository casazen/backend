using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using PropertyEntity = Casazen.Core.Entities.Property;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E08/F05/US02/T02 and T03 (NF-36, NF-78): a manual block and its change are saved together (<see cref="CalendarBlockService"/>), the changes are read and sent again only by who
/// reaches every property of them (<see cref="CalendarChangeQueries"/>, <see cref="CalendarChangesController"/>), and a change of price and rules is previewed without a trace and
/// applied all or nothing (<see cref="CalendarChangeService"/>). The four proof tests are the data and permission rules of the briefs. EF InMemory, the channel manager mocked.
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

    // --- T03: preview and apply --------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: permission rule. A request that names one property the caller may not write answers 403 for the whole request (preview and apply alike), before anything runs: the
    /// service is never called, so no rule and no process is saved for the others; a property of another org is a 404 <c>property_not_found</c>. And one that the caller does not
    /// reach is not found by the service either: nothing is saved for the property it does reach.
    /// </summary>
    [Fact]
    public async Task CalendarChangeApply_PropertyOutsideReach_RefusesWholeRequest_Test()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var service = new Mock<ICalendarChangeService>();
        var controller = Controller(db, new Mock<ICalendarChangeRunner>(), world.OrgWide, world, deniedProperty: world.Hidden.Id, service.Object);

        Assert.IsType<ForbidResult>((await controller.Apply(Body([world.Granted.Id, world.Hidden.Id]), CancellationToken.None)).Result);
        Assert.IsType<ForbidResult>((await controller.Preview(Body([world.Granted.Id, world.Hidden.Id]), CancellationToken.None)).Result);

        // A property of another org is not there for the caller.
        var foreign = await controller.Apply(Body([world.Granted.Id, world.OtherOrgProperty.Id]), CancellationToken.None);
        var problem = Assert.IsType<ObjectResult>(foreign.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("property_not_found", ((ProblemDetails)problem.Value!).Extensions["code"]);
        service.Verify(s => s.ApplyAsync(It.IsAny<HostScope>(), It.IsAny<CalendarChangeRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.PreviewAsync(It.IsAny<HostScope>(), It.IsAny<CalendarChangeRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // The shape of the request is refused before a property is looked up: no property, too many, a weekday set with no day, a patch with nothing in it.
        foreach (var refused in new[]
                 {
                     Body([]),
                     Body(Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToList()),
                     Body([world.Granted.Id], daysOfWeek: []),
                     Body([world.Granted.Id], withPrice: false),
                 })
        {
            await Assert.ThrowsAsync<DomainRuleException>(() => controller.Apply(refused, CancellationToken.None));
        }

        // And the service itself: for the collaborator the hidden property does not exist, the whole request fails, and the property it may reach gets nothing either.
        var h = new Change(db);
        await Assert.ThrowsAsync<NotFoundException>(
            () => h.Service().ApplyAsync(world.Restricted, Request([world.Granted.Id, world.Hidden.Id], new DayRulePatch(Price: FieldPatch<decimal>.Set(150m))), "auth0|collaborator"));
        Assert.Equal(0, await db.PropertyDayRules.CountAsync());
        Assert.Equal(0, await db.CalendarChangeProcesses.CountAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule. The rules, the process and its chunks are saved together or not at all. A property that fails validation after another one was staged fails the whole
    /// request (the rules of the first are not saved, and no process); a save that fails leaves neither rule nor process; and a change that works saves the rules of every property, one
    /// process of kind <c>Bulk</c> and its chunks in the same save, and enqueues one run after it.
    /// </summary>
    [Fact]
    public async Task CalendarChangeApply_RulesAndProcessAreOneTransaction_Test()
    {
        var h = new Change();
        var first = h.AddProperty("Casa Uno");
        var second = h.AddProperty("Casa Due");
        var night = Today.AddDays(1);
        // The second property has a rule with a minimum stay of five nights: a maximum of three is not valid there, after it was fine for the first.
        h.Db.PropertyDayRules.Add(new PropertyDayRule { OrgId = Change.Org, PropertyId = second, StayDate = night, MinNights = 5 });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var request = Request([first, second], new DayRulePatch(MaxNights: FieldPatch<int>.Set(3)), night, night);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => h.Service().ApplyAsync(Scope(), request, "auth0|host"));

        Assert.Equal("calendar_day_rule_invalid_stay", ex.Code);
        var check = h.NewDb();
        Assert.Equal(1, await check.PropertyDayRules.CountAsync());
        Assert.Equal(0, await check.CalendarChangeProcesses.CountAsync());
        Assert.Equal(0, await check.CalendarChangeChunks.CountAsync());
        h.VerifyRuns(Times.Never());

        // A save that fails after everything was staged: neither the rules nor the process.
        var failing = new Change(failingSave: true);
        var only = failing.AddProperty("Casa Tre");
        await Assert.ThrowsAsync<DbUpdateException>(
            () => failing.Service().ApplyAsync(Scope(), Request([only], new DayRulePatch(Price: FieldPatch<decimal>.Set(150m))), "auth0|host"));
        var none = failing.NewDb();
        Assert.Equal(0, await none.PropertyDayRules.CountAsync());
        Assert.Equal(0, await none.CalendarChangeProcesses.CountAsync());
        failing.VerifyRuns(Times.Never());

        // What works is saved in one go: the rules of both, one Bulk process, its chunks, one run.
        var done = await h.Service().ApplyAsync(Scope(), Request([first, second], new DayRulePatch(Price: FieldPatch<decimal>.Set(150m)), night, night.AddDays(1)), "auth0|host");

        var saved = h.NewDb();
        Assert.Equal((CalendarChangeState.Queued, 4, 0, 0), (done.State, done.ChangedDays, done.UnchangedDays, done.SkippedPastDays));
        Assert.Equal(4, await saved.PropertyDayRules.CountAsync(r => r.Price == 150m));
        var process = await saved.CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((done.ProcessId, CalendarChangeKind.Bulk, "auth0|host", 2), (process.Id, process.Kind, process.RequestedByUserId, process.PropertyCount));
        Assert.Equal(2, await saved.CalendarChangeChunks.CountAsync(c => c.ProcessId == process.Id && c.State == CalendarChunkState.Pending));
        h.VerifyRuns(Times.Once());
    }

    [Fact]
    public async Task CalendarChangePreview_SaysWhatWouldChange_AndChangesNothing()
    {
        var h = new Change();
        var mirrored = h.AddProperty("Casa Collegata");
        var local = h.AddProperty("Casa Solo CasaZen");
        h.Targets.Setup(t => t.LookupAsync(local, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.NotMirrored);
        // A night with the price already, a booking that takes two of the nights, and the days before today of the property.
        h.Db.PropertyDayRules.Add(new PropertyDayRule { OrgId = Change.Org, PropertyId = mirrored, StayDate = Today.AddDays(2), Price = 150m });
        var guest = new Guest { OrgId = Change.Org, FirstName = "Anna", LastName = "Prova", Email = $"{Guid.NewGuid():N}@example.com" };
        h.Db.Guests.Add(guest);
        h.Db.Bookings.Add(new Booking
        {
            OrgId = Change.Org,
            PropertyId = mirrored,
            GuestId = guest.Id,
            CheckInDate = Today.AddDays(3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            CheckOutDate = Today.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var request = Request([mirrored, local], new DayRulePatch(Price: FieldPatch<decimal>.Set(150m)), Today.AddDays(-2), Today.AddDays(5));

        var preview = await h.Service().PreviewAsync(Scope(), request);

        // 8 nights selected: 2 are over, 1 already has the price, 5 change (two of them taken by the booking, counted and not skipped).
        var one = preview.Properties.Single(p => p.PropertyId == mirrored);
        Assert.Equal((8, 5, 1, 2, 2, true), (one.Days, one.ChangedDays, one.UnchangedDays, one.TakenDays, one.SkippedPastDays, one.Mirrored));
        Assert.Equal(1, one.EstimatedQuotaUnits);
        Assert.Equal(5, one.Samples.Count);
        var sample = one.Samples[0];
        Assert.Equal(Today, sample.Date);
        Assert.Equal((10_000L, 15_000L), (sample.Before.PriceCents, sample.After.PriceCents));
        var notSent = preview.Properties.Single(p => p.PropertyId == local);
        Assert.Equal((false, 0), (notSent.Mirrored, notSent.EstimatedQuotaUnits));
        Assert.Equal([new CalendarChangeWarning("property_not_mirrored", local)], preview.Warnings);
        Assert.Equal((16, 11, 1, 2, 4, 1), (preview.Days, preview.ChangedDays, preview.UnchangedDays, preview.TakenDays, preview.SkippedPastDays, preview.EstimatedQuotaUnits));

        // Nothing was staged or saved: no new rule, no process.
        Assert.Equal(1, await h.NewDb().PropertyDayRules.CountAsync());
        Assert.Equal(0, await h.NewDb().CalendarChangeProcesses.CountAsync());
        Assert.Empty(h.Db.ChangeTracker.Entries<CalendarChangeProcess>());
        Assert.DoesNotContain(h.Db.ChangeTracker.Entries<PropertyDayRule>(), entry => entry.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task CalendarChange_AChangeThatChangesNothing_OrIsWhollyInThePast_IsRefusedWithAStableCode_AndSavesNothing()
    {
        var h = new Change();
        var property = h.AddProperty("Casa Uno");
        h.Db.PropertyDayRules.Add(new PropertyDayRule { OrgId = Change.Org, PropertyId = property, StayDate = Today.AddDays(1), Price = 150m });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        var minimum = new DayRulePatch(MinNights: FieldPatch<int>.Set(2));

        var same = await Assert.ThrowsAsync<DomainRuleException>(
            () => h.Service().ApplyAsync(Scope(), Request([property], new DayRulePatch(Price: FieldPatch<decimal>.Set(150m)), Today.AddDays(1), Today.AddDays(1)), null));
        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => h.Service().ApplyAsync(Scope(), Request([property], new DayRulePatch(), Today, Today.AddDays(1)), null));
        var weekdays = await Assert.ThrowsAsync<DomainRuleException>(
            () => h.Service().ApplyAsync(Scope(), Request([property], minimum, weekdays: new HashSet<DayOfWeek>()), null));
        var past = await Assert.ThrowsAsync<DomainRuleException>(
            () => h.Service().ApplyAsync(Scope(), Request([property], minimum, Today.AddDays(-5), Today.AddDays(-1)), null));
        var previewPast = await Assert.ThrowsAsync<DomainRuleException>(
            () => h.Service().PreviewAsync(Scope(), Request([property], minimum, Today.AddDays(-5), Today.AddDays(-1))));

        Assert.Equal(
            ["calendar_day_rule_nothing_to_change", "calendar_day_rule_nothing_to_change", "calendar_day_rule_nothing_to_change", "calendar_day_rule_in_past", "calendar_day_rule_in_past"],
            new[] { same.Code, empty.Code, weekdays.Code, past.Code, previewPast.Code });
        Assert.Equal(1, await h.NewDb().PropertyDayRules.CountAsync());
        Assert.Equal(0, await h.NewDb().CalendarChangeProcesses.CountAsync());
        h.VerifyRuns(Times.Never());
    }

    [Fact]
    public async Task CalendarChange_AChangeToAPropertyThatIsNotOnTheChannelManager_IsAppliedInCasaZen_AndSendsNothing()
    {
        var h = new Change();
        var property = h.AddProperty("Casa Solo CasaZen");
        h.Targets.Setup(t => t.LookupAsync(property, It.IsAny<CancellationToken>())).ReturnsAsync(OctorateTargetLookup.NotMirrored);

        var done = await h.Service().ApplyAsync(Scope(), Request([property], new DayRulePatch(ClosedToArrival: FieldPatch<bool>.Set(true))), null);

        var saved = h.NewDb();
        Assert.Equal((CalendarChangeState.Applied, 2), (done.State, done.ChangedDays));
        Assert.Equal(2, await saved.PropertyDayRules.CountAsync(r => r.ClosedToArrival));
        var process = await saved.CalendarChangeProcesses.AsNoTracking().SingleAsync();
        Assert.Equal((CalendarChangeKind.DayRules, CalendarChangeState.Applied), (process.Kind, process.State));
        Assert.Equal(0, await saved.CalendarChangeChunks.CountAsync());
        h.VerifyRuns(Times.Never());
    }

    [Fact]
    public void TheRequestOfTheBody_CarriesKeepSetAndClear_AndASetWithoutAValueIsNotARequest()
    {
        var body = new CalendarChangeRequestDto
        {
            PropertyIds = [Guid.NewGuid()],
            From = Today,
            To = Today.AddDays(6),
            DaysOfWeek = [DayOfWeek.Friday, DayOfWeek.Saturday],
            Price = new FieldPatchDto<decimal> { Action = FieldPatchKind.Set, Value = 120.5m },
            MaxNights = new FieldPatchDto<int> { Action = FieldPatchKind.Clear },
            ClosedToArrival = new FieldPatchDto<bool> { Action = FieldPatchKind.Set, Value = true },
        };

        Assert.True(body.TryToRequest(out var request));
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, request!.DaysOfWeek);
        Assert.Equal((FieldPatchKind.Set, 120.5m), (request.Patch.Price.Kind, request.Patch.Price.Value));
        Assert.Equal(FieldPatchKind.Clear, request.Patch.MaxNights.Kind);
        Assert.Equal(FieldPatchKind.Keep, request.Patch.MinNights.Kind);
        Assert.True(request.Patch.ClosedToArrival.Value);

        var noValue = new CalendarChangeRequestDto
        {
            PropertyIds = [Guid.NewGuid()],
            From = Today,
            To = Today,
            Price = new FieldPatchDto<decimal> { Action = FieldPatchKind.Set },
        };
        Assert.False(noValue.TryToRequest(out _));
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------------------

    private static HostScope Scope() => new(Change.Org);

    private static CalendarChangeRequest Request(
        IReadOnlyList<Guid> properties,
        DayRulePatch patch,
        DateOnly? from = null,
        DateOnly? to = null,
        IReadOnlySet<DayOfWeek>? weekdays = null)
    {
        return new CalendarChangeRequest(properties, from ?? Today.AddDays(1), to ?? Today.AddDays(2), weekdays, patch);
    }

    /// <summary>The body of a request: a price of 150 to set (unless <paramref name="withPrice"/> says the patch has nothing in it).</summary>
    private static CalendarChangeRequestDto Body(List<Guid> properties, List<DayOfWeek>? daysOfWeek = null, bool withPrice = true) => new()
    {
        PropertyIds = properties,
        From = Today.AddDays(1),
        To = Today.AddDays(2),
        DaysOfWeek = daysOfWeek,
        Price = withPrice ? new FieldPatchDto<decimal> { Action = FieldPatchKind.Set, Value = 150m } : null,
    };

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
        Guid? deniedProperty,
        ICalendarChangeService? changes = null)
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
            changes ?? Mock.Of<ICalendarChangeService>(),
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
            var property = HostScopeScenario.NewProperty(Change.Org, "auth0|owner", "Casa del Lago");
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

    /// <summary>The change service over the real rules of the nights and the real recorder, on one org.</summary>
    private sealed class Change : Database
    {
        public static readonly Guid Org = OrgId;

        public Change(AppDbContext? existing = null, bool failingSave = false)
            : base($"calendar-change-{Guid.NewGuid():N}", failingSave)
        {
            Db = existing ?? Work();
            Flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(true);
            Targets
                .Setup(t => t.LookupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(OctorateTargetLookup.Of(new OctorateBookingTarget("ACC1", "956528")));
        }

        public AppDbContext Db { get; }

        public Mock<IOctorateBookingTargets> Targets { get; } = new();

        public Mock<IFeatureFlags> Flags { get; } = new();

        public Mock<IBackgroundJobClient> Jobs { get; } = new();

        /// <summary>A property of the org, stored through a context that saves (the one under test may be set to fail its saves).</summary>
        public Guid AddProperty(string name)
        {
            var property = HostScopeScenario.NewProperty(Org, "auth0|owner", name);
            using var seed = NewDb();
            seed.Properties.Add(property);
            seed.SaveChanges();
            return property.Id;
        }

        public CalendarChangeService Service()
        {
            var clock = new FixedTimeProvider(Now);
            var configuration = new ConfigurationBuilder().Build();
            var options = Microsoft.Extensions.Options.Options.Create(new OctorateCalendarOptions());
            var rules = new PropertyDayRuleService(Db, Targets.Object, Flags.Object, configuration, NullLogger<PropertyDayRuleService>.Instance, clock);
            var recorder = new CalendarChangeRecorder(Db, Targets.Object, Flags.Object, options, clock, NullLogger<CalendarChangeRecorder>.Instance);
            return new CalendarChangeService(
                Db, rules, recorder, Targets.Object, Flags.Object, options, configuration, NullLogger<CalendarChangeService>.Instance, clock, Jobs.Object);
        }

        public void VerifyRuns(Times times) => Jobs.Verify(j => j.Create(It.Is<Job>(job => IsRun(job)), It.IsAny<IState>()), times);
    }
}
