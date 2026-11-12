using System.Reflection;
using System.Security.Claims;
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
using Casazen.Web.DTOs;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F03/US01/T02, NF-58, MKT-05: the change of the settings of an activation and its deactivation. Proofs: a deactivation without <c>confirmed</c> neither deletes the
/// connection nor changes the status (data), the login details of a channel are used for one call and never stored or logged (privacy), and an activation of another org, one
/// outside the caller's scope and an org-level one for a caller limited to some properties are the same 404 (permission, data). The rules (the connection deleted before the row
/// is turned off and kept as history, the «To do» closed with it, a function of the license never deactivated by CasaZen, a partner turned off by the guided declaration, one
/// change request at a time) are checked with a stub of the client of the channel manager, the real partner service and the real service of the «To do» list: what is proven is
/// what CasaZen asks, stores and answers, not Octorate.
/// </summary>
public class MarketplaceChangeTests
{
    private const string Username = "portal-user-31b7c2";
    private const string Password = "portal-pass-9d4e6a";
    private const string HotelId = "H-4471";
    private const string Collaborator = "auth0|collaborator";
    private const string Host = "auth0|host";

    // ─── Proofs ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarketplaceDeactivate_WithoutConfirmation_NeverDeletesConnection_Test()
    {
        var w = await World.CreateAsync();
        var channel = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.ToFix, "7001", error: "connection_disabled");
        var partner = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active);
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active);
        var before = await w.DescribedAsync(w.OrgA);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // Without a yes, for every kind and for an activation that does not exist: the same 422, before anything is read or asked of the channel manager.
            foreach (var id in new[] { channel.Id, partner.Id, function.Id, Guid.NewGuid() })
            {
                var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.DeactivateAsync(w.OrgA, w.OrgWide, id, confirmed: false, Host));
                Assert.Equal("marketplace_confirmation_required", refused.Code);
            }
        }

        // The connection is still there, nothing was deleted, nothing was asked, and no row changed: the status is the one it had.
        Assert.Empty(w.Client.Invocations);
        Assert.Empty(w.Deleted);
        Assert.Equal(before, await w.DescribedAsync(w.OrgA));

        // With the yes the same call deletes the connection, once.
        await using (var db = w.NewDb())
        {
            var outcome = await w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, channel.Id, confirmed: true, Host);
            Assert.Equal(MarketplaceActivationStatus.Deactivated, outcome.Status);
        }

        Assert.Equal([(World.AccA1, "7001")], w.Deleted);
    }

    [Fact]
    public async Task MarketplaceChange_TheLoginDetailsOfAChannel_AreUsedForOneCall_AndNeverStoredOrLogged_Test()
    {
        var w = await World.CreateAsync();
        var channel = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var settings = new MarketplaceSettingsChange(HotelId, Username, Password);

        MarketplaceActivationOutcome outcome;
        await using (var db = w.NewDb())
            outcome = await w.Service(db).UpdateSettingsAsync(w.OrgA, w.OrgWide, channel.Id, settings, Host);

        // The login details went to the channel manager once, as the members of the change.
        var sent = Assert.Single(w.Patched);
        Assert.Equal((World.AccA1, "7001"), (sent.Accommodation, sent.ConnectionId));
        Assert.Equal((HotelId, Username, Password), (sent.Patch.HotelId, sent.Patch.Username, sent.Patch.Password));

        // Not one column of the activation holds them, nor the answer, nor the request when it is printed, nor a line of the log.
        var row = Assert.Single(await w.RowsAsync(w.OrgA));
        Assert.Equal(w.Now, row.UpdatedAt);
        AssertNoSecret(Describe(row));
        AssertNoSecret(JsonSerializer.Serialize(MarketplaceActivationChangeDto.From(outcome)));
        AssertNoSecret(settings.ToString());
        AssertNoSecret(sent.Patch.ToString());
        AssertNoSecret(new UpdateMarketplaceSettingsRequest { HotelId = HotelId, Username = Username, Password = Password }.ToString());
        AssertNoSecret(w.Logs.AllOutput);
        Assert.Contains(w.Logs.Entries, e => e.Message.Contains("settings changed", StringComparison.Ordinal));

        // A change whose answer fails: the exception is raised as it is, the row is as it was, and the log still holds nothing of the login details.
        var failing = await World.CreateAsync();
        var other = await failing.AddAsync(failing.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var other0 = await failing.DescribedAsync(failing.OrgA);
        failing.UpdateFailure = new OctorateServerException(null, new HttpRequestException($"reset by peer, password={Password}"));
        await using (var db = failing.NewDb())
            await Assert.ThrowsAsync<OctorateServerException>(() => failing.Service(db).UpdateSettingsAsync(failing.OrgA, failing.OrgWide, other.Id, settings, Host));

        Assert.Equal(other0, await failing.DescribedAsync(failing.OrgA));
        AssertNoSecret(failing.Logs.AllOutput);
    }

    [Fact]
    public async Task MarketplaceChange_AnotherOrgsOutsideScopeOrOrgLevelActivation_IsA404_Test()
    {
        var w = await World.CreateAsync();
        var ofB = await w.AddAsync(w.PropertyB, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "9001", orgId: w.OrgB);
        var outside = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7002");
        var orgLevel = await w.AddAsync(null, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active);
        var function = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active);
        var own = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7001");

        // The collaborator is granted the first property only.
        await w.GrantAsync(Collaborator, w.PropertyA1);
        var scope = new HostScope(w.OrgA, GrantedToUserId: Collaborator);
        var before = await w.DescribedAsync(w.OrgA);
        var beforeB = await w.DescribedAsync(w.OrgB);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // Another org's activation, one of a property outside the scope, and the row of the whole org: the 404 of an activation that does not exist, on every method.
            foreach (var id in new[] { ofB.Id, outside.Id, orgLevel.Id, function.Id, Guid.NewGuid() })
            {
                var steps = new Func<Task>[]
                {
                    () => service.UpdateSettingsAsync(w.OrgA, scope, id, new MarketplaceSettingsChange(HotelId, null, null), Collaborator),
                    () => service.PreviewDeactivationAsync(w.OrgA, scope, id),
                    () => service.DeactivateAsync(w.OrgA, scope, id, confirmed: true, Collaborator),
                    () => service.RequestFunctionChangeAsync(w.OrgA, scope, id, null, Collaborator),
                };
                foreach (var step in steps)
                {
                    var refused = await Assert.ThrowsAsync<NotFoundException>(step);
                    Assert.Equal("marketplace_activation_not_found", refused.Code);
                }
            }

            // The whole org (an org-wide scope) reaches its own activations of every property and the one of the whole org, and still not another org's.
            await service.PreviewDeactivationAsync(w.OrgA, w.OrgWide, outside.Id);
            await service.PreviewDeactivationAsync(w.OrgA, w.OrgWide, orgLevel.Id);
            await Assert.ThrowsAsync<NotFoundException>(() => service.PreviewDeactivationAsync(w.OrgA, w.OrgWide, ofB.Id));

            // The scope of another org is a mistake of the caller, refused before anything is read.
            await Assert.ThrowsAsync<ArgumentException>(() => service.DeactivateAsync(w.OrgA, new HostScope(w.OrgB), own.Id, confirmed: true, Host));
        }

        // Nothing left for the channel manager and nothing changed in CasaZen.
        Assert.Empty(w.Client.Invocations);
        Assert.Equal(before, await w.DescribedAsync(w.OrgA));
        Assert.Equal(beforeB, await w.DescribedAsync(w.OrgB));

        // The same collaborator reaches the activation of the property it was given.
        await using var again = w.NewDb();
        var preview = await w.Service(again).PreviewDeactivationAsync(w.OrgA, scope, own.Id);
        Assert.Equal(own.Id, preview.ActivationId);
    }

    // ─── Deactivation ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAsync_Confirmed_DeletesTheConnectionKeepsTheRowAsHistoryAndClosesItsTodo()
    {
        var w = await World.CreateAsync();
        var channel = await w.AddAsync(
            w.PropertyA1,
            MarketplaceActivationKind.Channel,
            "142",
            MarketplaceActivationStatus.ToFix,
            "7001",
            error: "connection_disabled",
            changeRequestedAt: w.Now.AddDays(-1));
        var todoId = await w.RaiseTodoAsync(channel);

        MarketplaceActivationOutcome outcome;
        await using (var db = w.NewDb())
            outcome = await w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, channel.Id, confirmed: true, Host);

        // The connection of that accommodation is deleted, once, without a list to find another.
        Assert.Equal([(World.AccA1, "7001")], w.Deleted);
        w.Client.Verify(c => c.ListConnectionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal((channel.Id, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Deactivated), (outcome.ActivationId, outcome.Kind, outcome.Code, outcome.Status));

        // The row stays as history: turned off, without the connection that is gone and without the problem it carried, and its item of the «To do» is closed by the person.
        var row = Assert.Single(await w.RowsAsync(w.OrgA));
        Assert.Equal(MarketplaceActivationStatus.Deactivated, row.Status);
        Assert.Null(row.OctorateConnectionId);
        Assert.Null(row.LastErrorCode);
        Assert.Null(row.LastErrorAt);
        Assert.Null(row.ChangeRequestedAt);
        Assert.Null(row.TodoActivityId);
        Assert.Equal(w.Now, row.UpdatedAt);
        var todo = Assert.Single(await w.TodosAsync());
        Assert.Equal((todoId, InternalActivity.StatusCompleted, Host), (todo.Id, todo.Status, todo.ClosedBy));
        Assert.NotNull(todo.CompletedAt);

        // Asked again (a double click): it is already off, and nothing is deleted a second time.
        await using (var db = w.NewDb())
        {
            var second = await w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, channel.Id, confirmed: true, Host);
            Assert.Equal(MarketplaceActivationStatus.Deactivated, second.Status);
        }

        Assert.Single(w.Deleted);

        // A tool is the same, with the connection of its own.
        var tool = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);
        await using (var db = w.NewDb())
            await w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, tool.Id, confirmed: true, Host);

        Assert.Equal([(World.AccA1, "7001"), (World.AccA2, "7003")], w.Deleted);
        Assert.Equal(2, (await w.RowsAsync(w.OrgA)).Count(r => r.Status == MarketplaceActivationStatus.Deactivated));
    }

    [Fact]
    public async Task DeactivateAsync_AConnectionTheChannelManagerNoLongerHas_OrARowWithNone_IsTurnedOffAllTheSame()
    {
        var w = await World.CreateAsync();
        var gone = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var freed = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.ToFix, error: "connection_missing");

        // Octorate does not know the connection any more (removed on its panel): what was asked is done, the row is turned off.
        w.DeleteFailure = new OctorateNotFoundException();
        await using (var db = w.NewDb())
            Assert.Equal(MarketplaceActivationStatus.Deactivated, (await w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, gone.Id, confirmed: true, Host)).Status);

        // A row that has no connection id has nothing to delete, and none is looked for by guessing.
        w.DeleteFailure = null;
        w.Deleted.Clear();
        await using (var db = w.NewDb())
            Assert.Equal(MarketplaceActivationStatus.Deactivated, (await w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, freed.Id, confirmed: true, Host)).Status);

        Assert.Empty(w.Deleted);
        w.Client.Verify(c => c.ListConnectionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.All(await w.RowsAsync(w.OrgA), row => Assert.Equal(MarketplaceActivationStatus.Deactivated, row.Status));
    }

    [Fact]
    public async Task DeactivateAsync_AFailureOfTheChannelManager_ChangesNothing_AndIsRaisedAsItIs()
    {
        var w = await World.CreateAsync();
        var channel = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.ToFix, "7001", error: "connection_disabled");
        await w.RaiseTodoAsync(channel);
        var before = await w.DescribedAsync(w.OrgA);

        // No answer, the channel itself failing, the rate limit: each is raised, the row stays as it was, its item of the «To do» stays open, and the host can try again.
        foreach (var failure in new OctorateException[] { new OctorateServerException(503), new OctoratePartnerFailureException(), new OctorateRateLimitException(429) })
        {
            w.DeleteFailure = failure;
            await using var db = w.NewDb();
            var raised = await Assert.ThrowsAnyAsync<OctorateException>(() => w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, channel.Id, confirmed: true, Host));
            Assert.Equal(failure.Code, raised.Code);
        }

        Assert.Equal(before, await w.DescribedAsync(w.OrgA));
        Assert.Null(Assert.Single(await w.TodosAsync()).CompletedAt);

        // The org is not connected to the channel manager in this environment, or the property has no confirmed listing: it cannot delete its own connection, and says why.
        var disconnected = await World.CreateAsync(connected: false);
        var row = await disconnected.AddAsync(disconnected.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        await using (var db = disconnected.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => disconnected.Service(db).DeactivateAsync(disconnected.OrgA, disconnected.OrgWide, row.Id, confirmed: true, Host));
            Assert.Equal("marketplace_octorate_not_connected", refused.Code);
        }

        var unlinked = await World.CreateAsync(linked: false);
        var unlinkedRow = await unlinked.AddAsync(unlinked.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        await using (var db = unlinked.NewDb())
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => unlinked.Service(db).DeactivateAsync(unlinked.OrgA, unlinked.OrgWide, unlinkedRow.Id, confirmed: true, Host));
            Assert.Equal("marketplace_property_not_linked", refused.Code);
        }

        Assert.Empty(disconnected.Deleted);
        Assert.Empty(unlinked.Deleted);
    }

    [Fact]
    public async Task DeactivateAsync_ALicensedFunction_IsNeverDeactivatedByCasaZen()
    {
        var w = await World.CreateAsync();
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active, verified: true);
        var before = await w.DescribedAsync(w.OrgA);

        await using var db = w.NewDb();
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => w.Service(db).DeactivateAsync(w.OrgA, w.OrgWide, function.Id, confirmed: true, Host));

        // The license is the channel manager's: the answer points to the change request, nothing is asked of the channel manager, and the row is as it was.
        Assert.Equal("marketplace_change_not_supported", refused.Code);
        Assert.Empty(w.Client.Invocations);
        Assert.Equal(before, await w.DescribedAsync(w.OrgA));
    }

    [Fact]
    public async Task DeactivateAsync_APartner_IsTurnedOffByTheGuidedDeclaration_AndNeedsNoConnector()
    {
        var w = await World.CreateAsync(flag: false);
        var ofProperty = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active, declared: true);
        var ofOrg = await w.AddAsync(null, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active, declared: true);

        await using (var db = w.NewDb())
        {
            var service = w.Service(db);

            // The partner is not behind the connector: the host says it turned it off on the partner's site, and the row is turned off and kept as history.
            var outcome = await service.DeactivateAsync(w.OrgA, w.OrgWide, ofProperty.Id, confirmed: true, Host);
            Assert.Equal((ofProperty.Id, MarketplaceActivationKind.Partner, "partner-di-prova", w.PropertyA1, MarketplaceActivationStatus.Deactivated), (outcome.ActivationId, outcome.Kind, outcome.Code, outcome.PropertyId, outcome.Status));

            // The row of the whole org is the whole org's decision: a caller who reaches the whole org turns it off.
            var whole = await service.DeactivateAsync(w.OrgA, w.OrgWide, ofOrg.Id, confirmed: true, Host);
            Assert.Equal((MarketplaceActivationStatus.Deactivated, (Guid?)null), (whole.Status, whole.PropertyId));

            // Again: it is already off, and nothing changes.
            Assert.Equal(MarketplaceActivationStatus.Deactivated, (await service.DeactivateAsync(w.OrgA, w.OrgWide, ofProperty.Id, confirmed: true, Host)).Status);
        }

        var rows = await w.RowsAsync(w.OrgA);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(MarketplaceActivationStatus.Deactivated, row.Status));
        Assert.Empty(w.Client.Invocations);
    }

    // ─── The preview ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewDeactivationAsync_ExplainsTheEffectInPlainWords_InBothLanguages_ForEveryKind_AndWritesNothing()
    {
        var w = await World.CreateAsync();
        await w.AddPortalAsync("142", "Booking di prova", MarketplacePortalCategory.Ota);
        await w.AddPortalAsync("300", "Strumento di prova", MarketplacePortalCategory.ExternalTool);
        await w.AddPartnerAsync("partner-di-prova", "Servizio Uno");
        var channel = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var tool = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active, verified: true);
        var partner = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active);
        var uncatalogued = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "999", MarketplaceActivationStatus.Active, "7009");
        var before = await w.DescribedAsync(w.OrgA);
        var texts = new ResxMarketplaceTexts();

        await using var db = w.NewDb();
        var service = w.Service(db);
        var cases = new (MarketplaceActivation Row, string Name, int Effects, bool Guided)[]
        {
            (channel, "Booking di prova", 3, false),
            (tool, "Strumento di prova", 2, false),
            (function, texts.Get("Marketplace.Licensed.chat.Name", "it"), 2, true),
            (partner, "Servizio Uno", 2, true),
            (uncatalogued, "999", 3, false),
        };
        foreach (var (row, name, effects, guided) in cases)
        {
            var italian = await service.PreviewDeactivationAsync(w.OrgA, w.OrgWide, row.Id, "it");
            Assert.Equal((row.Id, row.Kind, row.TargetCode, true, guided), (italian.ActivationId, italian.Kind, italian.Code, italian.RequiresConfirmation, italian.Guided));
            Assert.Equal(effects, italian.Effects.Count);

            // The name of the channel, the tool, the function or the partner is put into the sentences, and no placeholder is left in them.
            Assert.Contains(italian.Effects, effect => effect.Contains(name, StringComparison.Ordinal));
            Assert.All(italian.Effects, effect => Assert.DoesNotContain("{0}", effect, StringComparison.Ordinal));

            // The same sentences in English are other words, with the English name of a function.
            var english = await service.PreviewDeactivationAsync(w.OrgA, w.OrgWide, row.Id, "en");
            Assert.Equal(effects, english.Effects.Count);
            Assert.NotEqual(italian.Effects, english.Effects);
            Assert.All(english.Effects, effect => Assert.DoesNotContain("Marketplace.Deactivate", effect, StringComparison.Ordinal));
        }

        // The example of the requirement, in plain words: new bookings no longer arrive, and the listing stays published until the host closes it on the channel.
        var channelEffects = (await service.PreviewDeactivationAsync(w.OrgA, w.OrgWide, channel.Id, "en")).Effects;
        Assert.Contains("New Booking di prova bookings will no longer arrive in CasaZen.", channelEffects);
        Assert.Contains("Your listing on Booking di prova stays published until you close it on the channel yourself.", channelEffects);

        // A preview reads: nothing is asked of the channel manager and nothing is written.
        Assert.Empty(w.Client.Invocations);
        Assert.Equal(before, await w.DescribedAsync(w.OrgA));
    }

    [Fact]
    public void TheSentencesOfThePreview_ExistInBothLanguages_ForTheNumberOfEffectsOfEachKind()
    {
        var texts = new ResxMarketplaceTexts();
        var expected = new Dictionary<MarketplaceActivationKind, int>
        {
            [MarketplaceActivationKind.Channel] = 3,
            [MarketplaceActivationKind.Tool] = 2,
            [MarketplaceActivationKind.LicensedFunction] = 2,
            [MarketplaceActivationKind.Partner] = 2,
        };
        foreach (var (kind, count) in expected)
        {
            for (var number = 1; number <= count + 1; number++)
            {
                var key = $"Marketplace.Deactivate.{kind}.Effect{number}";
                var italian = texts.Get(key, "it", "Nome");
                var english = texts.Get(key, "en", "Nome");

                // The sentences that the service reads exist and are translated; the next number does not exist (a key that exists in neither file comes back as itself).
                if (number <= count)
                {
                    Assert.NotEqual(key, italian);
                    Assert.NotEqual(key, english);
                    Assert.NotEqual(italian, english);
                }
                else
                {
                    Assert.Equal(key, italian);
                }
            }
        }

        foreach (var key in new[]
                 {
                     "MarketplaceConfirmationRequired",
                     "MarketplaceChangeNotSupported",
                     "MarketplaceChangeAlreadyRequested",
                     "MarketplaceSettingsRequired",
                     "MarketplaceChangeNoteTooLong",
                 })
        {
            Assert.NotEqual(key, texts.Get(key, "it"));
            Assert.NotEqual(key, texts.Get(key, "en"));
            Assert.NotEqual(texts.Get(key, "it"), texts.Get(key, "en"));
        }
    }

    // ─── The change request of a function ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestFunctionChangeAsync_RecordsTheRequest_KeepsTheStatus_AndOnlyOneIsOpenAtATime()
    {
        var w = await World.CreateAsync();
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active, verified: true);

        MarketplaceActivationOutcome outcome;
        await using (var db = w.NewDb())
            outcome = await w.Service(db).RequestFunctionChangeAsync(w.OrgA, w.OrgWide, function.Id, "please switch it off", Host);

        // The date of the request is recorded and the status stays: the card says «change requested on», until the license answers.
        Assert.Equal((MarketplaceActivationStatus.Active, (DateTime?)w.Now), (outcome.Status, outcome.ChangeRequestedAt));
        var row = Assert.Single(await w.RowsAsync(w.OrgA));
        Assert.Equal((MarketplaceActivationStatus.Active, w.Now, w.Now), (row.Status, row.ChangeRequestedAt, row.UpdatedAt));

        // One request is open at a time: the second is a 409 and the date is the first one's.
        w.Clock.Set(w.Now.AddDays(1));
        await using (var db = w.NewDb())
        {
            var again = await Assert.ThrowsAsync<DomainConflictException>(() => w.Service(db).RequestFunctionChangeAsync(w.OrgA, w.OrgWide, function.Id, null, Host));
            Assert.Equal("marketplace_change_already_requested", again.Code);
        }

        Assert.Equal(w.Now.AddDays(-1), Assert.Single(await w.RowsAsync(w.OrgA)).ChangeRequestedAt);

        // The reconciliation of the license clears the request when the state of the function changes (the license answered it): a new one can be made.
        await using (var db = w.NewDb())
        {
            var stored = await db.MarketplaceActivations.SingleAsync();
            stored.ChangeRequestedAt = null;
            await db.SaveChangesAsync();
        }

        await using (var db = w.NewDb())
            Assert.Equal(w.Now, (await w.Service(db).RequestFunctionChangeAsync(w.OrgA, w.OrgWide, function.Id, null, Host)).ChangeRequestedAt);

        // CasaZen asks the channel manager nothing and writes no license.
        Assert.Empty(w.Client.Invocations);
    }

    [Fact]
    public async Task RequestFunctionChangeAsync_IsOnlyForAFunctionThatIsNotTurnedOff_AndKeepsTheNoteShort()
    {
        var w = await World.CreateAsync();
        var channel = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var tool = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);
        var partner = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active);
        var off = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.LicensedFunction, "invoice", MarketplaceActivationStatus.Deactivated);
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active, verified: true);
        var before = await w.DescribedAsync(w.OrgA);

        await using var db = w.NewDb();
        var service = w.Service(db);
        foreach (var id in new[] { channel.Id, tool.Id, partner.Id, off.Id })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.RequestFunctionChangeAsync(w.OrgA, w.OrgWide, id, null, Host));
            Assert.Equal("marketplace_change_not_supported", refused.Code);
        }

        // A note longer than the limit is a 422, before the row is read: the same answer for an activation that does not exist.
        foreach (var id in new[] { function.Id, Guid.NewGuid() })
        {
            var tooLong = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.RequestFunctionChangeAsync(w.OrgA, w.OrgWide, id, new string('x', MarketplaceChangeLimits.MaxNoteLength + 1), Host));
            Assert.Equal("marketplace_change_note_invalid", tooLong.Code);
        }

        Assert.Equal(before, await w.DescribedAsync(w.OrgA));

        // A note of the longest length is taken, and is not kept anywhere.
        var outcome = await service.RequestFunctionChangeAsync(w.OrgA, w.OrgWide, function.Id, new string('x', MarketplaceChangeLimits.MaxNoteLength), Host);
        Assert.NotNull(outcome.ChangeRequestedAt);
        Assert.DoesNotContain("xxxxx", string.Join("; ", (await w.RowsAsync(w.OrgA)).Select(Describe)), StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxx", w.Logs.AllOutput, StringComparison.Ordinal);
    }

    // ─── The settings ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSettingsAsync_SendsOnlyWhatThePortalAsksFor_TrimmedAndNeverEverything()
    {
        var w = await World.CreateAsync();
        var both = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        var passwordOnly = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "400", MarketplaceActivationStatus.Active, "7002", verified: true);
        var nothing = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Tool, "300", MarketplaceActivationStatus.Active, "7003", verified: true);

        await using var db = w.NewDb();
        var service = w.Service(db);

        // The identifier alone: only the identifier is sent, trimmed, and nothing of the credentials.
        await service.UpdateSettingsAsync(w.OrgA, w.OrgWide, both.Id, new MarketplaceSettingsChange("  H-9  ", "  ", ""), Host);
        var patch = Assert.Single(w.Patched).Patch;
        Assert.Equal(("H-9", null, null), (patch.HotelId, patch.Username, patch.Password));

        // A portal that asks only for the login details drops the identifier the request carries.
        await service.UpdateSettingsAsync(w.OrgA, w.OrgWide, passwordOnly.Id, new MarketplaceSettingsChange("H-9", null, "new-password-1"), Host);
        patch = w.Patched[1].Patch;
        Assert.Equal((null, null, "new-password-1"), (patch.HotelId, patch.Username, patch.Password));
        Assert.Equal((World.AccA2, "7002"), (w.Patched[1].Accommodation, w.Patched[1].ConnectionId));

        // A change that names nothing the portal asks for is never «everything»: 422, and nothing is sent.
        var before = w.Patched.Count;
        foreach (var empty in new[] { new MarketplaceSettingsChange(null, null, null), new MarketplaceSettingsChange("  ", "", null), new MarketplaceSettingsChange("", " ", "") })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateSettingsAsync(w.OrgA, w.OrgWide, both.Id, empty, Host));
            Assert.Equal("marketplace_settings_required", refused.Code);
        }

        // A portal with nothing to change (the access is given on the channel manager's panel) cannot be changed from here.
        var nothingToChange = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateSettingsAsync(w.OrgA, w.OrgWide, nothing.Id, new MarketplaceSettingsChange(HotelId, Username, Password), Host));
        Assert.Equal("marketplace_change_not_supported", nothingToChange.Code);
        Assert.Equal(before, w.Patched.Count);
    }

    [Fact]
    public async Task UpdateSettingsAsync_IsOnlyForAChannelOrAToolThatHasItsConnection()
    {
        var w = await World.CreateAsync();
        var partner = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Partner, "partner-di-prova", MarketplaceActivationStatus.Active);
        var function = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active);
        var off = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Deactivated);
        var noConnection = await w.AddAsync(w.PropertyA2, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.ToFix, error: "connection_missing");
        var gone = await w.AddAsync(w.PropertyA1, MarketplaceActivationKind.Channel, "274", MarketplaceActivationStatus.Active, "7005", verified: true);
        var change = new MarketplaceSettingsChange(HotelId, null, null);

        await using var db = w.NewDb();
        var service = w.Service(db);

        // A partner, a function of the license and a row that was turned off: 422 «not supported»; nothing is sent.
        foreach (var id in new[] { partner.Id, function.Id, off.Id })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateSettingsAsync(w.OrgA, w.OrgWide, id, change, Host));
            Assert.Equal("marketplace_change_not_supported", refused.Code);
        }

        // An activation with no connection, and one whose connection the channel manager no longer has: 409, the activation starts again.
        var missing = await Assert.ThrowsAsync<DomainConflictException>(() => service.UpdateSettingsAsync(w.OrgA, w.OrgWide, noConnection.Id, change, Host));
        Assert.Equal("marketplace_connection_missing", missing.Code);
        w.UpdateFailure = new OctorateNotFoundException();
        var notThere = await Assert.ThrowsAsync<DomainConflictException>(() => service.UpdateSettingsAsync(w.OrgA, w.OrgWide, gone.Id, change, Host));
        Assert.Equal("marketplace_connection_missing", notThere.Code);

        // The channel manager failing is raised as it is, and the row is as it was.
        w.UpdateFailure = new OctoratePartnerFailureException();
        await Assert.ThrowsAsync<OctoratePartnerFailureException>(() => service.UpdateSettingsAsync(w.OrgA, w.OrgWide, gone.Id, change, Host));
        Assert.Equal(w.Now.AddDays(-2), (await w.RowsAsync(w.OrgA)).Single(r => r.Id == gone.Id).UpdatedAt);

        // With the connector off the activations that go through it do not exist: the 404; the partner is not behind it but has no settings.
        var off2 = await World.CreateAsync(flag: false);
        var channel = await off2.AddAsync(off2.PropertyA1, MarketplaceActivationKind.Channel, "142", MarketplaceActivationStatus.Active, "7001", verified: true);
        await using var db2 = off2.NewDb();
        var absent = await Assert.ThrowsAsync<NotFoundException>(() => off2.Service(db2).UpdateSettingsAsync(off2.OrgA, off2.OrgWide, channel.Id, change, Host));
        Assert.Equal("marketplace_activation_not_found", absent.Code);
        Assert.Empty(off2.Patched);
    }

    // ─── The endpoints, the registration ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheEndpoints_NeedOtaWrite_AreNotBehindTheConnectorAsAWhole_AndOnlyTheSettingsChangeIsGated()
    {
        var controller = typeof(MarketplaceActivationChangesController);
        Assert.Equal("api/marketplace/activations/{id:guid}", controller.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.OtaWrite, Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>()).Policy);

        // The partners never depend on Octorate: no gate on the class; the kind of the row decides in the service.
        Assert.Empty(controller.GetCustomAttributes<FeatureGateAttribute>());

        string? TemplateOf(string action) => controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>().Single().Template;
        Type VerbOf(string action) => controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>().Single().GetType();
        Assert.Equal((typeof(HttpPatchAttribute), null), (VerbOf(nameof(MarketplaceActivationChangesController.UpdateSettings)), TemplateOf(nameof(MarketplaceActivationChangesController.UpdateSettings))));
        Assert.Equal((typeof(HttpGetAttribute), "deactivation-preview"), (VerbOf(nameof(MarketplaceActivationChangesController.PreviewDeactivation)), TemplateOf(nameof(MarketplaceActivationChangesController.PreviewDeactivation))));
        Assert.Equal((typeof(HttpPostAttribute), "deactivate"), (VerbOf(nameof(MarketplaceActivationChangesController.Deactivate)), TemplateOf(nameof(MarketplaceActivationChangesController.Deactivate))));
        Assert.Equal((typeof(HttpPostAttribute), "change-request"), (VerbOf(nameof(MarketplaceActivationChangesController.RequestChange)), TemplateOf(nameof(MarketplaceActivationChangesController.RequestChange))));

        // The settings are a channel's or a tool's, which go through Octorate: gated before authentication. The other three answer by the kind of the row.
        var settings = controller.GetMethod(nameof(MarketplaceActivationChangesController.UpdateSettings))!;
        Assert.Equal(FeatureFlags.OctorateConnector, Assert.Single(settings.GetCustomAttributes<FeatureGateAttribute>()).Flag);
        foreach (var action in new[] { nameof(MarketplaceActivationChangesController.PreviewDeactivation), nameof(MarketplaceActivationChangesController.Deactivate), nameof(MarketplaceActivationChangesController.RequestChange) })
            Assert.Empty(controller.GetMethod(action)!.GetCustomAttributes<FeatureGateAttribute>());
    }

    [Fact]
    public async Task TheDeactivateEndpoint_ReadsNoBodyAndNoConfirmedAsNo_AndTheCallerIsTheOneOfTheToken()
    {
        var orgId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var scope = new HostScope(orgId);
        var outcome = new MarketplaceActivationOutcome(id, MarketplaceActivationKind.Channel, "142", Guid.NewGuid(), MarketplaceActivationStatus.Deactivated, null);

        var changes = new Mock<IMarketplaceChangeService>();
        changes
            .Setup(c => c.DeactivateAsync(orgId, scope, id, It.IsAny<bool>(), Host, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        changes
            .Setup(c => c.UpdateSettingsAsync(orgId, scope, id, It.IsAny<MarketplaceSettingsChange>(), Host, It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(Host, It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var controller = new MarketplaceActivationChangesController(changes.Object, orgs.Object, scopes.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Host)], "test")) },
            },
        };

        // No body, and a body that does not say yes: not confirmed. Only an explicit yes is one.
        Assert.IsType<OkObjectResult>((await controller.Deactivate(id, null, CancellationToken.None)).Result);
        await controller.Deactivate(id, new DeactivateMarketplaceActivationRequest(), CancellationToken.None);
        await controller.Deactivate(id, new DeactivateMarketplaceActivationRequest { Confirmed = true }, CancellationToken.None);
        changes.Verify(c => c.DeactivateAsync(orgId, scope, id, false, Host, It.IsAny<CancellationToken>()), Times.Exactly(2));
        changes.Verify(c => c.DeactivateAsync(orgId, scope, id, true, Host, It.IsAny<CancellationToken>()), Times.Once);

        // A settings change with no body is a change that names nothing (the service answers 422), never a 400 that says nothing about the rule.
        await controller.UpdateSettings(id, null, CancellationToken.None);
        changes.Verify(c => c.UpdateSettingsAsync(orgId, scope, id, new MarketplaceSettingsChange(null, null, null), Host, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Registration_TheServiceIsScoped()
    {
        var services = new ServiceCollection();
        services.AddCasazenMarketplace(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMarketplaceChangeService));
        Assert.Equal((typeof(MarketplaceChangeService), ServiceLifetime.Scoped), (descriptor.ImplementationType, descriptor.Lifetime));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertNoSecret(string text)
    {
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Username, text, StringComparison.Ordinal);
    }

    private static IFeatureFlags Flags(bool on)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(on);
        return flags.Object;
    }

    /// <summary>Every public member of an entity as text, so that a secret in any column of it is found and a change of any column is seen.</summary>
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

        public void Set(DateTime utcNow) => _now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Two orgs, each with a connected account in this environment; org A has two properties and org B one, each with a confirmed listing; the portals «142» (asks for the
    /// identifier and the login details), «400» (the login details only) and «300» (nothing: the access is given on the panel); a stub of the channel manager that keeps what
    /// it is asked to change and delete; the real partner service and the real service of the «To do» list.
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

        public DateTime Now => Clock.GetUtcNow().UtcDateTime;

        public DbContextOptions<AppDbContext> Options { get; } =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"marketplace-change-{Guid.NewGuid()}").Options;

        public Mock<IOctorateApiClient> Client { get; } = new();

        public CapturingLogger<MarketplaceChangeService> Logs { get; } = new();

        public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 8, 9, 30, 0, TimeSpan.Zero));

        /// <summary>The connections deleted on the channel manager, in order.</summary>
        public List<(string Accommodation, string ConnectionId)> Deleted { get; } = [];

        /// <summary>The changes sent to the channel manager, in order.</summary>
        public List<(string Accommodation, string ConnectionId, OctorateConnectionPatch Patch)> Patched { get; } = [];

        public Exception? DeleteFailure { get; set; }

        public Exception? UpdateFailure { get; set; }

        public static async Task<World> CreateAsync(bool connected = true, bool linked = true, bool flag = true)
        {
            var world = new World { FlagOn = flag };
            await world.SeedAsync(connected, linked);
            world.WireClient();
            return world;
        }

        public AppDbContext NewDb() => new(Options);

        public MarketplaceChangeService Service(AppDbContext db)
        {
            var host = new Mock<IHostEnvironment>();
            host.SetupGet(h => h.EnvironmentName).Returns("Staging");
            var flags = Flags(FlagOn);
            var partners = new MarketplacePartnerActivationService(db, flags, host.Object, Clock, NullLogger<MarketplacePartnerActivationService>.Instance);
            var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock);
            return new MarketplaceChangeService(
                db,
                Client.Object,
                new OctorateOrgScope(NullTenantContext.Instance),
                partners,
                activities,
                flags,
                host.Object,
                new ResxMarketplaceTexts(),
                Clock,
                Logs);
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

        public async Task AddPartnerAsync(string code, string name)
        {
            await using var db = NewDb();
            db.MarketplacePartners.Add(new MarketplacePartner { Code = code, Name = name, WebsiteUrl = "https://example.com", IsVisible = true });
            await db.SaveChangesAsync();
        }

        /// <summary>An activation as an earlier step or sync left it, written two days ago.</summary>
        public async Task<MarketplaceActivation> AddAsync(
            Guid? propertyId,
            MarketplaceActivationKind kind,
            string code,
            MarketplaceActivationStatus status,
            string? connectionId = null,
            string? error = null,
            Guid? orgId = null,
            bool verified = false,
            bool declared = false,
            DateTime? changeRequestedAt = null)
        {
            var at = Now.AddDays(-2);
            var row = new MarketplaceActivation
            {
                OrgId = orgId ?? OrgA,
                PropertyId = propertyId,
                Kind = kind,
                TargetCode = code,
                Status = status,
                IsDeclared = declared,
                DeclaredAt = declared ? at : null,
                OctorateConnectionId = connectionId,
                LastErrorCode = error,
                LastErrorAt = error is null ? null : at,
                LastSyncedAt = at,
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

        /// <summary>Raises the «To do» of a problem for the activation, as the health sync does, and keeps its id on the row.</summary>
        public async Task<Guid> RaiseTodoAsync(MarketplaceActivation activation)
        {
            await using var db = NewDb();
            var id = await new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock).CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.IntegrationError,
                    Action = ActivityAction.ResumeWizard,
                    EntityType = nameof(MarketplaceActivation),
                    EntityId = activation.Id,
                    PropertyId = activation.PropertyId,
                    Urgency = ActivityUrgency.High,
                    Reason = activation.LastErrorCode,
                },
                CancellationToken.None);
            var row = await db.MarketplaceActivations.SingleAsync(a => a.Id == activation.Id);
            row.TodoActivityId = id;
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<List<MarketplaceActivation>> RowsAsync(Guid orgId)
        {
            await using var db = NewDb();
            return await db.MarketplaceActivations.AsNoTracking().Where(a => a.OrgId == orgId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        }

        /// <summary>The activations of an org as text, to compare before and after.</summary>
        public async Task<List<string>> DescribedAsync(Guid orgId) => (await RowsAsync(orgId)).Select(Describe).ToList();

        public async Task<List<InternalActivity>> TodosAsync()
        {
            await using var db = NewDb();
            return await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
        }

        private async Task SeedAsync(bool connected, bool linked)
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
            if (linked)
                db.OctorateListingMappings.AddRange(Listing(OrgA, keyA, a1.Id, AccA1), Listing(OrgA, keyA, a2.Id, AccA2));

            db.OctorateListingMappings.Add(Listing(OrgB, keyB, b.Id, AccB));
            db.MarketplacePortals.AddRange(
                Portal("142", MarketplacePortalCategory.Ota, hotelId: true, password: true),
                Portal("274", MarketplacePortalCategory.Ota, hotelId: true, password: true),
                Portal("400", MarketplacePortalCategory.Ota, hotelId: false, password: true),
                Portal("300", MarketplacePortalCategory.ExternalTool, hotelId: false, password: false));
            await db.SaveChangesAsync();
        }

        private static MarketplacePortal Portal(string code, MarketplacePortalCategory category, bool hotelId, bool password) =>
            new()
            {
                PortalCode = code,
                Name = "Portal " + code,
                Category = category,
                Creatable = true,
                Enabled = true,
                IsListed = true,
                RequiresHotelId = hotelId,
                RequiresPassword = password,
            };

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
                .Setup(c => c.DeleteConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string accommodation, string connectionId, CancellationToken _) =>
                {
                    if (DeleteFailure is { } failure)
                        return Task.FromException(failure);

                    Deleted.Add((accommodation, connectionId));
                    return Task.CompletedTask;
                });

            Client
                .Setup(c => c.UpdateConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OctorateConnectionPatch>(), It.IsAny<CancellationToken>()))
                .Returns((string accommodation, string connectionId, OctorateConnectionPatch patch, CancellationToken _) =>
                {
                    if (UpdateFailure is { } failure)
                        return Task.FromException(failure);

                    Patched.Add((accommodation, connectionId, patch));
                    return Task.CompletedTask;
                });

            Client
                .Setup(c => c.ListConnectionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        }
    }
}
