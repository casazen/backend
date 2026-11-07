using System.Net;
using System.Reflection;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
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
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F02/US02/T01, NF-56, MKT-03, PO-12, D21: the functions of the channel manager's license for a property. Proofs: a collaborator cannot ask for a
/// module, check the license or read the list of a property outside its scope (permission), an org never reads or changes another org's requests
/// (data), the license is only read and a module that is on is never asked for (CasaZen never writes it). The rules (a request is idempotent, a
/// reconciliation closes a request the license shows, flags a module that went off and leaves alone what the license does not state) are checked with
/// a stub of the client of the channel manager, and the read of the license with the real client over a stub HTTP handler: what is proven is what
/// CasaZen asks, stores and answers, not Octorate.
/// </summary>
public class MarketplaceLicensedFunctionTests
{
    private const string Acc = "585492";
    private const string Collaborator = "auth0|collaborator";
    private const string Host = "auth0|host";
    private static readonly Guid OrgA = Guid.Parse("4f1e2d3c-6b5a-4978-8a1b-2c3d4e5f6a91");

    // ─── Proofs ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarketplaceLicensedFunction_RequestOutsideScope_Rejected_Test()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(off: ["chat"]);
        w.Licenses[World.OtherAccommodation] = License(off: ["chat"]);

        // The collaborator is granted the first property only.
        await w.GrantAsync(Collaborator, w.PropertyId);
        var scope = new HostScope(w.OrgA, GrantedToUserId: Collaborator);
        var before = await w.RowsAsync();

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // To ask, to check and to read the list of a property outside the scope: the 404 of a property that does not exist.
            var steps = new Func<Task>[]
            {
                () => service.RequestAsync(w.OrgA, scope, w.OtherPropertyId, "chat", Collaborator),
                () => service.RefreshAsync(w.OrgA, scope, w.OtherPropertyId),
                () => service.ListAsync(w.OrgA, scope, w.OtherPropertyId),
            };
            foreach (var step in steps)
            {
                var refused = await Assert.ThrowsAsync<NotFoundException>(step);
                Assert.Equal("marketplace_property_not_found", refused.Code);
            }
        }

        // Nothing left for the channel manager and nothing changed in CasaZen.
        Assert.Empty(w.Client.Invocations);
        Assert.Equal(before, await w.RowsAsync());

        // The scope of another org is a mistake of the caller, refused before anything is read.
        await using (var db = w.NewDb())
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => w.Service(db).RequestAsync(w.OrgA, new HostScope(Guid.NewGuid()), w.PropertyId, "chat", Collaborator));
        }

        Assert.Empty(w.Client.Invocations);

        // The same collaborator reaches the property it was given.
        await using (var db = w.NewDb())
        {
            var asked = await w.Service(db).RequestAsync(w.OrgA, scope, w.PropertyId, "chat", Collaborator);
            Assert.Equal(MarketplaceFunctionStatus.Requested, asked.Status);
        }

        Assert.Equal(w.PropertyId, Assert.Single(await w.ActivationsAsync()).PropertyId);
    }

    [Fact]
    public async Task MarketplaceLicensedFunction_AnotherOrg_IsNeverReadOrChanged_Test()
    {
        var w = await World.CreateAsync();
        await w.SeedOtherOrgAsync();
        w.Licenses[World.Accommodation] = License(on: ["bookingEngine"]);
        var ofB = await w.RowsAsync(w.OrgB);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // Org A sees nothing of org B's requests on its own property.
            var list = await service.ListAsync(w.OrgA, w.OrgWide, w.PropertyId);
            Assert.All(list.Functions, function => Assert.Equal(MarketplaceFunctionStatus.NotActive, function.Status));

            // It cannot name org B's property: the 404 of a property that does not exist, on every method, and the other way round.
            var steps = new Func<Task>[]
            {
                () => service.ListAsync(w.OrgA, w.OrgWide, w.PropertyOfB),
                () => service.RefreshAsync(w.OrgA, w.OrgWide, w.PropertyOfB),
                () => service.RequestAsync(w.OrgA, w.OrgWide, w.PropertyOfB, "chat", Host),
                () => service.ReconcileAsync(w.OrgA, w.PropertyOfB),
                () => service.ReconcileAsync(w.OrgB, w.PropertyId),
            };
            foreach (var step in steps)
            {
                var refused = await Assert.ThrowsAsync<NotFoundException>(step);
                Assert.Equal("marketplace_property_not_found", refused.Code);
            }
        }

        Assert.Empty(w.Client.Invocations);

        // Org A's own check reads its own accommodation and touches its own rows only.
        await using (var db = w.NewDb())
            await w.Service(db).ReconcileAsync(w.OrgA, w.PropertyId);

        Assert.Equal([World.Accommodation], w.Reads);
        Assert.Equal(ofB, await w.RowsAsync(w.OrgB));
        var made = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(("bookingEngine", w.OrgA, w.PropertyId), (made.TargetCode, made.OrgId, made.PropertyId));
    }

    // ─── The list: the modules, the plan and the room limit ───────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ShowsTheTwelveModules_WithThePlanAndTheRoomLimit_AndFlagsAPropertyWithNoRoomLicense()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License("FREE", 0, on: ["bookingEngine", "octoSite"], off: ["chat", "invoice"]);
        w.Licenses[World.OtherAccommodation] = License("PRO", 5, on: ["chat"]);

        // Before any read: every module is there and not active, the plan is not known, and nothing is asked of the channel manager.
        MarketplaceFunctionList before;
        await using (var db = w.NewDb())
            before = await w.Service(db).ListAsync(w.OrgA, w.OrgWide, w.PropertyId, "it");

        Assert.Equal(MarketplaceLicensedFunctions.Codes, before.Functions.Select(function => function.Code));
        Assert.All(before.Functions, function => Assert.Equal(MarketplaceFunctionStatus.NotActive, function.Status));
        Assert.Null(before.Tier);
        Assert.Null(before.RoomsLimit);
        Assert.False(before.NoRoomLicense);
        Assert.Null(before.LicenseReadAt);
        Assert.Empty(w.Client.Invocations);

        // After a check: the plan, the room limit, the modules the license shows on, and the flag of a property with no license per room.
        MarketplaceFunctionList after;
        await using (var db = w.NewDb())
            after = await w.Service(db).RefreshAsync(w.OrgA, w.OrgWide, w.PropertyId, "it");

        Assert.Equal(("FREE", 0, true), (after.Tier, after.RoomsLimit, after.NoRoomLicense));
        Assert.Equal<DateTime?>(w.Clock.Now, after.LicenseReadAt);
        Assert.Equal(MarketplaceFunctionStatus.Active, after.Functions.Single(function => function.Code == "bookingEngine").Status);
        Assert.Equal(MarketplaceFunctionStatus.Active, after.Functions.Single(function => function.Code == "octoSite").Status);
        Assert.Equal(MarketplaceFunctionStatus.NotActive, after.Functions.Single(function => function.Code == "chat").Status);

        // The next list is read from CasaZen: the plan is the one of the last read, and the channel manager is not asked again.
        w.Client.Invocations.Clear();
        MarketplaceFunctionList again;
        await using (var db = w.NewDb())
            again = await w.Service(db).ListAsync(w.OrgA, w.OrgWide, w.PropertyId, "it");

        Assert.Empty(w.Client.Invocations);
        Assert.Equal(("FREE", 0, true), (again.Tier, again.RoomsLimit, again.NoRoomLicense));

        // The other property has a license for five rooms: not flagged, and the first property keeps its own plan.
        await using (var db = w.NewDb())
        {
            var other = await w.Service(db).RefreshAsync(w.OrgA, w.OrgWide, w.OtherPropertyId, "it");
            Assert.Equal(("PRO", 5, false), (other.Tier, other.RoomsLimit, other.NoRoomLicense));
        }

        await using (var db = w.NewDb())
            Assert.Equal("FREE", (await w.Service(db).ListAsync(w.OrgA, w.OrgWide, w.PropertyId, "it")).Tier);

        // After a restart the plan is not known again, and the answer says so instead of guessing.
        var restarted = await World.CreateAsync();
        await using (var db = restarted.NewDb())
        {
            var fresh = await restarted.Service(db).ListAsync(restarted.OrgA, restarted.OrgWide, restarted.PropertyId);
            Assert.Null(fresh.LicenseReadAt);
            Assert.False(fresh.NoRoomLicense);
        }
    }

    [Fact]
    public async Task ListAsync_NamesAndGuidance_AreInTheLanguageAsked_AndNameTheFunctionAndTheProperty()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(on: ["bookingEngine"], off: ["chat"]);
        var texts = new ResxMarketplaceTexts();

        MarketplaceFunctionList italian;
        MarketplaceFunctionList english;
        await using (var db = w.NewDb())
        {
            await w.Service(db).RefreshAsync(w.OrgA, w.OrgWide, w.PropertyId, "it");
            italian = await w.Service(db).ListAsync(w.OrgA, w.OrgWide, w.PropertyId, "it");
            english = await w.Service(db).ListAsync(w.OrgA, w.OrgWide, w.PropertyId, "en-GB");
        }

        var chatIt = italian.Functions.Single(function => function.Code == "chat");
        var chatEn = english.Functions.Single(function => function.Code == "chat");
        Assert.Equal(texts.Get("Marketplace.Licensed.chat.Name", "it"), chatIt.Name);
        Assert.Equal(texts.Get("Marketplace.Licensed.chat.Name", "en"), chatEn.Name);
        Assert.NotEqual(chatIt.Name, chatEn.Name);

        // What to ask the channel manager: the function and the property, in the language asked, and no brand.
        Assert.Contains(chatIt.Name, chatIt.RequestGuidance, StringComparison.Ordinal);
        Assert.Contains(w.PropertyName, chatIt.RequestGuidance, StringComparison.Ordinal);
        Assert.Contains(chatEn.Name, chatEn.RequestGuidance, StringComparison.Ordinal);
        Assert.Contains(w.PropertyName, chatEn.RequestGuidance, StringComparison.Ordinal);
        Assert.NotEqual(chatIt.RequestGuidance, chatEn.RequestGuidance);
        Assert.DoesNotContain("octorate", chatIt.RequestGuidance + chatEn.RequestGuidance, StringComparison.OrdinalIgnoreCase);

        // A function that is already on has nothing to ask.
        Assert.Null(italian.Functions.Single(function => function.Code == "bookingEngine").RequestGuidance);
    }

    // ─── The request ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestAsync_RecordsARequestDeclaredByTheHost_AndAskingAgainReturnsTheSameOne_WithoutEverWritingTheLicense()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(off: ["chat"]);

        MarketplaceFunctionEntry first;
        await using (var db = w.NewDb())
            first = await w.Service(db).RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host, "en");

        Assert.Equal(MarketplaceFunctionStatus.Requested, first.Status);
        Assert.True(first.IsDeclared);
        Assert.Equal<DateTime?>(w.Clock.Now, first.RequestedAt);
        Assert.Null(first.VerifiedAt);
        Assert.NotNull(first.ActivationId);
        Assert.Contains(w.PropertyName, first.RequestGuidance, StringComparison.Ordinal);

        var row = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(
            (MarketplaceActivationKind.LicensedFunction, "chat", w.PropertyId, w.OrgA, MarketplaceActivationStatus.Requested),
            (row.Kind, row.TargetCode, row.PropertyId, row.OrgId, row.Status));
        Assert.True(row.IsDeclared);
        Assert.Equal<DateTime?>(w.Clock.Now, row.DeclaredAt);

        // Asking again, a day later, returns the same request with its first date, and makes no second row.
        w.Clock.Set(new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.Zero));
        MarketplaceFunctionEntry again;
        await using (var db = w.NewDb())
            again = await w.Service(db).RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host, "it");

        Assert.Equal(first.ActivationId, again.ActivationId);
        Assert.Equal(first.RequestedAt, again.RequestedAt);
        Assert.Equal(MarketplaceFunctionStatus.Requested, again.Status);
        Assert.Single(await w.ActivationsAsync());

        // CasaZen never writes the license: the only thing it asked of the channel manager, twice, was to read it.
        Assert.Equal(2, w.Client.Invocations.Count);
        Assert.All(w.Client.Invocations, call => Assert.Equal(nameof(IOctorateApiClient.GetAccommodationLicenseAsync), call.Method.Name));

        // The log says what was asked, for which property and by whom: ids and the code of the module, never the name of the property.
        Assert.Contains(w.PropertyId.ToString(), w.Logs.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(w.PropertyName, w.Logs.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestAsync_AModuleThatIsOn_OrAnUnknownCode_IsRefusedWithTheStableCodes()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(on: ["chat"], off: ["invoice"]);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // A code that is not one of the twelve, however it is written: 404, before anything is read.
            foreach (var code in new[] { "teleport", "Chat", "", "  " })
            {
                var unknown = await Assert.ThrowsAsync<NotFoundException>(() => service.RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, code, Host));
                Assert.Equal("marketplace_function_unknown", unknown.Code);
            }

            Assert.Empty(w.Client.Invocations);

            // A module the license shows on: 409, and nothing to ask.
            var on = await Assert.ThrowsAsync<DomainConflictException>(() => service.RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host));
            Assert.Equal("marketplace_function_already_active", on.Code);
        }

        // What the license showed is recorded all the same: the card of that module says Active, verified, with no request behind it.
        var chat = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(("chat", MarketplaceActivationStatus.Active, false), (chat.TargetCode, chat.Status, chat.IsDeclared));
        Assert.Equal<DateTime?>(w.Clock.Now, chat.VerifiedAt);
        Assert.Null(chat.RequestedAt);

        // A module that is off is asked for (the code is trimmed, and only an exact code is a code).
        await using (var db = w.NewDb())
        {
            var asked = await w.Service(db).RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, " invoice ", Host);
            Assert.Equal(("invoice", MarketplaceFunctionStatus.Requested), (asked.Code, asked.Status));
        }
    }

    [Fact]
    public async Task RequestAsync_AFunctionToFix_IsAskedForAgain()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(off: ["chat"]);
        var gone = await w.AddRowAsync(
            "chat", MarketplaceActivationStatus.ToFix, verified: true, changeRequestedAt: w.Clock.Now.AddDays(-1), error: MarketplaceFunctionErrorCodes.LicenseModuleOff);

        MarketplaceFunctionEntry asked;
        await using (var db = w.NewDb())
            asked = await w.Service(db).RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host);

        // The same row, asked for again: Requested and declared, with the new date, and the old problem and the old change request are closed.
        Assert.Equal(gone.Id, asked.ActivationId);
        var row = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(MarketplaceActivationStatus.Requested, row.Status);
        Assert.True(row.IsDeclared);
        Assert.Equal<DateTime?>(w.Clock.Now, row.RequestedAt);
        Assert.Null(row.VerifiedAt);
        Assert.Null(row.LastErrorCode);
        Assert.Null(row.ChangeRequestedAt);
    }

    [Fact]
    public async Task Methods_NeedTheConnectorAndALinkedProperty_AndNothingIsReadOtherwise()
    {
        // The connector is off, the org has no connected account, or the property has no confirmed listing (a proposed one is not one).
        var off = await World.CreateAsync(flag: false);
        var disconnected = await World.CreateAsync(connected: false);
        var unlinked = await World.CreateAsync(linked: false);
        var proposed = await World.CreateAsync(mappingStatus: OctorateMappingStatus.Proposed);

        foreach (var (world, code) in new[]
                 {
                     (off, "marketplace_octorate_not_connected"),
                     (disconnected, "marketplace_octorate_not_connected"),
                     (unlinked, "marketplace_property_not_linked"),
                     (proposed, "marketplace_property_not_linked"),
                 })
        {
            await using var db = world.NewDb();
            var service = world.Service(db);
            var steps = new Func<Task>[]
            {
                () => service.ListAsync(world.OrgA, world.OrgWide, world.PropertyId),
                () => service.RefreshAsync(world.OrgA, world.OrgWide, world.PropertyId),
                () => service.RequestAsync(world.OrgA, world.OrgWide, world.PropertyId, "chat", Host),
                () => service.ReconcileAsync(world.OrgA, world.PropertyId),
            };
            foreach (var step in steps)
            {
                var refused = await Assert.ThrowsAnyAsync<DomainException>(step);
                Assert.Equal(code, refused.Code);
                Assert.IsType(code == "marketplace_property_not_linked" ? typeof(DomainRuleException) : typeof(DomainConflictException), refused);
            }

            // Nothing was read, nothing was written.
            Assert.Empty(world.Client.Invocations);
            Assert.Empty(await world.ActivationsAsync());
        }
    }

    [Fact]
    public async Task AFailedRead_RecordsNothing_AndLeavesTheRowsAsTheyWere()
    {
        var w = await World.CreateAsync();
        var waiting = await w.AddRowAsync("invoice", MarketplaceActivationStatus.Requested, declared: true);
        var before = await w.RowsAsync();
        w.ReadFailure = new OctorateServerException(null);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // The failure is the typed error of the client, never a request recorded on a guess, never a list that looks read.
            await Assert.ThrowsAsync<OctorateServerException>(() => service.RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host));
            await Assert.ThrowsAsync<OctorateServerException>(() => service.RefreshAsync(w.OrgA, w.OrgWide, w.PropertyId));
            await Assert.ThrowsAsync<OctorateServerException>(() => service.ReconcileAsync(w.OrgA, w.PropertyId));
        }

        Assert.Equal(before, await w.RowsAsync());
        Assert.Equal(waiting.Id, Assert.Single(await w.ActivationsAsync()).Id);
        await using (var db = w.NewDb())
            Assert.Null((await w.Service(db).ListAsync(w.OrgA, w.OrgWide, w.PropertyId)).LicenseReadAt);
    }

    // ─── The reconciliation ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_ClosesARequestTheLicenseShows_CreatesTheRowOfAModuleThatIsOn_AndFlagsAModuleThatWentOff()
    {
        var w = await World.CreateAsync();
        var asked = w.Clock.Now.AddDays(-3);
        var changeAsked = w.Clock.Now.AddDays(-1);
        await w.AddRowAsync("chat", MarketplaceActivationStatus.Requested, declared: true);
        await w.AddRowAsync("invoice", MarketplaceActivationStatus.Requested, declared: true);
        await w.AddRowAsync("smartOffer", MarketplaceActivationStatus.Active, verified: true, changeRequestedAt: changeAsked);
        await w.AddRowAsync("rms", MarketplaceActivationStatus.Active, verified: true, changeRequestedAt: changeAsked);
        w.Licenses[World.Accommodation] = License(on: ["chat", "bookingEngine", "rms"], off: ["invoice", "smartOffer"]);

        await using (var db = w.NewDb())
            await w.Service(db).ReconcileAsync(w.OrgA, w.PropertyId);

        var rows = (await w.ActivationsAsync()).ToDictionary(row => row.TargetCode);
        Assert.Equal(5, rows.Count);

        // The request the license shows: Active, proven by the license and no longer declared; the date of the request is kept.
        var chat = rows["chat"];
        Assert.Equal((MarketplaceActivationStatus.Active, false), (chat.Status, chat.IsDeclared));
        Assert.Equal<DateTime?>(w.Clock.Now, chat.VerifiedAt);
        Assert.Null(chat.DeclaredAt);
        Assert.Equal<DateTime?>(asked, chat.RequestedAt);
        Assert.Null(chat.LastErrorCode);

        // A request the license does not show waits as it is.
        var invoice = rows["invoice"];
        Assert.Equal((MarketplaceActivationStatus.Requested, true), (invoice.Status, invoice.IsDeclared));
        Assert.Null(invoice.VerifiedAt);

        // A module that went off: to fix, with the code; the change the host asked for is answered, so it is closed.
        var smartOffer = rows["smartOffer"];
        Assert.Equal((MarketplaceActivationStatus.ToFix, "license_module_off"), (smartOffer.Status, smartOffer.LastErrorCode));
        Assert.Equal<DateTime?>(w.Clock.Now, smartOffer.LastErrorAt);
        Assert.Null(smartOffer.ChangeRequestedAt);

        // A module that is still on keeps its open change request: the license has not answered it.
        var rms = rows["rms"];
        Assert.Equal(MarketplaceActivationStatus.Active, rms.Status);
        Assert.Equal<DateTime?>(changeAsked, rms.ChangeRequestedAt);

        // A module that is on with no row: a verified Active row of this property, with no request behind it.
        var created = rows["bookingEngine"];
        Assert.Equal((MarketplaceActivationStatus.Active, false, w.PropertyId, w.OrgA), (created.Status, created.IsDeclared, created.PropertyId, created.OrgId));
        Assert.Equal<DateTime?>(w.Clock.Now, created.VerifiedAt);
        Assert.Null(created.RequestedAt);
        Assert.Equal(MarketplaceActivationKind.LicensedFunction, created.Kind);

        // The check reads, and writes nothing to the channel manager.
        Assert.Single(w.Client.Invocations);
    }

    [Fact]
    public async Task ReconcileAsync_AModuleTheLicenseDoesNotState_ChangesNothingForIt()
    {
        var w = await World.CreateAsync();
        await w.AddRowAsync("chat", MarketplaceActivationStatus.Active, verified: true);
        await w.AddRowAsync("invoice", MarketplaceActivationStatus.ToFix, verified: true, error: MarketplaceFunctionErrorCodes.LicenseModuleOff);
        await w.AddRowAsync("rms", MarketplaceActivationStatus.Requested, declared: true);
        var before = (await w.RowsAsync()).ToList();

        // The card states one module only; an absent flag is not an off flag.
        w.Licenses[World.Accommodation] = License(on: ["bookingEngine"]);
        await using (var db = w.NewDb())
            await w.Service(db).ReconcileAsync(w.OrgA, w.PropertyId);

        var after = await w.RowsAsync();
        Assert.All(before, line => Assert.Contains(line, after));
        var added = Assert.Single(await w.ActivationsAsync(), row => row.TargetCode == "bookingEngine");
        Assert.Equal(MarketplaceActivationStatus.Active, added.Status);
        Assert.Equal(4, after.Count);
    }

    [Fact]
    public async Task ReconcileAsync_AFunctionToFix_IsActiveAgainWhenTheLicenseShowsItOn()
    {
        var w = await World.CreateAsync();
        await w.AddRowAsync("smartOffer", MarketplaceActivationStatus.ToFix, verified: true, error: MarketplaceFunctionErrorCodes.LicenseModuleOff);
        w.Licenses[World.Accommodation] = License(on: ["smartOffer"]);

        await using (var db = w.NewDb())
            await w.Service(db).ReconcileAsync(w.OrgA, w.PropertyId);

        var row = Assert.Single(await w.ActivationsAsync());
        Assert.Equal(MarketplaceActivationStatus.Active, row.Status);
        Assert.Null(row.LastErrorCode);
        Assert.Null(row.LastErrorAt);
        Assert.Equal<DateTime?>(w.Clock.Now, row.VerifiedAt);
    }

    [Fact]
    public async Task RefreshAsync_BringsTheRequestsInLineWithTheLicense_AndAnswersTheNewState()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(off: ["chat"]);
        await using (var db = w.NewDb())
            await w.Service(db).RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host);

        // The channel manager switches the module on; the check turns the request into an active function.
        w.Licenses[World.Accommodation] = License(on: ["chat"]);
        MarketplaceFunctionList list;
        await using (var db = w.NewDb())
            list = await w.Service(db).RefreshAsync(w.OrgA, w.OrgWide, w.PropertyId);

        var chat = list.Functions.Single(function => function.Code == "chat");
        Assert.Equal((MarketplaceFunctionStatus.Active, false, null), (chat.Status, chat.IsDeclared, chat.RequestGuidance));
        Assert.Equal<DateTime?>(w.Clock.Now, chat.VerifiedAt);
    }

    // ─── The card of the catalog ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCatalogCardOfAFunction_ShowsTheRequestOfTheProperty_AndTheSummaryOnlyWithinTheScope()
    {
        var w = await World.CreateAsync();
        w.Licenses[World.Accommodation] = License(off: ["chat"]);
        await using (var db = w.NewDb())
            await w.Service(db).RequestAsync(w.OrgA, w.OrgWide, w.PropertyId, "chat", Host);

        await using (var db = w.NewDb())
        {
            var catalog = w.Catalog(db);

            // Asked about the property that has the request: Pending, the property's; about the other one: not active; about none: the summary.
            Assert.Equal((MarketplaceCardStatus.Pending, MarketplaceStatusScope.Property), StateOf(await catalog.GetCatalogAsync(w.OrgA, w.OrgWide, w.PropertyId, "it")));
            Assert.Equal((MarketplaceCardStatus.NotActive, MarketplaceStatusScope.Property), StateOf(await catalog.GetCatalogAsync(w.OrgA, w.OrgWide, w.OtherPropertyId, "it")));
            Assert.Equal((MarketplaceCardStatus.Pending, MarketplaceStatusScope.Org), StateOf(await catalog.GetCatalogAsync(w.OrgA, w.OrgWide, null, "it")));

            // A collaborator who was given the other property only does not see the request of the first one, even in the summary.
            await w.GrantAsync(Collaborator, w.OtherPropertyId);
            var limited = new HostScope(w.OrgA, GrantedToUserId: Collaborator);
            Assert.Equal((MarketplaceCardStatus.NotActive, MarketplaceStatusScope.Org), StateOf(await catalog.GetCatalogAsync(w.OrgA, limited, null, "it")));
        }

        // The license shows the module on: the card of the property is Active.
        w.Licenses[World.Accommodation] = License(on: ["chat"]);
        await using (var db = w.NewDb())
            await w.Service(db).ReconcileAsync(w.OrgA, w.PropertyId);

        await using (var db = w.NewDb())
            Assert.Equal((MarketplaceCardStatus.Active, MarketplaceStatusScope.Property), StateOf(await w.Catalog(db).GetCatalogAsync(w.OrgA, w.OrgWide, w.PropertyId, "it")));
    }

    // ─── The read of the license by the client ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccommodationLicenseAsync_ReadsTheLicenseOfTheWholeCard_WithOneGetAndNothingElse()
    {
        var h = new OctorateConnectionClientHarness();
        h.Answer(
            HttpStatusCode.OK,
            """
            { "id": "585492", "name": "Casa Privata", "vatCode": "IT01234567890",
              "license": { "channelManager": "FREE", "roomsLimit": 0, "expireDay": "2027-01-31",
                "chat": false, "invoice": false, "fiscalPrinter": false, "smartOffer": true, "octoDaily": false, "rms": false,
                "businessIntelligence": false, "bookingEngine": true, "octoSite": true, "webConcierge": true, "metaSearch": false,
                "rateChecker": false, "realPlanning": true } }
            """);

        OctorateLicense license;
        using (h.Scope.Use(OrgA))
            license = await h.Client.GetAccommodationLicenseAsync(Acc, CancellationToken.None);

        // One GET of the whole card (the short one has no license) and nothing sent.
        var request = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{OctorateConnectionClientHarness.BaseAddress}/rest/v1/accommodation/{Acc}", request.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("?fields=all", request.Uri.Query);
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(("FREE", 0, new DateOnly(2027, 1, 31)), (license.Tier, license.RoomsLimit, license.ExpireDay));
        Assert.Equal(MarketplaceLicensedFunctions.Codes.Count, license.Modules.Count);
        Assert.True(license.Modules["bookingEngine"]);
        Assert.False(license.Modules["chat"]);

        // A flag that is not one of the twelve modules is not read, and nothing of the card reaches the log.
        Assert.DoesNotContain("realPlanning", license.Modules.Keys);
        Assert.DoesNotContain("IT01234567890", h.Logs.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Casa Privata", h.Logs.AllOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccommodationLicenseAsync_AnAnswerWithoutALicense_IsNotAnEmptyLicense_AndTheErrorsKeepTheirTypes()
    {
        // Each of these answers is a card CasaZen cannot read a license from: none is an empty license (which would read as «all off»).
        foreach (var body in new[]
                 {
                     """{ "id": "585492", "name": "Casa" }""",
                     """{ "license": [] }""",
                     """{ "license": null }""",
                     """[]""",
                 })
        {
            var h = new OctorateConnectionClientHarness();
            h.Answer(HttpStatusCode.OK, body);
            using (h.Scope.Use(OrgA))
                await Assert.ThrowsAsync<OctorateServerException>(() => h.Client.GetAccommodationLicenseAsync(Acc, CancellationToken.None));
        }

        var missing = new OctorateConnectionClientHarness();
        missing.Answer(HttpStatusCode.NotFound, """{ "type": "ApiResourceNotFound" }""");
        using (missing.Scope.Use(OrgA))
            await Assert.ThrowsAsync<OctorateNotFoundException>(() => missing.Client.GetAccommodationLicenseAsync(Acc, CancellationToken.None));

        var refused = new OctorateConnectionClientHarness();
        refused.Answer(HttpStatusCode.Forbidden, """{ "type": "ApiSecurityException" }""");
        using (refused.Scope.Use(OrgA))
            await Assert.ThrowsAsync<OctorateAuthException>(() => refused.Client.GetAccommodationLicenseAsync(Acc, CancellationToken.None));

        // The wrapper of the v2 and v3 answers is read too, and a bad accommodation id never leaves.
        var wrapped = new OctorateConnectionClientHarness();
        wrapped.Answer(HttpStatusCode.OK, """{ "data": { "license": { "channelManager": "PRO", "roomsLimit": 4, "chat": true } } }""");
        using (wrapped.Scope.Use(OrgA))
        {
            var license = await wrapped.Client.GetAccommodationLicenseAsync(Acc, CancellationToken.None);
            Assert.Equal(("PRO", 4, true), (license.Tier, license.RoomsLimit, license.Modules["chat"]));
            await Assert.ThrowsAsync<ArgumentException>(() => wrapped.Client.GetAccommodationLicenseAsync(" ", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => wrapped.Client.GetAccommodationLicenseAsync(new string('9', 65), CancellationToken.None));
        }

        Assert.Single(wrapped.Requests);
    }

    [Fact]
    public void ReadLicense_ReadsOnlyWhatTheCardStates_AndNeverTakesAnAbsentFlagForOff()
    {
        using var document = JsonDocument.Parse(
            """
            { "license": { "channelManager": " PRO ", "roomsLimit": "3", "chat": "true", "invoice": 0, "rms": 1, "smartOffer": "maybe",
              "bookingEngine": { "enabled": true }, "expireDay": 1893456000000 } }
            """);
        var license = OctorateApiClient.ReadLicense(document.RootElement);

        Assert.NotNull(license);
        Assert.Equal(("PRO", 3, new DateOnly(2030, 1, 1)), (license.Tier, license.RoomsLimit, license.ExpireDay));
        Assert.True(license.Modules["chat"]);
        Assert.False(license.Modules["invoice"]);
        Assert.True(license.Modules["rms"]);

        // A flag that is missing or that CasaZen does not understand is not in the map: it is neither on nor off.
        Assert.False(license.Modules.ContainsKey("smartOffer"));
        Assert.False(license.Modules.ContainsKey("bookingEngine"));
        Assert.False(license.Modules.ContainsKey("metaSearch"));
        Assert.Equal(3, license.Modules.Count);
    }

    [Fact]
    public void ReadLicense_NeverGuessesAPlanARoomLimitOrADate()
    {
        using var document = JsonDocument.Parse(
            $$"""
            { "license": { "channelManager": "{{new string('X', 41)}}", "roomsLimit": -1, "expireDay": 365 } }
            """);
        var license = OctorateApiClient.ReadLicense(document.RootElement);

        Assert.NotNull(license);
        Assert.Null(license.Tier);
        Assert.Null(license.RoomsLimit);
        Assert.Null(license.ExpireDay);
        Assert.Empty(license.Modules);

        // A plan that is not text, a date that is not a date, and a day in ISO form with a time.
        using var other = JsonDocument.Parse("""{ "license": { "channelManager": { "plan": "FREE" }, "expireDay": "soon" } }""");
        var unclear = OctorateApiClient.ReadLicense(other.RootElement);
        Assert.NotNull(unclear);
        Assert.Null(unclear.Tier);
        Assert.Null(unclear.ExpireDay);

        using var moment = JsonDocument.Parse("""{ "license": { "expireDay": "2027-03-15T10:30:00Z" } }""");
        Assert.Equal(new DateOnly(2027, 3, 15), OctorateApiClient.ReadLicense(moment.RootElement)!.ExpireDay);

        // Not an accommodation, or no license object: no license.
        using var notObject = JsonDocument.Parse("""[1, 2]""");
        Assert.Null(OctorateApiClient.ReadLicense(notObject.RootElement));
        using var noLicense = JsonDocument.Parse("""{ "license": "FREE" }""");
        Assert.Null(OctorateApiClient.ReadLicense(noLicense.RootElement));
    }

    [Fact]
    public void TheClient_ReadsTheLicense_AndHasNoMethodThatWritesIt()
    {
        var methods = typeof(IOctorateApiClient)
            .GetMethods()
            .Where(method => method.Name.Contains("License", StringComparison.OrdinalIgnoreCase))
            .Select(method => method.Name)
            .ToList();

        Assert.Equal([nameof(IOctorateApiClient.GetAccommodationLicenseAsync)], methods);

        // The change of an accommodation has no member for the license either.
        Assert.DoesNotContain(
            typeof(OctoratePropertyUpdate).GetProperties(),
            property => property.Name.Contains("License", StringComparison.OrdinalIgnoreCase));
    }

    // ─── The endpoints, the registration and the words ────────────────────────────────────────────────────

    [Fact]
    public void TheEndpoints_NeedOtaRead_TheChecksAndTheRequestsNeedOtaWrite_AndAllSitBehindTheConnector()
    {
        var controller = typeof(MarketplaceFunctionsController);
        Assert.Equal("api/marketplace/functions", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(FeatureFlags.OctorateConnector, Assert.Single(controller.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        Assert.Equal(CasazenPolicies.OtaRead, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);

        string? PolicyOf(string action) => controller.GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().SingleOrDefault()?.Policy;
        string? TemplateOf(string action) => controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>().Single().Template;

        // The read has the policy of the class; the two writes add the policy of the write.
        Assert.Null(PolicyOf(nameof(MarketplaceFunctionsController.List)));
        Assert.Equal(CasazenPolicies.OtaWrite, PolicyOf(nameof(MarketplaceFunctionsController.Refresh)));
        Assert.Equal(CasazenPolicies.OtaWrite, PolicyOf(nameof(MarketplaceFunctionsController.RequestFunction)));
        Assert.Null(TemplateOf(nameof(MarketplaceFunctionsController.List)));
        Assert.Equal("refresh", TemplateOf(nameof(MarketplaceFunctionsController.Refresh)));
        Assert.Equal("{moduleCode}/request", TemplateOf(nameof(MarketplaceFunctionsController.RequestFunction)));
    }

    [Fact]
    public void Registration_TheServiceIsScoped()
    {
        var services = new ServiceCollection();
        services.AddCasazenMarketplace(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMarketplaceLicensedFunctionService));
        Assert.Equal(typeof(MarketplaceLicensedFunctionService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void TheMessagesAndTheGuidance_AreInBothLanguages()
    {
        var texts = new ResxMarketplaceTexts();
        foreach (var key in new[] { "MarketplaceFunctionAlreadyActive", "MarketplaceFunctionUnknown", MarketplaceLicensedFunctionService.RequestGuidanceKey })
        {
            var italian = texts.Get(key, "it", "Chat", "Casa");
            var english = texts.Get(key, "en", "Chat", "Casa");

            // A key that exists in neither file comes back as itself; an English text that is the Italian one is a text nobody translated.
            Assert.NotEqual(key, italian);
            Assert.NotEqual(key, english);
            Assert.NotEqual(italian, english);
        }

        Assert.Contains("Chat", texts.Get(MarketplaceLicensedFunctionService.RequestGuidanceKey, "en", "Chat", "Casa"), StringComparison.Ordinal);
        Assert.Contains("Casa", texts.Get(MarketplaceLicensedFunctionService.RequestGuidanceKey, "it", "Chat", "Casa"), StringComparison.Ordinal);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A license as the channel manager would give it: the modules named in <paramref name="on"/> and <paramref name="off"/> are stated, the others are not.</summary>
    private static OctorateLicense License(string? tier = "PRO", int? rooms = 3, string[]? on = null, string[]? off = null)
    {
        var modules = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var code in on ?? [])
            modules[code] = true;

        foreach (var code in off ?? [])
            modules[code] = false;

        return new OctorateLicense(tier, modules, rooms, null);
    }

    /// <summary>The status and the scope of the card of the function «chat» in a catalog.</summary>
    private static (MarketplaceCardStatus Status, MarketplaceStatusScope Scope) StateOf(MarketplaceCatalog catalog)
    {
        var card = catalog.Groups
            .Single(group => group.Id == MarketplaceGroupIds.ChannelManagerFunctions)
            .Entries.Single(entry => entry.Code == "chat");
        return (card.Status, card.StatusScope);
    }

    /// <summary>Every public member of an entity as text, to compare before and after.</summary>
    private static string Describe(object entity) =>
        string.Join(
            "; ",
            entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => $"{property.Name}={property.GetValue(entity)}"));

    /// <summary>A clock a test can move.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;

        public DateTime Now => _now.UtcDateTime;

        public void Set(DateTimeOffset utcNow) => _now = utcNow;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// An org with a connected account and two properties, each with a confirmed listing, a stub of the channel manager that answers the license it is
    /// given for each accommodation, and a memory cache of its own (what a restart empties).
    /// </summary>
    private sealed class World
    {
        public const string Accommodation = "585492";
        public const string OtherAccommodation = "585493";

        public Guid OrgA => MarketplaceLicensedFunctionTests.OrgA;

        public Guid OrgB { get; } = Guid.Parse("0b7e9a14-5d2c-4f31-8e6a-90c1d2e3f4a5");

        public Guid PropertyId { get; private set; }

        public Guid OtherPropertyId { get; private set; }

        public Guid PropertyOfB { get; private set; }

        public string PropertyName { get; private set; } = string.Empty;

        public HostScope OrgWide => new(OrgA);

        public bool FlagOn { get; set; } = true;

        public DbContextOptions<AppDbContext> Options { get; } =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"licensed-functions-{Guid.NewGuid()}").Options;

        public Mock<IOctorateApiClient> Client { get; } = new();

        public CapturingLogger<MarketplaceLicensedFunctionService> Logs { get; } = new();

        public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero));

        public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

        /// <summary>The license the channel manager answers for an accommodation; one with nothing stated when the test set none.</summary>
        public Dictionary<string, OctorateLicense> Licenses { get; } = new(StringComparer.Ordinal);

        public Exception? ReadFailure { get; set; }

        /// <summary>The accommodations whose license was read, in order.</summary>
        public List<string> Reads { get; } = [];

        public static async Task<World> CreateAsync(
            bool connected = true,
            bool linked = true,
            bool flag = true,
            OctorateMappingStatus mappingStatus = OctorateMappingStatus.Confirmed)
        {
            var world = new World { FlagOn = flag };
            await world.SeedAsync(connected, linked, mappingStatus);
            world.WireClient();
            return world;
        }

        public AppDbContext NewDb() => new(Options);

        public MarketplaceLicensedFunctionService Service(AppDbContext db) =>
            new(db, Client.Object, new OctorateOrgScope(NullTenantContext.Instance), Flags(), Host(), new ResxMarketplaceTexts(), Cache, Clock, Logs);

        public MarketplaceCatalogService Catalog(AppDbContext db)
        {
            var suppliers = new Mock<IHostSupplierUsageReader>();
            suppliers
                .Setup(s => s.ListAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<HostUsedSupplier>());
            return new MarketplaceCatalogService(db, suppliers.Object, Flags(), Mock.Of<IFileStorage>(), new ResxMarketplaceTexts(), Host());
        }

        public async Task GrantAsync(string userId, Guid propertyId)
        {
            await using var db = NewDb();
            db.Set<PropertyMemberAccess>().Add(new PropertyMemberAccess { OrgId = OrgA, UserId = userId, PropertyId = propertyId });
            await db.SaveChangesAsync();
        }

        /// <summary>A licensed function row of the first property of org A, as an earlier request or check left it.</summary>
        public async Task<MarketplaceActivation> AddRowAsync(
            string code,
            MarketplaceActivationStatus status,
            bool declared = false,
            bool verified = false,
            DateTime? changeRequestedAt = null,
            string? error = null)
        {
            var at = Clock.Now.AddDays(-3);
            var row = new MarketplaceActivation
            {
                OrgId = OrgA,
                PropertyId = PropertyId,
                Kind = MarketplaceActivationKind.LicensedFunction,
                TargetCode = code,
                Status = status,
                IsDeclared = declared,
                DeclaredAt = declared ? at : null,
                RequestedAt = declared ? at : null,
                VerifiedAt = verified ? at : null,
                ChangeRequestedAt = changeRequestedAt,
                LastErrorCode = error,
                LastErrorAt = error is null ? null : at,
                LastSyncedAt = at,
                CreatedAt = at,
                UpdatedAt = at,
            };
            await using var db = NewDb();
            db.MarketplaceActivations.Add(row);
            await db.SaveChangesAsync();
            return row;
        }

        public async Task<List<MarketplaceActivation>> ActivationsAsync(Guid? orgId = null)
        {
            await using var db = NewDb();
            var org = orgId ?? OrgA;
            return await db.MarketplaceActivations.AsNoTracking().Where(a => a.OrgId == org).OrderBy(a => a.TargetCode).ToListAsync();
        }

        /// <summary>The activations of an org as text, to compare before and after.</summary>
        public async Task<List<string>> RowsAsync(Guid? orgId = null) => (await ActivationsAsync(orgId)).Select(Describe).ToList();

        /// <summary>A second org with a connected account, a property, its listing, and a request and an active function of its own.</summary>
        public async Task SeedOtherOrgAsync()
        {
            var property = new Property { OrgId = OrgB, OwnerId = "auth0|owner-b", Name = "Casa B", Address = "Via B 1", City = "Roma", MaxGuests = 2 };
            PropertyOfB = property.Id;
            var connectionKey = Guid.NewGuid();

            await using var db = NewDb();
            db.Orgs.Add(new OrgEntity { Id = OrgB, Name = "Org B", Slug = "org-b", DisplayName = "Org B", PlanTier = PlanTier.Pro, BillingCountry = "IT" });
            db.OctorateConnections.Add(new OctorateConnection
            {
                Id = connectionKey,
                OrgId = OrgB,
                Environment = OctorateEnvironment.Test,
                Status = OctorateConnectionStatus.Connected,
            });
            db.Properties.Add(property);
            db.OctorateListingMappings.Add(Listing(OrgB, connectionKey, property.Id, "777001", "1", OctorateMappingStatus.Confirmed));
            db.MarketplaceActivations.AddRange(
                Row(OrgB, property.Id, "chat", MarketplaceActivationStatus.Requested),
                Row(OrgB, property.Id, "invoice", MarketplaceActivationStatus.Active));
            await db.SaveChangesAsync();
        }

        private async Task SeedAsync(bool connected, bool linked, OctorateMappingStatus mappingStatus)
        {
            var connectionKey = Guid.NewGuid();
            var first = NewProperty("Casa Lago");
            var second = NewProperty("Casa Monte");
            PropertyId = first.Id;
            OtherPropertyId = second.Id;
            PropertyName = first.Name;

            await using var db = NewDb();
            db.Orgs.Add(new OrgEntity { Id = OrgA, Name = "Org A", Slug = "org-a", DisplayName = "Org A", PlanTier = PlanTier.Pro, BillingCountry = "IT" });
            db.OctorateConnections.Add(new OctorateConnection
            {
                Id = connectionKey,
                OrgId = OrgA,
                Environment = OctorateEnvironment.Test,
                Status = connected ? OctorateConnectionStatus.Connected : OctorateConnectionStatus.Disconnected,
            });
            db.Properties.AddRange(first, second);
            if (linked)
            {
                db.OctorateListingMappings.AddRange(
                    Listing(OrgA, connectionKey, first.Id, Accommodation, "956528", mappingStatus),
                    Listing(OrgA, connectionKey, second.Id, OtherAccommodation, "956540", mappingStatus));
            }

            await db.SaveChangesAsync();
        }

        private static OctorateListingMapping Listing(
            Guid orgId, Guid connectionKey, Guid propertyId, string accommodation, string product, OctorateMappingStatus status) =>
            new()
            {
                OrgId = orgId,
                ConnectionId = connectionKey,
                OctorateAccommodationId = accommodation,
                PropertyId = propertyId,
                Status = status,
                OctorateProductId = product,
            };

        private static MarketplaceActivation Row(Guid orgId, Guid propertyId, string code, MarketplaceActivationStatus status) =>
            new()
            {
                OrgId = orgId,
                PropertyId = propertyId,
                Kind = MarketplaceActivationKind.LicensedFunction,
                TargetCode = code,
                Status = status,
                IsDeclared = status == MarketplaceActivationStatus.Requested,
            };

        private Property NewProperty(string name) =>
            new() { OrgId = OrgA, OwnerId = "auth0|owner", Name = name, Address = "Via Prova 1", City = "Como", MaxGuests = 4 };

        private IFeatureFlags Flags()
        {
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(() => FlagOn);
            return flags.Object;
        }

        private static IHostEnvironment Host()
        {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            return host.Object;
        }

        /// <summary>The channel manager: it answers the license it is given for the accommodation asked, or the failure of the test, and keeps what it was asked.</summary>
        private void WireClient() =>
            Client
                .Setup(c => c.GetAccommodationLicenseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string accommodation, CancellationToken _) =>
                {
                    Reads.Add(accommodation);
                    if (ReadFailure is { } failure)
                        return Task.FromException<OctorateLicense>(failure);

                    return Task.FromResult(Licenses.TryGetValue(accommodation, out var license) ? license : License());
                });
    }
}
