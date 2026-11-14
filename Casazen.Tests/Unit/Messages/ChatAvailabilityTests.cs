using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Messages;
using Casazen.Web.Infrastructure;
using Casazen.Infrastructure.Data;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E23/F01/US01/T03 (NF-61, NF-62): the inbox tells the listings whose chat is not licensed yet (<c>GET api/messages/channel-status</c>) and the reply box explains instead of failing
/// (<c>MessageThreadActions.Reason</c> <c>chat_not_licensed</c>), on the real hub service over a database. The proof the brief asks for: a caller sees the listings of its own
/// properties only; and the rules around it: a listing nobody confirmed is not listed, the answer holds no code of the channel manager, and the endpoint has the policy and the scope
/// of the list.
/// </summary>
public class ChatAvailabilityTests : IAsyncLifetime
{
    private static readonly DateTime Checked = MessageHubTestKit.Now.UtcDateTime.AddMinutes(-7);

    private readonly AppDbContext _db = HostScopeScenario.NewInMemoryDb();
    private MessageHubWorld _world = null!;

    public async Task InitializeAsync() => _world = await MessageHubTestKit.SeedAsync(_db);

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    /// <summary>
    /// PROOF TEST: permission rule. A caller sees the listings of the properties it reaches and no other: the whole org sees its two houses, the collaborator «Solo alcuni» only the one it
    /// was given, the other org only its own, and a listing nobody confirmed is in none of them. What the answer holds is a status, a property and an instant: no accommodation id, no
    /// code of the channel manager.
    /// </summary>
    [Fact]
    public async Task ChannelStatus_OnlyUserProperties_Test()
    {
        await AddListingAsync(_world.Scope.Granted.Id, "ACC-GRANTED", state: OtaMessageSyncErrorCodes.ChatNotLicensed);
        await AddListingAsync(_world.Scope.Hidden.Id, "ACC-HIDDEN", state: "octorate_unavailable");
        await AddListingAsync(_world.Scope.OtherOrgProperty.Id, "ACC-OTHER", orgId: _world.Scope.OtherOrgId);
        await AddListingAsync(null, "ACC-PROPOSED", status: OctorateMappingStatus.Proposed);
        var hub = MessageHubTestKit.Service(_db);

        var wholeOrg = await hub.GetChannelStatusAsync(_world.Scope.OrgWide);
        var collaborator = await hub.GetChannelStatusAsync(_world.Scope.Restricted);
        var otherOrg = await hub.GetChannelStatusAsync(new HostScope(_world.Scope.OtherOrgId));

        // Ordered by the name of the property: «Casa Bianca» (the hidden house) before «Trullo».
        Assert.Equal(
            [
                new MessageChannelStatus(_world.Scope.Hidden.Id, "Casa Bianca", MessageChannelStatuses.Error, Checked),
                new MessageChannelStatus(_world.Scope.Granted.Id, "Trullo", MessageChannelStatuses.WaitingPermission, Checked),
            ],
            wholeOrg);
        Assert.Equal(
            [new MessageChannelStatus(_world.Scope.Granted.Id, "Trullo", MessageChannelStatuses.WaitingPermission, Checked)],
            collaborator);
        Assert.Equal([new MessageChannelStatus(_world.Scope.OtherOrgProperty.Id, "Villa Altrove", MessageChannelStatuses.Available, null)], otherOrg);
        Assert.Empty(await hub.GetChannelStatusAsync(new HostScope(Guid.NewGuid())));

        // A listing the copy has not reached yet is available, with no instant; the answer to a client names no code of the channel manager.
        var json = JsonSerializer.Serialize(MessageChannelStatusDto.From(wholeOrg), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"status\":\"waiting_permission\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"error\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ACC-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("octorate_unavailable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chat_not_licensed", json, StringComparison.Ordinal);
    }

    /// <summary>A listing whose last pass worked, or that was never read, is available; one whose chat comes back (the error cleared) is available again.</summary>
    [Fact]
    public async Task ChannelStatus_AListingThatWorks_OrWasNeverRead_IsAvailable()
    {
        await AddListingAsync(_world.Scope.Granted.Id, "ACC-GRANTED", state: null);
        await AddListingAsync(_world.Scope.Hidden.Id, "ACC-HIDDEN", hasState: false);

        var statuses = await MessageHubTestKit.Service(_db).GetChannelStatusAsync(_world.Scope.OrgWide);

        Assert.All(statuses, s => Assert.Equal(MessageChannelStatuses.Available, s.Status));
        Assert.Equal(Checked, statuses.Single(s => s.PropertyId == _world.Scope.Granted.Id).LastCheckedAt);
        Assert.Null(statuses.Single(s => s.PropertyId == _world.Scope.Hidden.Id).LastCheckedAt);
    }

    /// <summary>
    /// The reply box explains instead of failing: a conversation of a listing whose chat is not in the license has no action, with the reason <c>chat_not_licensed</c> (the code the
    /// refusal of a send carries), and gets its actions back when the listing reads its chat again. A conversation of another listing keeps its own.
    /// </summary>
    [Fact]
    public async Task ThreadActions_ChatNotLicensed_ExplainsWhyTheReplyBoxIsClosed()
    {
        var hub = MessageHubTestKit.Service(_db, channelMapping: Mock.Of<IChannelMappingService>());
        var key = MessageThreadKey.ForOta(_world.GrantedReservation.Id);
        var open = await hub.GetThreadAsync(_world.Scope.OrgWide, key, includeAmounts: false);
        Assert.True(open.Actions.CanReply);
        Assert.Null(open.Actions.Reason);

        await AddListingAsync(_world.Scope.Granted.Id, "ACC-1", state: OtaMessageSyncErrorCodes.ChatNotLicensed);
        var blocked = await hub.GetThreadAsync(_world.Scope.OrgWide, key, includeAmounts: false);

        Assert.Equal(MessageThreadActions.Blocked(MessageThreadActions.ChatNotLicensedReason), blocked.Actions);
        Assert.Equal("chat_not_licensed", MessageThreadActions.ChatNotLicensedReason);

        // The listing reads its chat again.
        var state = await _db.OtaMessageSyncStates.SingleAsync(s => s.OctorateAccommodationId == "ACC-1");
        state.LastError = null;
        await _db.SaveChangesAsync();
        Assert.True((await hub.GetThreadAsync(_world.Scope.OrgWide, key, includeAmounts: false)).Actions.CanReply);
    }

    // ---- The endpoint ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The endpoint is a GET under <c>api/messages</c> with the policy of the list (booking read), no anonymous access, and an answer that is not cached.</summary>
    [Fact]
    public void ChannelStatus_IsAGetUnderApiMessages_WithTheSamePolicyAsTheList()
    {
        var type = typeof(HostMessagesController);
        var action = type.GetMethod(nameof(HostMessagesController.ChannelStatus))!;

        Assert.Equal(CasazenPolicies.BookingRead, Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>()).Policy);
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal("channel-status", action.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.NotNull(type.GetCustomAttribute<PrivateAnswerAttribute>());
    }

    /// <summary>The endpoint reads the scope of the caller and hands it to the hub as it is; a caller with no scope is a 403 and the hub is never asked.</summary>
    [Fact]
    public async Task ChannelStatus_UsesTheScopeOfTheCaller_AndRefusesACallerWithNone()
    {
        var orgId = _world.Scope.OrgId;
        var scope = _world.Scope.Restricted;
        var orgs = new Mock<IOrgContextResolver>();
        var scopes = new Mock<IHostScopeResolver>();
        var hub = new Mock<IMessageHubService>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        scopes.Setup(s => s.ResolveAsync("auth0|anna", It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        hub.Setup(h => h.GetChannelStatusAsync(scope, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MessageChannelStatus(_world.Scope.Granted.Id, "Trullo", MessageChannelStatuses.WaitingPermission, Checked)]);

        var ok = await Controller(orgs, scopes, hub).ChannelStatus(CancellationToken.None);

        var dto = Assert.IsType<MessageChannelStatusDto>(Assert.IsType<OkObjectResult>(ok.Result).Value);
        Assert.Equal(new MessageChannelStatusItemDto(_world.Scope.Granted.Id, "Trullo", "waiting_permission", Checked), Assert.Single(dto.Items));

        scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync((HostScope?)null);
        hub.Invocations.Clear();
        var forbidden = await Controller(orgs, scopes, hub).ChannelStatus(CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(forbidden.Result).StatusCode);
        hub.VerifyNoOtherCalls();
    }

    // ---- The data -------------------------------------------------------------------------------------------------------------------------

    private static HostMessagesController Controller(Mock<IOrgContextResolver> orgs, Mock<IHostScopeResolver> scopes, Mock<IMessageHubService> hub) =>
        new(Mock.Of<IGuestMessageService>(), orgs.Object, scopes.Object, hub.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|anna")], "test")),
                    RequestServices = new ServiceCollection()
                        .AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer())
                        .BuildServiceProvider(),
                },
            },
        };

    /// <summary>A mapping of a listing to a property, and the row of the copy of the conversations that says how the chat of the listing stands.</summary>
    private async Task AddListingAsync(
        Guid? propertyId,
        string accommodationId,
        string? state = null,
        bool hasState = true,
        Guid? orgId = null,
        OctorateMappingStatus status = OctorateMappingStatus.Confirmed)
    {
        var org = orgId ?? _world.Scope.OrgId;
        var connection = Guid.NewGuid();
        _db.OctorateListingMappings.Add(new OctorateListingMapping
        {
            OrgId = org,
            ConnectionId = connection,
            OctorateAccommodationId = accommodationId,
            PropertyId = propertyId,
            Status = status,
        });
        if (hasState && status == OctorateMappingStatus.Confirmed)
        {
            _db.OtaMessageSyncStates.Add(new OtaMessageSyncState
            {
                OrgId = org,
                ConnectionId = connection,
                PropertyId = propertyId,
                OctorateAccommodationId = accommodationId,
                LastError = state,
                UpdatedAt = Checked,
            });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
