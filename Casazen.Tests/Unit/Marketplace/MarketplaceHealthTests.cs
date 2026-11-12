using System.Reflection;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F03/US01/T01, NF-57, MKT-04, OCT-14: the health of the active integrations. Proofs: the read and the sync never touch another org's activations (data), a
/// collaborator limited to some properties reads the rows of those only and a row with no property is the whole org's (permission), and the text of the channel manager
/// never reaches the page, the answer or the log (ADR-023-R6, privacy). The rules (the status of a connection, an unrecognised word changing nothing, exactly one «To do» that
/// closes by itself, the rate limit stopping the run and the next run continuing, the quota read once and warning at the percent) are checked with a stub of the client of
/// the channel manager, a stub of the license reconciliation and the real service of the «To do» list: what is proven is what CasaZen asks, stores and answers, not Octorate.
/// </summary>
public class MarketplaceHealthTests
{
    private const string Collaborator = "auth0|collaborator";

    // ─── Proofs ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarketplaceHealth_OtherOrgActivations_NeverReturned_Test()
    {
        var w = await World.CreateAsync();
        var ofA = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001");
        await w.AddAsync(w.PropertyB, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "9001", orgId: w.OrgB);
        await w.AddAsync(w.PropertyB, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active, orgId: w.OrgB);
        w.Connections[World.AccA1] = [Conn("142", "7001", "ENABLED")];
        w.Connections[World.AccB] = [Conn("142", "9001", "DISABLED")];
        var ofB = await w.DescribedAsync(w.OrgB);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // Org A reads its own row and nothing of org B's.
            var health = await service.GetHealthAsync(w.OrgA, w.OrgWide, null);
            Assert.Equal(ofA.Id, Assert.Single(health.Items).ActivationId);

            // It cannot name org B's property: the 404 of a property that does not exist.
            var refused = await Assert.ThrowsAsync<NotFoundException>(() => service.GetHealthAsync(w.OrgA, w.OrgWide, w.PropertyB));
            Assert.Equal("marketplace_property_not_found", refused.Code);

            // The scope of another org is a mistake of the caller, refused before anything is read.
            await Assert.ThrowsAsync<ArgumentException>(() => service.GetHealthAsync(w.OrgA, new HostScope(w.OrgB), null));
        }

        // The sync of org A reads its own accommodation and touches its own rows only.
        var result = await w.SyncAsync(w.OrgA);
        Assert.Equal(1, result.Properties);
        Assert.Equal([World.AccA1], w.Listed);
        Assert.Equal(ofB, await w.DescribedAsync(w.OrgB));
        Assert.Empty(await w.TodosAsync());

        // And the other way round: org B's sync reads org B's accommodation, finds its connection disabled and raises its own «To do»; org A's row stays as the first sync left it.
        var ofAAfter = await w.DescribedAsync(w.OrgA);
        w.Listed.Clear();
        await w.SyncAsync(w.OrgB);
        Assert.Equal([World.AccB], w.Listed);
        Assert.Equal(ofAAfter, await w.DescribedAsync(w.OrgA));
        var raised = await w.TodosAsync();
        Assert.NotEmpty(raised);
        Assert.All(raised, todo => Assert.Equal(w.OrgB, todo.OrgId));

        // The job syncs the orgs that have something to sync, each for itself.
        await using var jobDb = w.NewDb();
        Assert.Equal(2, (await w.Service(jobDb).ListOrgsToSyncAsync()).Count);
    }

    [Fact]
    public async Task MarketplaceHealth_PropertyOutsideScope_Rejected_Test()
    {
        var w = await World.CreateAsync();
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001");
        await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.ToFix, "7002", error: "connection_disabled");
        var wholeOrgRow = await w.AddAsync(null, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active);

        // The collaborator is granted the first property only.
        await w.GrantAsync(Collaborator, w.PropertyA1);
        var scope = new HostScope(w.OrgA, GrantedToUserId: Collaborator);

        await using var db = w.NewDb();
        var service = w.Service(db);

        // Without a property: the rows of the granted property only. The row with no property is the whole org's: «no property named» is never «inside the scope».
        var seen = await service.GetHealthAsync(w.OrgA, scope, null);
        var item = Assert.Single(seen.Items);
        Assert.Equal((w.PropertyA1, "142"), (item.PropertyId, item.Code));

        // The other property: the 404 of a property that does not exist.
        var refused = await Assert.ThrowsAsync<NotFoundException>(() => service.GetHealthAsync(w.OrgA, scope, w.PropertyA2));
        Assert.Equal("marketplace_property_not_found", refused.Code);

        // The whole org reads every row, the one with no property included; asked about one property it reads that property's.
        var everything = await service.GetHealthAsync(w.OrgA, w.OrgWide, null);
        Assert.Equal(3, everything.Items.Count);
        Assert.Contains(everything.Items, row => row.ActivationId == wholeOrgRow.Id && row.PropertyId is null && row.PropertyName is null);
        var one = Assert.Single((await service.GetHealthAsync(w.OrgA, w.OrgWide, w.PropertyA2)).Items);
        Assert.Equal("274", one.Code);

        // Nothing of this went to the channel manager.
        Assert.Empty(w.Client.Invocations);
    }

    [Fact]
    public async Task MarketplaceHealth_TheTextOfTheChannelManager_NeverReachesThePageTheAnswerOrTheLog_Test()
    {
        const string Raw = "RAW-TEXT-OF-THE-CHANNEL jane.doe@example.com login failed";
        var w = await World.CreateAsync();
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001");
        w.Connections[World.AccA1] = [new OctorateConnectionInfo("7001", "142", "DISABLED", null, Raw)];

        await w.SyncAsync(w.OrgA);

        await using var db = w.NewDb();
        var health = await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null, "it");
        var item = Assert.Single(health.Items);
        Assert.Equal("connection_disabled", item.Error!.Code);

        // The row, the answer in both languages, the log of the sync and the «To do» hold the code and the words of CasaZen, never the words of the channel.
        var english = await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null, "en");
        var everything = string.Join(
            "\n",
            JsonSerializer.Serialize(MarketplaceHealthDto.From(health)),
            JsonSerializer.Serialize(MarketplaceHealthDto.From(english)),
            w.Logs.AllOutput,
            string.Join("; ", (await w.RowsAsync(w.OrgA)).Select(Describe)),
            string.Join("; ", (await w.TodosAsync()).Select(Describe)));
        Assert.DoesNotContain("RAW-TEXT", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("jane.doe", everything, StringComparison.Ordinal);

        // A code nobody listed (a row some other code left) has no text of its own: the generic one, in the language asked, and not the code or its key.
        await using (var other = w.NewDb())
        {
            var row = await other.MarketplaceActivations.SingleAsync();
            row.LastErrorCode = "something_new";
            await other.SaveChangesAsync();
        }

        var generic = Assert.Single((await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null, "en")).Items).Error!;
        Assert.Equal("something_new", generic.Code);
        Assert.Equal(new ResxMarketplaceTexts().Get(MarketplaceHealthErrorCodes.GenericMessageKey, "en"), generic.Message);
        Assert.DoesNotContain("Marketplace.Error", generic.Message, StringComparison.Ordinal);
    }

    // ─── The read ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealthAsync_ShowsTheStatusTheLastSyncThePlainErrorAndTheQuotaWarning_AndAsksOctorateNothing()
    {
        var w = await World.CreateAsync();
        w.Configuration = new OctorateConfiguration("SANDBOX", 164, 150, null);
        await w.SyncAsync(w.OrgA);
        w.Client.Invocations.Clear();

        await w.AddPortalAsync("142", "Booking di prova", MarketplacePortalCategory.Ota);
        await w.AddPortalAsync("300", "Strumento di prova", MarketplacePortalCategory.ExternalTool);
        var channel = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.ToFix, "7001", error: "connection_disabled");
        var tool = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);
        var function = await w.AddAsync(
            w.PropertyA1,
            MarketplaceActivationKind.LicensedFunction,
            "chat",
            MarketplaceActivationStatus.ToFix,
            error: "license_module_off",
            changeRequestedAt: w.Clock.Now.AddDays(-1));
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Deactivated, "7004");
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active);

        await using var db = w.NewDb();
        var italian = await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null, "it");

        // One row for each channel, tool and function that is not turned off; a partner and a turned off channel are not there. By property name, then kind.
        Assert.Equal([tool.Id, function.Id, channel.Id], italian.Items.Select(i => i.ActivationId));
        var shown = italian.Items.Single(i => i.ActivationId == channel.Id);
        Assert.Equal(("Casa Beta", "Booking di prova", MarketplaceActivationStatus.ToFix), (shown.PropertyName, shown.Name, shown.Status));
        Assert.Equal(channel.LastSyncedAt, shown.LastSyncedAt);
        Assert.Equal("connection_disabled", shown.Error!.Code);
        Assert.Equal("Strumento di prova", italian.Items.Single(i => i.ActivationId == tool.Id).Name);
        Assert.Null(italian.Items.Single(i => i.ActivationId == tool.Id).Error);

        // The function is named by the resource files and keeps its request.
        var chat = italian.Items.Single(i => i.ActivationId == function.Id);
        Assert.Equal(new ResxMarketplaceTexts().Get("Marketplace.Licensed.chat.Name", "it"), chat.Name);
        Assert.Equal(function.ChangeRequestedAt, chat.ChangeRequestedAt);
        Assert.Equal("license_module_off", chat.Error!.Code);

        // The quota of the last sync is the same on every row: 150 of 164 is past the 80 percent.
        Assert.All(italian.Items, row => Assert.Equal((150, 164, true), (row.Quota!.Used, row.Quota.Limit, row.Quota.Warn)));

        // The messages are in the language asked, and they are not the same words.
        var english = await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null, "en");
        Assert.NotEqual(shown.Error.Message, english.Items.Single(i => i.ActivationId == channel.Id).Error!.Message);

        // A read is the database and the memory: nothing is asked of Octorate.
        Assert.Empty(w.Client.Invocations);
    }

    [Fact]
    public async Task GetHealthAsync_WithTheConnectorOff_AnswersNothing()
    {
        var w = await World.CreateAsync(flag: false);
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001");

        await using var db = w.NewDb();
        Assert.Empty((await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null)).Items);
        Assert.Empty(await w.Service(db).ListOrgsToSyncAsync());
    }

    // ─── The sync: the status of a connection ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_TurnsTheStatusOfAConnectionIntoTheStatusOfTheActivation_AndAnUnrecognisedStatusChangesNothing()
    {
        var w = await World.CreateAsync();
        var enabled = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Pending, "7001");
        var waiting = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7002", verified: true);
        var disabled = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);
        var unknown = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "235", MarketplaceActivationStatus.Active, "7004", verified: true);
        var silent = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "301", MarketplaceActivationStatus.Pending, "7005");
        w.Connections[World.AccA1] =
        [
            Conn("142", "7001", "ENABLED"),
            Conn("274", "7002", " waiting "),
            Conn("300", "7003", "DISABLED"),
            Conn("235", "7004", "SUSPENDED"),
            Conn("301", "7005", null),
        ];
        var unknownBefore = Describe(unknown);
        var silentBefore = Describe(silent);

        var result = await w.SyncAsync(w.OrgA);

        Assert.Equal((1, 3, 2), (result.Properties, result.Updated, result.Unrecognised));
        var rows = (await w.RowsAsync(w.OrgA)).ToDictionary(r => r.Id);

        // ENABLED: Active, proven by Octorate and read now.
        Assert.Equal(MarketplaceActivationStatus.Active, rows[enabled.Id].Status);
        Assert.Equal(w.Clock.Now, rows[enabled.Id].VerifiedAt);
        Assert.Equal(w.Clock.Now, rows[enabled.Id].LastSyncedAt);
        Assert.Null(rows[enabled.Id].LastErrorCode);

        // WAITING (in any case, with spaces): Pending.
        Assert.Equal(MarketplaceActivationStatus.Pending, rows[waiting.Id].Status);

        // DISABLED: To fix with the code of the problem and its moment.
        Assert.Equal((MarketplaceActivationStatus.ToFix, "connection_disabled", w.Clock.Now), (rows[disabled.Id].Status, rows[disabled.Id].LastErrorCode, rows[disabled.Id].LastErrorAt));

        // A word nobody knew, or none: the row is exactly as it was (a healthy connection is never marked broken by it), and the warning says which word.
        Assert.Equal(unknownBefore, Describe(rows[unknown.Id]));
        Assert.Equal(silentBefore, Describe(rows[silent.Id]));
        Assert.Contains(w.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SUSPENDED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncAsync_AConnectionTheListNoLongerHas_IsToFix_ButNotWithinTheGraceOfARowJustWritten()
    {
        var w = await World.CreateAsync();
        var gone = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var recent = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Pending, "7002", age: TimeSpan.FromMinutes(2));
        var freed = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.ToFix, error: "connection_missing");
        var recentBefore = Describe(recent);

        var result = await w.SyncAsync(w.OrgA);
        Assert.Equal(1, result.Updated);
        var rows = (await w.RowsAsync(w.OrgA)).ToDictionary(r => r.Id);

        // Not listed and not new: To fix, and the id is kept so that a connection that comes back is read again.
        Assert.Equal((MarketplaceActivationStatus.ToFix, "connection_missing", "7001"), (rows[gone.Id].Status, rows[gone.Id].LastErrorCode, rows[gone.Id].OctorateConnectionId));

        // The list may lag just after a create: a row written two minutes ago is left for the next run.
        Assert.Equal(recentBefore, Describe(rows[recent.Id]));

        // A row with no connection id has nothing to compare: its status, its problem and its last read stay (its «To do» is raised, as for every row that is to fix).
        Assert.Equal(
            (MarketplaceActivationStatus.ToFix, "connection_missing", null, freed.LastSyncedAt),
            (rows[freed.Id].Status, rows[freed.Id].LastErrorCode, rows[freed.Id].OctorateConnectionId, rows[freed.Id].LastSyncedAt));
        Assert.NotNull(rows[freed.Id].TodoActivityId);

        // The connection comes back (the list was behind): the activation is read again and recovers.
        w.Connections[World.AccA1] = [Conn("142", "7001", "ENABLED")];
        await w.SyncAsync(w.OrgA);
        var back = (await w.RowsAsync(w.OrgA)).Single(r => r.Id == gone.Id);
        Assert.Equal((MarketplaceActivationStatus.Active, null), (back.Status, back.LastErrorCode));
    }

    // ─── The sync: the «To do» items ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_AProblemRaisesExactlyOneTodo_AndItClosesByItselfWhenTheConnectionIsHealthyAgain()
    {
        var w = await World.CreateAsync();
        var channel = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var tool = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);
        w.Connections[World.AccA1] = [Conn("142", "7001", "DISABLED"), Conn("300", "7003", "DISABLED")];

        // The problem becomes one item for each activation: High for a channel, Medium for a tool, the code as the reason and nothing else about it.
        var first = await w.SyncAsync(w.OrgA);
        Assert.Equal(2, first.TodoRaised);
        var todos = await w.TodosAsync();
        Assert.Equal(2, todos.Count);
        var item = Assert.Single(todos, t => t.EntityId == channel.Id);
        Assert.Equal(("IntegrationError", ActivityAction.ResumeWizard, "MarketplaceActivation"), (item.ActivityType, item.Action, item.EntityType));
        Assert.Equal((ActivityUrgency.High, w.PropertyA1, "connection_disabled"), (item.Urgency, item.PropertyId, item.Reason));
        Assert.Equal(ActivityUrgency.Medium, Assert.Single(todos, t => t.EntityId == tool.Id).Urgency);
        Assert.Null(item.CompletedAt);
        var rows = (await w.RowsAsync(w.OrgA)).ToDictionary(r => r.Id);
        Assert.Equal(item.Id, rows[channel.Id].TodoActivityId);

        // The same problem at the next run: no second item.
        var second = await w.SyncAsync(w.OrgA);
        Assert.Equal(0, second.TodoRaised);
        Assert.Equal(2, (await w.TodosAsync()).Count);

        // The channel is healthy again: its item is closed by the system and the activation forgets it. The tool's problem and item stay.
        w.Connections[World.AccA1] = [Conn("142", "7001", "ENABLED"), Conn("300", "7003", "DISABLED")];
        var third = await w.SyncAsync(w.OrgA);
        Assert.Equal(1, third.TodoClosed);
        todos = await w.TodosAsync();
        var closed = Assert.Single(todos, t => t.EntityId == channel.Id);
        Assert.Equal((InternalActivity.StatusCompleted, InternalActivity.ClosedBySystem), (closed.Status, closed.ClosedBy));
        Assert.NotNull(closed.CompletedAt);
        Assert.Null(Assert.Single(todos, t => t.EntityId == tool.Id).CompletedAt);
        rows = (await w.RowsAsync(w.OrgA)).ToDictionary(r => r.Id);
        Assert.Null(rows[channel.Id].TodoActivityId);
        Assert.NotNull(rows[tool.Id].TodoActivityId);

        // A new problem of the channel raises a new item; the one that was closed stays closed.
        w.Connections[World.AccA1] = [Conn("142", "7001", "DISABLED"), Conn("300", "7003", "DISABLED")];
        await w.SyncAsync(w.OrgA);
        todos = await w.TodosAsync();
        Assert.Equal(3, todos.Count);
        Assert.Equal(1, todos.Count(t => t.EntityId == channel.Id && t.CompletedAt is null));

        // The host closed the tool's item by hand while the problem stays: it is not raised again at every run.
        await using (var db = w.NewDb())
            await w.Activities(db).CloseAsync(rows[tool.Id].TodoActivityId!.Value, "auth0|host", CancellationToken.None);

        var fourth = await w.SyncAsync(w.OrgA);
        Assert.Equal(0, fourth.TodoRaised);
        Assert.Equal(3, (await w.TodosAsync()).Count);

        // When the tool recovers the reference is cleared, so its next problem is a new item.
        w.Connections[World.AccA1] = [Conn("142", "7001", "DISABLED"), Conn("300", "7003", "ENABLED")];
        await w.SyncAsync(w.OrgA);
        Assert.Null((await w.RowsAsync(w.OrgA)).Single(r => r.Id == tool.Id).TodoActivityId);
    }

    // ─── The sync: the license, the quota, the properties ─────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_ReconcilesTheLicenseOfThePropertiesThatHaveAFunction_AndRaisesTheTodoOfAFunctionThatWentOff()
    {
        var w = await World.CreateAsync();
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active, verified: true);
        await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7002", verified: true);
        w.Connections[World.AccA1] = [Conn("142", "7001", "ENABLED")];
        w.Connections[World.AccA2] = [Conn("274", "7002", "ENABLED")];

        // The reconciliation of the license is the service of the licensed functions: here it finds the module off, as it does when the license no longer shows it.
        w.OnReconcile = async (_, propertyId) =>
        {
            await using var db = w.NewDb();
            var row = await db.MarketplaceActivations.SingleAsync(a => a.PropertyId == propertyId && a.Kind == MarketplaceActivationKind.LicensedFunction);
            row.Status = MarketplaceActivationStatus.ToFix;
            row.LastErrorCode = "license_module_off";
            await db.SaveChangesAsync();
        };

        var result = await w.SyncAsync(w.OrgA);

        // Once, for the property that has a function: not for the other one.
        Assert.Equal([(w.OrgA, w.PropertyA1)], w.Reconciled);
        Assert.Equal((2, 1), (result.Properties, result.TodoRaised));
        var todo = Assert.Single(await w.TodosAsync());
        Assert.Equal((function.Id, ActivityUrgency.Medium, "license_module_off"), (todo.EntityId, todo.Urgency, todo.Reason));
        Assert.Equal(todo.Id, (await w.RowsAsync(w.OrgA)).Single(r => r.Id == function.Id).TodoActivityId);
    }

    [Fact]
    public async Task SyncAsync_AFailureOfOnePropertyIsLoggedByCodeAndLeavesTheOthers_AndARefusedTokenEndsTheRunOfTheOrg()
    {
        var w = await World.CreateAsync();
        var first = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true, lastSyncedAt: w.Clock.Now.AddDays(-3));
        var second = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7002", verified: true, lastSyncedAt: w.Clock.Now.AddDays(-1));
        w.Connections[World.AccA2] = [Conn("274", "7002", "DISABLED")];

        // The first property does not answer: it is left for the next run, the second is read, and the log has the code and never a message.
        w.ListFailures[World.AccA1] = new OctorateServerException(503, new HttpRequestException("body of the answer: secret-body"));
        var result = await w.SyncAsync(w.OrgA);
        Assert.Equal((1, 1, 1), (result.Failed, result.Properties, result.Updated));
        var rows = (await w.RowsAsync(w.OrgA)).ToDictionary(r => r.Id);
        Assert.Equal(MarketplaceActivationStatus.Active, rows[first.Id].Status);
        Assert.Equal(MarketplaceActivationStatus.ToFix, rows[second.Id].Status);
        Assert.Contains(w.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("octorate_unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain("secret-body", w.Logs.AllOutput, StringComparison.Ordinal);

        // The token of the org is refused: the other properties would fail alike, so the run of the org ends there.
        w.Listed.Clear();
        w.ListFailures[World.AccA1] = OctorateAuthException.Refused(403);
        var refused = await w.SyncAsync(w.OrgA);
        Assert.Equal([World.AccA1], w.Listed);
        Assert.Equal(1, refused.Failed);

        // A property with no confirmed listing has nothing to read: it is skipped, and nothing is asked for it.
        var unlinked = await World.CreateAsync(linkedA2: false);
        await unlinked.AddAsync(unlinked.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7002", verified: true);
        var skipped = await unlinked.SyncAsync(unlinked.OrgA);
        Assert.Equal((1, 0), (skipped.Skipped, skipped.Properties));
        Assert.Empty(unlinked.Listed);
    }

    [Fact]
    public async Task SyncAsync_ARateLimitStopsTheRunCleanly_WhatWasDoneIsKept_AndTheNextRunContinuesWithTheRest()
    {
        var w = await World.CreateAsync();
        var first = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true, lastSyncedAt: w.Clock.Now.AddDays(-3));
        var second = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7002", verified: true, lastSyncedAt: w.Clock.Now.AddDays(-1));
        w.Connections[World.AccA1] = [Conn("142", "7001", "DISABLED")];
        w.ListFailures[World.AccA2] = new OctorateRateLimitException(429);

        // The least recently synced property is read first and saved; the second is rate limited: the run stops there and says so.
        await Assert.ThrowsAsync<OctorateRateLimitException>(() => w.SyncAsync(w.OrgA));
        Assert.Equal([World.AccA1, World.AccA2], w.Listed);
        var rows = (await w.RowsAsync(w.OrgA)).ToDictionary(r => r.Id);
        Assert.Equal(MarketplaceActivationStatus.ToFix, rows[first.Id].Status);
        Assert.Equal(MarketplaceActivationStatus.Active, rows[second.Id].Status);
        Assert.Single(await w.TodosAsync());
        Assert.Contains(w.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

        // The next run starts with the property that was not reached (the first one was synced a moment ago): it continues, it does not start again.
        w.Listed.Clear();
        w.ListFailures.Clear();
        w.Connections[World.AccA2] = [Conn("274", "7002", "ENABLED")];
        w.Clock.Set(w.Clock.Now.AddMinutes(30));
        var next = await w.SyncAsync(w.OrgA);
        Assert.Equal([World.AccA2, World.AccA1], w.Listed);
        Assert.Equal(2, next.Properties);

        // The rate limit of the quota read, which comes first, stops the run before any connection is read.
        w.Listed.Clear();
        w.ConfigurationFailure = new OctorateRateLimitException(429);
        await Assert.ThrowsAsync<OctorateRateLimitException>(() => w.SyncAsync(w.OrgA));
        Assert.Empty(w.Listed);
    }

    [Fact]
    public async Task SyncAsync_ReadsTheQuotaOncePerRun_AndWarnsAtTheConfiguredPercent()
    {
        var w = await World.CreateAsync();
        await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7002", verified: true);
        w.Connections[World.AccA1] = [Conn("142", "7001", "ENABLED")];
        w.Connections[World.AccA2] = [Conn("274", "7002", "ENABLED")];

        // One read of the configuration however many properties there are; 131 of 164 is just under 80 percent, 132 is over it.
        w.Configuration = new OctorateConfiguration("SANDBOX", 164, 131, null);
        await w.SyncAsync(w.OrgA);
        Assert.Equal(1, w.ConfigurationReads);
        Assert.Equal((131, 164, false), await w.QuotaOfFirstRowAsync());

        w.Configuration = new OctorateConfiguration("SANDBOX", 164, 132, null);
        await w.SyncAsync(w.OrgA);
        Assert.Equal(2, w.ConfigurationReads);
        Assert.Equal((132, 164, true), await w.QuotaOfFirstRowAsync());

        // The percent is a setting: at 50 percent 82 of 164 warns and 81 does not.
        w.HealthOptions.QuotaWarnPercent = 50;
        w.Configuration = new OctorateConfiguration("SANDBOX", 164, 81, null);
        await w.SyncAsync(w.OrgA);
        Assert.False((await w.QuotaOfFirstRowAsync()).Warn);
        w.Configuration = new OctorateConfiguration("SANDBOX", 164, 82, null);
        await w.SyncAsync(w.OrgA);
        Assert.True((await w.QuotaOfFirstRowAsync()).Warn);

        // A reading that cannot be had leaves the last one; a configuration that states no quota removes it (nothing is guessed).
        w.ConfigurationFailure = new OctorateServerException(null);
        var failed = await w.SyncAsync(w.OrgA);
        Assert.Equal(1, failed.Failed);
        Assert.Equal(2, failed.Properties);
        Assert.Equal(82, (await w.QuotaOfFirstRowAsync()).Used);
        w.ConfigurationFailure = null;
        w.Configuration = new OctorateConfiguration("SANDBOX", null, null, null);
        await w.SyncAsync(w.OrgA);
        await using var db = w.NewDb();
        Assert.All((await w.Service(db).GetHealthAsync(w.OrgA, w.OrgWide, null)).Items, row => Assert.Null(row.Quota));
    }

    [Fact]
    public async Task SyncAsync_WithTheConnectorOffOrWithoutAConnectedAccount_ReadsAndWritesNothing()
    {
        var off = await World.CreateAsync(flag: false);
        await off.AddAsync(off.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        off.Connections[World.AccA1] = [Conn("142", "7001", "DISABLED")];
        var offBefore = await off.DescribedAsync(off.OrgA);
        Assert.Equal(MarketplaceSyncResult.Empty, await off.SyncAsync(off.OrgA));
        Assert.Empty(off.Client.Invocations);
        Assert.Equal(offBefore, await off.DescribedAsync(off.OrgA));
        Assert.Empty(await off.TodosAsync());

        var disconnected = await World.CreateAsync(connected: false);
        await disconnected.AddAsync(disconnected.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        Assert.Equal(MarketplaceSyncResult.Empty, await disconnected.SyncAsync(disconnected.OrgA));
        Assert.Empty(disconnected.Client.Invocations);
        await using var db = disconnected.NewDb();
        Assert.Empty(await disconnected.Service(db).ListOrgsToSyncAsync());

        // An empty org is a mistake of the caller.
        await Assert.ThrowsAsync<ArgumentException>(() => disconnected.SyncAsync(Guid.Empty));
    }

    // ─── The job ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheJob_GoesOrgByOrg_StopsAtTheFirstRateLimit_AndGoesOnAfterAnyOtherFailure()
    {
        var orgs = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var health = new Mock<IMarketplaceHealthService>();
        health.Setup(h => h.ListOrgsToSyncAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgs);
        health.Setup(h => h.SyncAsync(orgs[0], It.IsAny<CancellationToken>())).ReturnsAsync(new MarketplaceSyncResult(2, 1, 0, 0, 0, 1, 0));
        health.Setup(h => h.SyncAsync(orgs[1], It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("secret detail of a bug"));
        health.Setup(h => h.SyncAsync(orgs[2], It.IsAny<CancellationToken>())).ThrowsAsync(new OctorateRateLimitException(429));
        var logs = new CapturingLogger<MarketplaceConnectionSyncJob>();

        await new MarketplaceConnectionSyncJob(health.Object, Flags(true), logs).ExecuteAsync(CancellationToken.None);

        // The org that failed for another reason did not stop the run; the rate limit did: the fourth org was never asked, and is the first of the next run.
        health.Verify(h => h.SyncAsync(orgs[0], It.IsAny<CancellationToken>()), Times.Once);
        health.Verify(h => h.SyncAsync(orgs[1], It.IsAny<CancellationToken>()), Times.Once);
        health.Verify(h => h.SyncAsync(orgs[2], It.IsAny<CancellationToken>()), Times.Once);
        health.Verify(h => h.SyncAsync(orgs[3], It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain("secret detail", logs.AllOutput, StringComparison.Ordinal);

        // With the connector off the run does nothing, not even the list of the orgs.
        var off = new Mock<IMarketplaceHealthService>(MockBehavior.Strict);
        await new MarketplaceConnectionSyncJob(off.Object, Flags(false), new CapturingLogger<MarketplaceConnectionSyncJob>()).ExecuteAsync(CancellationToken.None);
        off.VerifyNoOtherCalls();
    }

    // ─── The rules, the words, the registration ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ENABLED", "Healthy", null)]
    [InlineData(" enabled ", "Healthy", null)]
    [InlineData("WAITING", "Waiting", null)]
    [InlineData("Waiting", "Waiting", null)]
    [InlineData("DISABLED", "Broken", "connection_disabled")]
    [InlineData("SUSPENDED", "Unrecognised", null)]
    [InlineData("ERROR", "Unrecognised", null)]
    [InlineData("", "Unrecognised", null)]
    [InlineData(null, "Unrecognised", null)]
    public void Classify_KnowsThreeWords_AndNothingElseIsConcluded(string? status, string health, string? code) =>
        Assert.Equal(
            new MarketplaceConnectionVerdict(Enum.Parse<MarketplaceConnectionHealth>(health), code),
            MarketplaceHealthRules.Classify(status));

    [Theory]
    [InlineData(131, 164, 80, false)]
    [InlineData(132, 164, 80, true)]
    [InlineData(164, 164, 80, true)]
    [InlineData(0, 164, 80, false)]
    [InlineData(10, 0, 80, false)]
    public void IsWarning_IsTheUsedShareOfTheLimit(int used, int limit, int percent, bool expected) =>
        Assert.Equal(expected, MarketplaceHealthRules.IsWarning(used, limit, percent));

    [Fact]
    public void TheQuotaWarnPercent_Is80ByDefault_IsReadFromTheSection_AndAnOutOfRangeValueIsTheDefault()
    {
        Assert.Equal(80, new MarketplaceHealthOptions().EffectiveQuotaWarnPercent);
        Assert.Equal("Marketplace:Health", MarketplaceHealthOptions.SectionName);

        var configured = new MarketplaceHealthOptions();
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Marketplace:Health:QuotaWarnPercent"] = "60" })
            .Build()
            .GetSection(MarketplaceHealthOptions.SectionName)
            .Bind(configured);
        Assert.Equal(60, configured.EffectiveQuotaWarnPercent);

        foreach (var wrong in new[] { 0, -5, 101, 1000 })
            Assert.Equal(80, new MarketplaceHealthOptions { QuotaWarnPercent = wrong }.EffectiveQuotaWarnPercent);

        Assert.Equal(1, new MarketplaceHealthOptions { QuotaWarnPercent = 1 }.EffectiveQuotaWarnPercent);
        Assert.Equal(100, new MarketplaceHealthOptions { QuotaWarnPercent = 100 }.EffectiveQuotaWarnPercent);
    }

    [Fact]
    public void EveryCodeOfAProblem_HasAnItalianAndAnEnglishMessage_AndNoneNamesTheChannelManagerBrand()
    {
        var texts = new ResxMarketplaceTexts();
        var keys = MarketplaceHealthErrorCodes.All.Select(MarketplaceHealthErrorCodes.MessageKeyOf).Append(MarketplaceHealthErrorCodes.GenericMessageKey);
        foreach (var key in keys)
        {
            var italian = texts.Get(key, "it");
            var english = texts.Get(key, "en");

            // A key that exists in neither file comes back as itself; an English text that is the Italian one is a text nobody translated.
            Assert.NotEqual(key, italian);
            Assert.NotEqual(key, english);
            Assert.NotEqual(italian, english);
            Assert.DoesNotContain("Octorate", italian + english, StringComparison.OrdinalIgnoreCase);
        }

        // The codes are the ones the other services keep on a row, not a second spelling of them.
        Assert.Equal(MarketplaceHealthErrorCodes.ChannelFailed, new OctoratePartnerFailureException().Code);
        Assert.Equal(MarketplaceHealthErrorCodes.ChannelManagerUnavailable, new OctorateServerException(null).Code);
        Assert.Equal(MarketplaceActivationErrorCodes.ConnectionLost, MarketplaceHealthErrorCodes.ConnectionMissing);
        Assert.Equal(MarketplaceActivationErrorCodes.ConnectionNotEnabled, MarketplaceHealthErrorCodes.ConnectionNotEnabled);
        Assert.Equal(MarketplaceFunctionErrorCodes.LicenseModuleOff, MarketplaceHealthErrorCodes.LicenseModuleOff);
        Assert.Equal(MarketplaceHealthErrorCodes.All.Count, MarketplaceHealthErrorCodes.All.Distinct().Count());
    }

    [Fact]
    public void TheEndpoint_NeedsOtaRead_AndSitsBehindTheConnector()
    {
        var controller = typeof(MarketplaceHealthController);
        Assert.Equal("api/marketplace/health", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(FeatureFlags.OctorateConnector, Assert.Single(controller.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.Equal(CasazenPolicies.OtaRead, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);

        var get = controller.GetMethod(nameof(MarketplaceHealthController.Get))!;
        Assert.Null(get.GetCustomAttributes<HttpMethodAttribute>().Single().Template);
        Assert.Empty(get.GetCustomAttributes<AuthorizeAttribute>());
    }

    [Fact]
    public void Registration_TheServiceAndTheJobAreScoped_AndTheOptionsAreBound()
    {
        var services = new ServiceCollection();
        services.AddCasazenMarketplace(new ConfigurationBuilder().Build());

        var service = Assert.Single(services, d => d.ServiceType == typeof(IMarketplaceHealthService));
        Assert.Equal((typeof(MarketplaceHealthService), ServiceLifetime.Scoped), (service.ImplementationType, service.Lifetime));
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(MarketplaceConnectionSyncJob)).Lifetime);
        Assert.Contains(services, d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(MarketplaceHealthOptions)));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static OctorateConnectionInfo Conn(string portal, string id, string? status) => new(id, portal, status, null, null);

    private static IFeatureFlags Flags(bool on)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(on);
        return flags.Object;
    }

    /// <summary>Every public member of an entity as text, so that a change of any column is found.</summary>
    private static string Describe(object entity) =>
        string.Join(
            "; ",
            entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0 && (property.PropertyType.IsValueType || property.PropertyType == typeof(string)))
                .Select(property => $"{property.Name}={property.GetValue(entity)}"));

    /// <summary>A clock a test can move.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;

        public DateTime Now => _now.UtcDateTime;

        public void Set(DateTime utcNow) => _now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Two orgs, each with a connected account in this environment; org A has two properties (Casa Alfa, Casa Beta) and org B one, each with a confirmed listing; a stub of
    /// the channel manager that lists the connections it is given for each accommodation, and of the reconciliation of the license; the real service of the «To do» list.
    /// </summary>
    private sealed class World
    {
        public const string AccA1 = "585492";
        public const string AccA2 = "585493";
        public const string AccB = "700001";

        public Guid OrgA { get; } = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a91");

        public Guid OrgB { get; } = Guid.Parse("0b7e9a14-5d2c-4f31-8e6a-90c1d2e3f4a5");

        public Guid PropertyA1 { get; private set; }

        public Guid PropertyA2 { get; private set; }

        public Guid PropertyB { get; private set; }

        public HostScope OrgWide => new(OrgA);

        public bool FlagOn { get; set; } = true;

        public DbContextOptions<AppDbContext> Options { get; } =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"marketplace-health-{Guid.NewGuid()}").Options;

        public Mock<IOctorateApiClient> Client { get; } = new();

        public CapturingLogger<MarketplaceHealthService> Logs { get; } = new();

        public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.Zero));

        public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

        public MarketplaceHealthOptions HealthOptions { get; } = new();

        /// <summary>The connections the channel manager lists, by accommodation.</summary>
        public Dictionary<string, OctorateConnectionInfo[]> Connections { get; } = new(StringComparer.Ordinal);

        /// <summary>What the list of an accommodation answers instead of its connections.</summary>
        public Dictionary<string, Exception> ListFailures { get; } = new(StringComparer.Ordinal);

        /// <summary>The accommodations whose connections were listed, in order.</summary>
        public List<string> Listed { get; } = [];

        public OctorateConfiguration Configuration { get; set; } = new("SANDBOX", 164, 10, null);

        public Exception? ConfigurationFailure { get; set; }

        public int ConfigurationReads { get; private set; }

        /// <summary>The org and property of every call of the reconciliation of the license, in order.</summary>
        public List<(Guid OrgId, Guid PropertyId)> Reconciled { get; } = [];

        /// <summary>What the reconciliation of the license does when it runs (it writes the rows in the real service).</summary>
        public Func<Guid, Guid, Task>? OnReconcile { get; set; }

        public static async Task<World> CreateAsync(bool connected = true, bool linkedA2 = true, bool flag = true)
        {
            var world = new World { FlagOn = flag };
            await world.SeedAsync(connected, linkedA2);
            world.WireClient();
            return world;
        }

        public AppDbContext NewDb() => new(Options);

        public InternalActivityService Activities(AppDbContext db) => new(db, NullLogger<InternalActivityService>.Instance, Clock);

        public MarketplaceHealthService Service(AppDbContext db)
        {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            var licenses = new Mock<IMarketplaceLicensedFunctionService>();
            licenses
                .Setup(l => l.ReconcileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid orgId, Guid propertyId, CancellationToken _) =>
                {
                    Reconciled.Add((orgId, propertyId));
                    if (OnReconcile is { } run)
                        await run(orgId, propertyId);
                });
            return new MarketplaceHealthService(
                db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                Activities(db),
                licenses.Object,
                Flags(FlagOn),
                host.Object,
                new ResxMarketplaceTexts(),
                Cache,
                Microsoft.Extensions.Options.Options.Create(HealthOptions),
                Clock,
                Logs);
        }

        public async Task<MarketplaceSyncResult> SyncAsync(Guid orgId)
        {
            await using var db = NewDb();
            return await Service(db).SyncAsync(orgId);
        }

        public async Task GrantAsync(string userId, Guid propertyId)
        {
            await using var db = NewDb();
            db.Set<PropertyMemberAccess>().Add(new PropertyMemberAccess { OrgId = OrgA, UserId = userId, PropertyId = propertyId });
            await db.SaveChangesAsync();
        }

        public async Task AddPortalAsync(string code, string name, MarketplacePortalCategory category)
        {
            await using var db = NewDb();
            db.MarketplacePortals.Add(new MarketplacePortal { PortalCode = code, Name = name, Category = category, Enabled = true, IsListed = true });
            await db.SaveChangesAsync();
        }

        /// <summary>An activation as an earlier step or sync left it: written (and last read) <paramref name="age"/> ago, two days by default.</summary>
        public async Task<MarketplaceActivation> AddAsync(
            Guid? propertyId,
            MarketplaceActivationKind kind,
            string code,
            MarketplaceActivationStatus status,
            string? connectionId = null,
            TimeSpan? age = null,
            string? error = null,
            Guid? orgId = null,
            DateTime? lastSyncedAt = null,
            bool verified = false,
            DateTime? changeRequestedAt = null)
        {
            var at = Clock.Now - (age ?? TimeSpan.FromDays(2));
            var row = new MarketplaceActivation
            {
                OrgId = orgId ?? OrgA,
                PropertyId = propertyId,
                Kind = kind,
                TargetCode = code,
                Status = status,
                OctorateConnectionId = connectionId,
                LastErrorCode = error,
                LastErrorAt = error is null ? null : at,
                LastSyncedAt = lastSyncedAt ?? at,
                VerifiedAt = verified ? at : null,
                ChangeRequestedAt = changeRequestedAt,
                CreatedAt = at,
                UpdatedAt = at,
            };
            await using var db = NewDb();
            db.MarketplaceActivations.Add(row);
            await db.SaveChangesAsync();
            return row;
        }

        public async Task<List<MarketplaceActivation>> RowsAsync(Guid orgId)
        {
            await using var db = NewDb();
            return await db.MarketplaceActivations.AsNoTracking().Where(a => a.OrgId == orgId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        }

        /// <summary>The activations of an org as text, to compare before and after.</summary>
        public async Task<List<string>> DescribedAsync(Guid orgId) => (await RowsAsync(orgId)).Select(Describe).ToList();

        /// <summary>Every «To do» of every org, the oldest first.</summary>
        public async Task<List<InternalActivity>> TodosAsync()
        {
            await using var db = NewDb();
            return await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        }

        /// <summary>The quota of the last sync, as the health read shows it on its first row.</summary>
        public async Task<(int Used, int Limit, bool Warn)> QuotaOfFirstRowAsync()
        {
            await using var db = NewDb();
            var quota = (await Service(db).GetHealthAsync(OrgA, OrgWide, null)).Items.First().Quota!;
            return (quota.Used, quota.Limit, quota.Warn);
        }

        private async Task SeedAsync(bool connected, bool linkedA2)
        {
            var keyA = Guid.NewGuid();
            var keyB = Guid.NewGuid();
            var a1 = NewProperty(OrgA, "Casa Alfa");
            var a2 = NewProperty(OrgA, "Casa Beta");
            var b = NewProperty(OrgB, "Casa Gamma");
            (PropertyA1, PropertyA2, PropertyB) = (a1.Id, a2.Id, b.Id);

            await using var db = NewDb();
            db.OctorateConnections.AddRange(Account(keyA, OrgA, connected), Account(keyB, OrgB, true));
            db.Properties.AddRange(a1, a2, b);
            db.OctorateListingMappings.Add(Listing(OrgA, keyA, a1.Id, AccA1));
            if (linkedA2)
                db.OctorateListingMappings.Add(Listing(OrgA, keyA, a2.Id, AccA2));

            db.OctorateListingMappings.Add(Listing(OrgB, keyB, b.Id, AccB));
            await db.SaveChangesAsync();
        }

        private static OctorateConnection Account(Guid id, Guid orgId, bool connected) =>
            new()
            {
                Id = id,
                OrgId = orgId,
                Environment = OctorateEnvironment.Test,
                Status = connected ? OctorateConnectionStatus.Connected : OctorateConnectionStatus.Disconnected,
            };

        private static OctorateListingMapping Listing(Guid orgId, Guid connectionKey, Guid propertyId, string accommodation) =>
            new()
            {
                OrgId = orgId,
                ConnectionId = connectionKey,
                OctorateAccommodationId = accommodation,
                PropertyId = propertyId,
                Status = OctorateMappingStatus.Confirmed,
                OctorateProductId = "956528",
            };

        private static Property NewProperty(Guid orgId, string name) =>
            new()
            {
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = name,
                Address = "Via Prova 1",
                City = "Como",
                MaxGuests = 4,
            };

        private void WireClient()
        {
            Client
                .Setup(c => c.ListConnectionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string accommodation, CancellationToken _) =>
                {
                    Listed.Add(accommodation);
                    if (ListFailures.TryGetValue(accommodation, out var failure))
                        return Task.FromException<OctorateConnectionInfo[]>(failure);

                    return Task.FromResult(Connections.TryGetValue(accommodation, out var connections) ? connections : []);
                });

            Client
                .Setup(c => c.GetConfigurationAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    ConfigurationReads++;
                    return ConfigurationFailure is { } failure
                        ? Task.FromException<OctorateConfiguration>(failure)
                        : Task.FromResult(Configuration);
                });
        }
    }
}
