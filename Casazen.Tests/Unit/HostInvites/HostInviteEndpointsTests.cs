using System.Reflection;
using Casazen.Core.HostInvites;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.HostInvites;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Casazen.Tests.Unit.HostInvites;

/// <summary>The authorization shape of the host invite endpoints: who may call what, read from the attributes.</summary>
public class HostInviteEndpointsTests
{
    [Fact]
    public void AdminHostInvitesController_IsAdminOnly_AndRoutedUnderAdmin()
    {
        var type = typeof(AdminHostInvitesController);

        Assert.Equal("api/admin/host-invites", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(CasazenPolicies.AdminOnly, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.DoesNotContain(type.GetMethods(), m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        Assert.All(Actions(type), m => Assert.Null(m.GetCustomAttribute<AuthorizeAttribute>()));
    }

    [Fact]
    public void HostInvitationsController_NeedsAHostRolePermissionNotJustASignIn_AndHasNoAnonymousAction()
    {
        var type = typeof(HostInvitationsController);

        Assert.Equal("api/hosts/me/invitations", type.GetCustomAttribute<RouteAttribute>()!.Template);
        var policy = type.GetCustomAttribute<AuthorizeAttribute>()!.Policy;
        Assert.Equal(CasazenPolicies.SharedPropertyRead, policy);
        Assert.NotEqual(CasazenPolicies.Authenticated, policy);
        Assert.NotEqual(CasazenPolicies.AdminOnly, policy);
        Assert.All(Actions(type), m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
    }

    [Fact]
    public void HostInvitationsController_ExposesCreateListQuotaResendRevoke()
    {
        var routes = Actions(typeof(HostInvitationsController))
            .Select(m => m.GetCustomAttributes<HttpMethodAttribute>().Single())
            .Select(a => $"{a.HttpMethods.Single()} {a.Template ?? string.Empty}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "DELETE {id:guid}", "GET ", "GET quota", "POST ", "POST {id:guid}/resend" }.Order(StringComparer.Ordinal),
            routes);
    }

    [Fact]
    public void HostInviteLinksController_LookupIsAnonymousAndRateLimited_AcceptRequiresSignIn()
    {
        var type = typeof(HostInviteLinksController);
        Assert.Equal("api/host-invites", type.GetCustomAttribute<RouteAttribute>()!.Template);

        foreach (var name in new[] { nameof(HostInviteLinksController.Lookup), nameof(HostInviteLinksController.LookupByBody) })
        {
            var action = type.GetMethod(name)!;
            Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Equal(RateLimitPolicies.PublicInvitationLookup, action.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        }

        var accept = type.GetMethod(nameof(HostInviteLinksController.Accept))!;
        Assert.Equal(CasazenPolicies.Authenticated, accept.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Null(accept.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void LookupResponse_NeverCarriesAFullEmailMember()
    {
        var members = typeof(HostInviteLookupResponse).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Email", members);
        Assert.Contains("MaskedEmail", members);
        Assert.Equal(new[] { "InviterName", "MaskedEmail", "State", "Valid" }, members.Order().ToArray());
    }

    [Fact]
    public void HostInviteDto_FromItem_MapsStateAsNameAndKeepsInviterOnlyWhenGiven()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var item = new HostInviteItem(Guid.NewGuid(), "a@example.com", HostInviteState.Expired, null, now, now, null, null);

        var dto = HostInviteDto.From(item);

        Assert.Equal("Expired", dto.State);
        Assert.Null(dto.InvitedBy);
    }

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().OfType<IActionHttpMethodProvider>().Any());
}
