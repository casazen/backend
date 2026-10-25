using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// ADR-010-R2: agency.* policies require OrgEntity.PlanTier == PlanTier.Agency. Non-agency orgs
/// cannot access B2B endpoints (permission proof test).
/// </summary>
public class AgencyTierAuthorizationHandlerTests
{
    private const string Sub = "auth0|agency-tier-test";

    [Theory]
    [InlineData(PlanTier.Starter, false)]
    [InlineData(PlanTier.Pro, false)]
    [InlineData(PlanTier.Scale, false)]
    [InlineData(PlanTier.Agency, true)]
    public async Task AgencyTierPolicy_NonAgencyOrgEntity_Returns403(PlanTier tier, bool shouldSucceed)
    {
        // Arrange
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            PlanTier = tier,
            IsAgencyTierDowngraded = false
        };

        var handler = CreateHandler(org, "GET");
        var context = CreateContext();

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.Equal(shouldSucceed, context.HasSucceeded);
        if (!shouldSucceed)
        {
            Assert.Contains(context.FailureReasons,
                r => r.Message == "feature_requires_agency_tier");
        }
    }

    [Theory]
    [InlineData("GET", true)]    // Reads succeed even when downgraded
    [InlineData("POST", false)]  // Mutations fail when downgraded
    [InlineData("PUT", false)]
    [InlineData("PATCH", false)]
    [InlineData("DELETE", false)]
    public async Task AgencyTierPolicy_DowngradedAgency_MutationsFail_ReadsSucceed(
        string httpMethod,
        bool shouldSucceed)
    {
        // Arrange: Agency tier but downgraded
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            PlanTier = PlanTier.Agency,
            IsAgencyTierDowngraded = true
        };

        var handler = CreateHandler(org, httpMethod);
        var context = CreateContext();

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.Equal(shouldSucceed, context.HasSucceeded);
        if (!shouldSucceed)
        {
            Assert.Contains(context.FailureReasons,
                r => r.Message == "tier_insufficient");
        }
    }

    [Fact]
    public async Task AgencyTierPolicy_AgencyNotDowngraded_AllOperationsSucceed()
    {
        // Arrange: Agency tier, not downgraded
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            PlanTier = PlanTier.Agency,
            IsAgencyTierDowngraded = false
        };

        var handler = CreateHandler(org, "POST");
        var context = CreateContext();

        // Act
        await handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
        Assert.Empty(context.FailureReasons);
    }

    private static AgencyTierAuthorizationHandler CreateHandler(OrgEntity org, string httpMethod)
    {
        var orgId = org.Id;

        var orgContextResolver = new Mock<IOrgContextResolver>();
        orgContextResolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(orgId);

        var orgService = new Mock<IOrgService>();
        orgService.Setup(s => s.GetByIdAsync(orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = httpMethod;

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);

        return new AgencyTierAuthorizationHandler(
            orgContextResolver.Object,
            orgService.Object,
            httpContextAccessor.Object);
    }

    private static AuthorizationHandlerContext CreateContext()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", Sub),
                new Claim(ClaimTypes.NameIdentifier, Sub),
            ],
            "TestAuthType"));

        return new AuthorizationHandlerContext(
            [new AgencyTierRequirement()],
            user,
            null);
    }
}
