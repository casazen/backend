using System.Data;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Infrastructure;
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
        Assert.Equal([new MessageChannelStatus(_world.Scope.OtherOrgProperty.Id, "Villa Altrove", MessageChannelStatuses.Available, Checked)], otherOrg);
        Assert.Empty(await hub.GetChannelStatusAsync(new HostScope(Guid.NewGuid())));

        // The answer to a client names no code of the channel manager.
        var json = JsonSerializer.Serialize(MessageChannelStatusDto.From(wholeOrg), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"status\":\"waiting_permission\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"error\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ACC-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("octorate_unavailable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chat_not_licensed", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelStatus_ReportsPendingUntilTheFirstPassCompletes(bool importInProgress)
    {
        await AddListingAsync(_world.Scope.Granted.Id, "ACC-GRANTED", state: null);
        await AddListingAsync(_world.Scope.Hidden.Id, "ACC-HIDDEN", hasState: importInProgress, passCompleted: false);

        var statuses = await MessageHubTestKit.Service(_db).GetChannelStatusAsync(_world.Scope.OrgWide);

        Assert.Contains(new MessageChannelStatus(_world.Scope.Granted.Id, "Trullo", MessageChannelStatuses.Available, Checked), statuses);
        Assert.Contains(new MessageChannelStatus(_world.Scope.Hidden.Id, "Casa Bianca", MessageChannelStatuses.PendingSync, null), statuses);
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

    /// <summary>
    /// The three statements of the channel status (the confirmed listings of the properties the scope reaches, their names, the sync states) are turned into SQL by the PostgreSQL
    /// provider for every kind of scope (the in-memory provider accepts what Npgsql cannot translate: see <see cref="NpgsqlTranslationProbe"/>), and the reach of a collaborator is
    /// applied in the statement itself (an <c>EXISTS</c> on the grants of the person), never on the rows after they were read.
    /// </summary>
    [Theory]
    [InlineData("whole-org")]
    [InlineData("collaborator")]
    [InlineData("collaborator-one-area")]
    [InlineData("account-in-no-team")]
    public async Task ChannelStatus_TranslatesToSqlOnPostgres_AndTheReachIsInTheStatement(string kind)
    {
        var orgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
        var scope = kind switch
        {
            "collaborator" => new HostScope(orgId, GrantedToUserId: "auth0|collaboratore"),
            "collaborator-one-area" => new HostScope(orgId, GrantedToUserId: "auth0|collaboratore", GrantedMode: RentalMode.Short),
            "account-in-no-team" => new HostScope(orgId, OwnerId: "auth0|titolare"),
            _ => new HostScope(orgId),
        };
        var statements = new List<string>();

        // The first statement answers one confirmed listing, so that the two that follow it (the names, the sync states) are sent as well.
        static DataTable? OneListing(string sql)
        {
            if (!sql.Contains("OctorateListingMappings", StringComparison.Ordinal))
                return null;

            var table = new DataTable();
            table.Columns.Add("PropertyId", typeof(Guid));
            table.Columns.Add("OctorateAccommodationId", typeof(string));
            table.Rows.Add(Guid.Parse("11111111-2222-4333-8444-555555555555"), "ACC-1");
            return table;
        }

        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: OneListing);
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => MessageHubTestKit.Service(db).GetChannelStatusAsync(scope));

        Assert.Equal(3, statements.Count);
        Assert.Contains("OctorateListingMappings", statements[0], StringComparison.Ordinal);
        Assert.Contains("OtaMessageSyncStates", statements[2], StringComparison.Ordinal);
        Assert.All(statements, sql => Assert.DoesNotContain("\"Text\"", sql, StringComparison.Ordinal));
        if (kind.StartsWith("collaborator", StringComparison.Ordinal))
            Assert.Contains("PropertyMemberAccesses", statements[0], StringComparison.Ordinal);
        else if (kind == "whole-org")
            Assert.DoesNotContain("PropertyMemberAccesses", statements[0], StringComparison.Ordinal);
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
        OctorateMappingStatus status = OctorateMappingStatus.Confirmed,
        bool passCompleted = true)
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
                LastRecentPassAt = passCompleted && state is null ? Checked : null,
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
