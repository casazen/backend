using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The check «Fatto» runs on the panel step of a «Do it for me» request (E20/F03/US01/T02), with the web layer's own <see cref="TenantContext"/> and the
/// real <see cref="OctorateOrgScope"/>, so the one thing <see cref="OrgScopedPanelVerification"/> exists for is exercised and not assumed: a platform
/// administrator works from a request whose tenant is its OWN org, where a call to the channel manager for another org is refused by design; the
/// verifier of the panel step must still read for the org of the request. Only the channel manager (the police account it answers) and the reader of
/// the mapping (which says what tenant it ran under) are fakes.
/// </summary>
public class OrgScopedPanelVerificationTests
{
    private static readonly Guid StaffOrgId = Guid.Parse("5a1f0c52-3b7e-4d49-9e0a-6c2d8f1b7a31");
    private static readonly Guid RequestOrgId = Guid.Parse("c3e9a7b4-61d2-4f58-8a1b-0d4e92f6b573");
    private static readonly Guid PropertyId = Guid.Parse("8b2d4f6a-0c1e-4a37-b5d9-7e3f1a9c2d84");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly List<(bool FilterEnabled, Guid? OrgId)> _tenantsOfTheMappingReads = [];
    private readonly List<Guid> _orgsOfTheCallsToTheChannelManager = [];

    /// <summary>
    /// The verifier reads the mapping and asks the channel manager for the org of the request, from the request of a staff user of another org. The
    /// control: the same verifier in the scope of the staff user's own request reads under the staff org and is refused by the guard of the channel
    /// manager's scope, before any call is made. The staff user's own context is untouched by the verification.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_FromTheRequestOfAStaffUserOfAnotherOrg_ReadsAndCallsTheChannelManagerForTheOrgOfTheRequest()
    {
        await using var provider = BuildProvider();
        await using var staffRequest = provider.CreateAsyncScope();
        var staffTenant = staffRequest.ServiceProvider.GetRequiredService<TenantContext>();
        staffTenant.SetOrgId(StaffOrgId);
        var context = new PanelStepContext(RequestOrgId, PropertyId, PanelStepKind.PoliceConnection, null);

        // The control: in the scope of the staff user's own request a call for the org of the request is refused, and nothing was asked of the channel manager.
        var inTheRequestOwnScope = staffRequest.ServiceProvider.GetServices<IPanelStepVerifier>().Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => inTheRequestOwnScope.VerifyAsync(context, Ct));
        Assert.Empty(_orgsOfTheCallsToTheChannelManager);

        // The verification of «Fatto»: a scope of its own, put under the org of the request.
        var verification = new OrgScopedPanelVerification(staffRequest.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
        var outcome = await verification.VerifyAsync(context, Ct);

        Assert.Equal(PanelVerificationOutcome.Verified, outcome);
        Assert.Equal(new[] { RequestOrgId }, _orgsOfTheCallsToTheChannelManager.ToArray());

        // The reads of the mapping: the first (the control) ran under the staff org, the one of the verification under the org of the request, with the
        // tenant filter on in both.
        Assert.Equal(2, _tenantsOfTheMappingReads.Count);
        Assert.Equal((true, (Guid?)StaffOrgId), _tenantsOfTheMappingReads[0]);
        Assert.Equal((true, (Guid?)RequestOrgId), _tenantsOfTheMappingReads[1]);
        Assert.Equal(StaffOrgId, staffTenant.OrgId);
    }

    /// <summary>Each verification gets a scope of its own: two requests of two orgs, one after the other, never share the tenant (a scope is put under one org once).</summary>
    [Fact]
    public async Task VerifyAsync_TwoOrgsOneAfterTheOther_EachRunsUnderItsOwnOrg()
    {
        await using var provider = BuildProvider();
        await using var staffRequest = provider.CreateAsyncScope();
        staffRequest.ServiceProvider.GetRequiredService<TenantContext>().SetOrgId(StaffOrgId);
        var verification = new OrgScopedPanelVerification(staffRequest.ServiceProvider.GetRequiredService<IServiceScopeFactory>());
        var otherOrgId = Guid.Parse("e7a4c1d8-92b3-4f06-a5e1-3b8d6c0f4a92");

        await verification.VerifyAsync(new PanelStepContext(RequestOrgId, PropertyId, PanelStepKind.PoliceConnection, null), Ct);
        await verification.VerifyAsync(new PanelStepContext(otherOrgId, PropertyId, PanelStepKind.PoliceConnection, null), Ct);

        Assert.Equal(new[] { RequestOrgId, otherOrgId }, _orgsOfTheCallsToTheChannelManager.ToArray());
        Assert.Equal((true, (Guid?)RequestOrgId), _tenantsOfTheMappingReads[0]);
        Assert.Equal((true, (Guid?)otherOrgId), _tenantsOfTheMappingReads[1]);
    }

    /// <summary>
    /// The web layer as it is: the request of a staff user is authenticated (so the tenant filter is on and the channel manager's scope refuses another
    /// org), the tenant context is the web layer's own and answers both <see cref="ITenantContext"/> and <see cref="IBackgroundTenantScope"/> for the one
    /// instance of its scope, as in <c>ServiceCollectionExtensions</c>.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|staff-support")], "Test")) },
        });
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IBackgroundTenantScope>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IOctorateOrgScope, OctorateOrgScope>();
        services.AddScoped<IPropertyMappingReader>(sp => new TenantRecordingMappings(sp.GetRequiredService<ITenantContext>(), _tenantsOfTheMappingReads));
        services.AddScoped(sp =>
        {
            var orgScope = sp.GetRequiredService<IOctorateOrgScope>();
            var client = new Mock<IOctorateApiClient>();
            client
                .Setup(c => c.GetPoliceAccountStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => _orgsOfTheCallsToTheChannelManager.Add(orgScope.OrgId))
                .ReturnsAsync(new OctoratePoliceAccountStatus(true, true, true, null));
            return client.Object;
        });
        services.AddScoped<IPanelStepVerifier, PoliceConnectionPanelVerifier>();
        return services.BuildServiceProvider();
    }

    /// <summary>Every property is confirmed on an accommodation of its own; it keeps the tenant it was asked under.</summary>
    private sealed class TenantRecordingMappings(ITenantContext tenant, List<(bool FilterEnabled, Guid? OrgId)> seen) : IPropertyMappingReader
    {
        public bool IsProvided => true;

        public Task<IReadOnlyDictionary<Guid, string>> GetConfirmedAccommodationIdsAsync(
            Guid orgId,
            IReadOnlyCollection<Guid> propertyIds,
            CancellationToken ct)
        {
            seen.Add((tenant.FilterEnabled, tenant.OrgId));
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(propertyIds.ToDictionary(id => id, id => $"ACC-TEST-{id:N}"));
        }
    }
}
