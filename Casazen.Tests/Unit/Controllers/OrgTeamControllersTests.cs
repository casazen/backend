using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// AM-02: the endpoints of the owner and the administrators. The org and the account are the caller's own (never from the
/// request), a caller with no org gets 404, every call is passed to the service as it came, the link is never cached.
/// </summary>
public class OrgTeamControllersTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime Now = new(2026, 10, 8, 10, 30, 0, DateTimeKind.Utc);

    private readonly Mock<IOrgInvitationService> _invitations = new();
    private readonly Mock<IOrgTeamService> _team = new();
    private readonly Mock<IOrgContextResolver> _org = new();

    public OrgTeamControllersTests()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
    }

    private static T WithContext<T>(T controller, params Claim[] claims)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        return controller;
    }

    private OrgInvitationsController Invitations(string? sub = "auth0|owner") =>
        WithContext(new OrgInvitationsController(_invitations.Object, _org.Object), sub is null ? [] : [new Claim("sub", sub)]);

    private OrgMembersController Members(string? sub = "auth0|owner") =>
        WithContext(new OrgMembersController(_team.Object, _org.Object), sub is null ? [] : [new Claim("sub", sub)]);

    private static OrgInvitationView View(OrgInvitationStatus status = OrgInvitationStatus.Pending) => new(
        ItemId, "anna.leone@example.com", "Anna Leone", OrgRole.Collaborator, ["short-rent"], PropertyScope.All,
        status, Now, Now.AddDays(7), null, "auth0|owner");

    private static OrgMemberView MemberView(OrgRole role = OrgRole.Collaborator) => new(
        ItemId, "auth0|anna", "anna.leone@example.com", "Anna", "Leone", role, OrgMemberStatus.Active,
        PropertyScope.All, ["short-rent"], Now, null);

    // ─── Invitations ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvitation_PassesTheRequestWithTheCallersOrgAndAccount_Answers201()
    {
        CreateOrgInvitation? received = null;
        _invitations.Setup(i => i.CreateAsync(It.IsAny<CreateOrgInvitation>(), It.IsAny<CancellationToken>()))
            .Callback<CreateOrgInvitation, CancellationToken>((r, _) => received = r)
            .ReturnsAsync(new OrgInvitationSent(View(), true));

        var result = await Invitations().Create(
            new CreateOrgInvitationRequest
            {
                Email = "anna.leone@example.com",
                Name = "Anna Leone",
                Role = OrgRole.Collaborator,
                Areas = ["short-rent"],
                PropertyScope = PropertyScope.Selected,
                Language = "en",
            },
            CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        var dto = Assert.IsType<OrgInvitationSentDto>(created.Value);
        Assert.True(dto.EmailQueued);
        Assert.Equal(ItemId, dto.Invitation.Id);
        Assert.NotNull(received);
        Assert.Equal((OrgId, "auth0|owner", "anna.leone@example.com", "Anna Leone", OrgRole.Collaborator, PropertyScope.Selected, "en"),
            (received.OrgId, received.ActorUserId, received.Email, received.Name, received.Role, received.PropertyScope, received.Language));
        Assert.Equal(["short-rent"], received.Areas);
    }

    [Fact]
    public async Task EveryInvitationAction_ACallerWithNoOrg_Gets404NoOrganization()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var controller = Invitations();

        var created = await controller.Create(new CreateOrgInvitationRequest { Email = "a@example.com", Name = "A", Areas = ["short-rent"] }, CancellationToken.None);
        var list = await controller.List(CancellationToken.None);
        var resend = await controller.Resend(ItemId, CancellationToken.None);
        var revoke = await controller.Revoke(ItemId, CancellationToken.None);
        var link = await controller.CopyLink(ItemId, CancellationToken.None);

        foreach (var result in new[] { created.Result, list.Result, resend.Result, revoke, link.Result })
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result).StatusCode);
        _invitations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EveryInvitationAction_ACallerWithNoAccount_Gets401()
    {
        var controller = Invitations(sub: null);

        var list = await controller.List(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(list.Result);
        _invitations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListInvitations_ReturnsTheOrgsInvitationsWithoutAnyToken()
    {
        _invitations.Setup(i => i.ListAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync([View(), View(OrgInvitationStatus.Expired)]);

        var result = await Invitations().List(CancellationToken.None);

        var items = Assert.IsAssignableFrom<IReadOnlyList<OrgInvitationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([OrgInvitationStatus.Pending, OrgInvitationStatus.Expired], items.Select(i => i.Status));
        Assert.DoesNotContain(typeof(OrgInvitationDto).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(OrgInvitationDto).GetProperties(), p => p.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResendInvitation_PassesTheCallersOrgAndAccount()
    {
        _invitations.Setup(i => i.ResendAsync(OrgId, ItemId, "auth0|owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgInvitationSent(View(), false));

        var result = await Invitations().Resend(ItemId, CancellationToken.None);

        var dto = Assert.IsType<OrgInvitationSentDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.EmailQueued);
    }

    [Fact]
    public async Task RevokeInvitation_Answers204()
    {
        var result = await Invitations().Revoke(ItemId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _invitations.Verify(i => i.RevokeAsync(OrgId, ItemId, "auth0|owner", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CopyInvitationLink_IsNeverCached()
    {
        _invitations.Setup(i => i.CopyLinkAsync(OrgId, ItemId, "auth0|owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgInvitationLink(ItemId, "https://casazen-app.test/invite/accept?token=abc", Now.AddDays(7)));
        var controller = Invitations();

        var result = await controller.CopyLink(ItemId, CancellationToken.None);

        var dto = Assert.IsType<OrgInvitationLinkDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(("https://casazen-app.test/invite/accept?token=abc", Now.AddDays(7)), (dto.Url, dto.ExpiresAt));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    // ─── Members ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListMembers_ReturnsThePeopleAndTheSeats()
    {
        _team.Setup(t => t.ListAsync(OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgTeamView([MemberView(OrgRole.Owner), MemberView()], new OrgSeatUsage(10, 2, 1)));

        var result = await Members().List(CancellationToken.None);

        var response = Assert.IsType<OrgMembersResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([OrgRole.Owner, OrgRole.Collaborator], response.Items.Select(m => m.Role));
        Assert.Equal((10, 3, 7, 2, 1, false, true), (response.Seats.Max, response.Seats.Used, response.Seats.Available, response.Seats.ActiveMembers, response.Seats.PendingInvitations, response.Seats.Unlimited, response.Seats.CanInvite));
    }

    [Fact]
    public async Task ListMembers_AnUnlimitedPlan_SaysSo()
    {
        _team.Setup(t => t.ListAsync(OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgTeamView([MemberView(OrgRole.Owner)], new OrgSeatUsage(int.MaxValue, 40, 3)));

        var result = await Members().List(CancellationToken.None);

        var seats = Assert.IsType<OrgMembersResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Seats;
        Assert.True(seats.Unlimited);
        Assert.Equal(int.MaxValue, seats.Max);
        Assert.Equal(43, seats.Used);
    }

    [Fact]
    public async Task ChangeMemberRole_PassesTheRoleAndTheCallersAccount()
    {
        _team.Setup(t => t.ChangeRoleAsync(OrgId, ItemId, OrgRole.Accountant, "auth0|owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MemberView(OrgRole.Accountant));

        var result = await Members().ChangeRole(ItemId, new ChangeOrgMemberRoleRequest { Role = OrgRole.Accountant }, CancellationToken.None);

        Assert.Equal(OrgRole.Accountant, Assert.IsType<OrgMemberDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Role);
    }

    [Fact]
    public async Task DeactivateReactivateAndRemove_PassTheCallersOrgAndAccount()
    {
        _team.Setup(t => t.DeactivateAsync(OrgId, ItemId, "auth0|owner", It.IsAny<CancellationToken>())).ReturnsAsync(MemberView());
        _team.Setup(t => t.ReactivateAsync(OrgId, ItemId, "auth0|owner", It.IsAny<CancellationToken>())).ReturnsAsync(MemberView());
        var controller = Members();

        Assert.IsType<OkObjectResult>((await controller.Deactivate(ItemId, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Reactivate(ItemId, CancellationToken.None)).Result);
        Assert.IsType<NoContentResult>(await controller.Remove(ItemId, CancellationToken.None));

        _team.Verify(t => t.RemoveAsync(OrgId, ItemId, "auth0|owner", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EveryMemberAction_ACallerWithNoOrg_Gets404()
    {
        _org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var controller = Members();

        var list = await controller.List(CancellationToken.None);
        var change = await controller.ChangeRole(ItemId, new ChangeOrgMemberRoleRequest(), CancellationToken.None);
        var deactivate = await controller.Deactivate(ItemId, CancellationToken.None);
        var reactivate = await controller.Reactivate(ItemId, CancellationToken.None);
        var remove = await controller.Remove(ItemId, CancellationToken.None);

        foreach (var result in new[] { list.Result, change.Result, deactivate.Result, reactivate.Result, remove })
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result).StatusCode);
        _team.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dtos_NeverCarryTheTokenOrItsHash_ExceptTheLinkTheManagerAskedFor()
    {
        foreach (var type in new[] { typeof(OrgInvitationDto), typeof(OrgInvitationSentDto), typeof(OrgMemberDto), typeof(OrgMembersResponse), typeof(OrgInvitationLookupResponse), typeof(AcceptOrgInvitationResponse) })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase));
        }
    }
}
