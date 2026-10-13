using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Search;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// UI-13a: which groups of the global search the caller may read. The decision is the one of the policies of the endpoints that
/// list the same objects, evaluated for the caller; the org and the reach on its properties come from the same resolvers as
/// everywhere (never the token). A caller with no host permission does not even look up its org.
/// </summary>
public class SearchAccessResolverTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid SupplierOrgId = Guid.Parse("0b6d6a3e-3d1f-4f0a-9d7e-5b0c4f2a1e22");

    private readonly Mock<IAuthorizationService> _authorization = new();
    private readonly Mock<IOrgContextResolver> _orgs = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();
    private readonly Mock<ISupplierOrgContextResolver> _suppliers = new();
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim("sub", "auth0|someone")], "Test"));
    private readonly List<string> _asked = [];

    private SearchAccessResolver NewResolver(IEnumerable<string> allowedPolicies, HostScope? scope = null, Guid? linkedSupplier = null)
    {
        var allowed = allowedPolicies.ToHashSet(StringComparer.Ordinal);
        _authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .Returns((ClaimsPrincipal _, object? _, string policy) =>
            {
                _asked.Add(policy);
                return Task.FromResult(allowed.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
            });
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        _scopes
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
        _suppliers.Setup(s => s.GetLinkedSupplierOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(linkedSupplier);
        return new SearchAccessResolver(_authorization.Object, _orgs.Object, _scopes.Object, _suppliers.Object);
    }

    private static readonly string[] EveryHostPolicy =
    [
        CasazenPolicies.PropertyRead, CasazenPolicies.LongRentPropertyRead, CasazenPolicies.BookingRead,
        CasazenPolicies.GuestRead, CasazenPolicies.LeaseRead,
    ];

    [Fact]
    public async Task Resolve_EveryPermission_GivesEveryGroupWithTheScopeOfTheDatabase()
    {
        var scope = new HostScope(OrgId);
        var resolver = NewResolver([.. EveryHostPolicy, CasazenPolicies.Supplier], scope, SupplierOrgId);

        var access = await resolver.ResolveAsync(_user);

        Assert.Equal(new HostSearchAccess(scope, true, true, true, true, true), access.Host);
        Assert.Equal(SupplierOrgId, access.SupplierOrgId);
        Assert.False(access.IsEmpty);
    }

    [Theory]
    [InlineData(nameof(CasazenPolicies.PropertyRead), true, false, false, false, false)]
    [InlineData(nameof(CasazenPolicies.LongRentPropertyRead), false, true, false, false, false)]
    [InlineData(nameof(CasazenPolicies.BookingRead), false, false, true, false, false)]
    [InlineData(nameof(CasazenPolicies.GuestRead), false, false, false, true, false)]
    [InlineData(nameof(CasazenPolicies.LeaseRead), false, false, false, false, true)]
    public async Task Resolve_OnePermission_GivesOnlyItsGroup(
        string policyField, bool shortRent, bool longRent, bool bookings, bool guests, bool leases)
    {
        var policy = (string)typeof(CasazenPolicies).GetField(policyField)!.GetRawConstantValue()!;
        var scope = new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore");
        var resolver = NewResolver([policy], scope);

        var access = await resolver.ResolveAsync(_user);

        Assert.Equal(new HostSearchAccess(scope, shortRent, longRent, bookings, guests, leases), access.Host);
        Assert.Null(access.SupplierOrgId);
    }

    [Fact]
    public async Task Resolve_TheSameMoreSpecificPoliciesAsTheEndpoints_AreTheOnesAsked()
    {
        var resolver = NewResolver([]);

        await resolver.ResolveAsync(_user);

        // Property.read of each rental context, booking.read, guest.read, lease.read, and the supplier role: nothing else decides.
        Assert.Equal(
            EveryHostPolicy.Append(CasazenPolicies.Supplier).Order(StringComparer.Ordinal),
            _asked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Resolve_NoHostPermission_DoesNotLookUpTheOrg_AndHasNoHostAccess()
    {
        var resolver = NewResolver([CasazenPolicies.Supplier], linkedSupplier: SupplierOrgId);

        var access = await resolver.ResolveAsync(_user);

        Assert.Null(access.Host);
        Assert.Equal(SupplierOrgId, access.SupplierOrgId);
        _orgs.Verify(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()), Times.Never);
        _scopes.Verify(
            s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Resolve_APermissionWithoutAReachOnTheOrg_GivesNoHostAccess_NeverTheWholeOrg()
    {
        // The member was deactivated, or belongs to another org: the resolver answers null and the search reads no host group.
        var resolver = NewResolver([.. EveryHostPolicy], scope: null);

        var access = await resolver.ResolveAsync(_user);

        Assert.Null(access.Host);
        Assert.True(access.IsEmpty);
    }

    [Fact]
    public async Task Resolve_APermissionWithoutAnOrg_GivesNoHostAccess()
    {
        var resolver = NewResolver([.. EveryHostPolicy], new HostScope(OrgId));
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);

        var access = await resolver.ResolveAsync(_user);

        Assert.Null(access.Host);
        _scopes.Verify(
            s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Resolve_TheSupplierRoleWithoutALinkedSupplierOrg_HasNoInbox()
    {
        var resolver = NewResolver([CasazenPolicies.Supplier], linkedSupplier: null);

        var access = await resolver.ResolveAsync(_user);

        Assert.Null(access.SupplierOrgId);
        Assert.True(access.IsEmpty);
    }

    [Fact]
    public async Task Resolve_NoSupplierRole_DoesNotLookUpTheSupplierOrg()
    {
        var resolver = NewResolver([.. EveryHostPolicy], new HostScope(OrgId), linkedSupplier: SupplierOrgId);

        var access = await resolver.ResolveAsync(_user);

        Assert.Null(access.SupplierOrgId);
        _suppliers.Verify(s => s.GetLinkedSupplierOrgIdAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Resolve_TheReachIsAskedWithTheRolesOfTheToken_OnlyForTheOrgOfTheCaller()
    {
        var scope = new HostScope(OrgId);
        var resolver = NewResolver([CasazenPolicies.BookingRead], scope);
        var withRole = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "auth0|owner"), new Claim(ClaimTypes.Role, "PropertyOwner")], "Test"));

        await resolver.ResolveAsync(withRole);

        _scopes.Verify(
            s => s.ResolveAsync("auth0|owner", It.Is<IReadOnlySet<string>>(roles => roles.Contains("PropertyOwner")), OrgId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Resolve_NoUser_IsRefused()
    {
        var resolver = NewResolver([]);

        await Assert.ThrowsAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!));
    }
}
