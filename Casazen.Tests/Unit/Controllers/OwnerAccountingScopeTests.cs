using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E06/F05/US02 (NF-110, recurring rule of the portata): the owner accounting spans the properties of the owners, so a caller whose reach is restricted to some
/// properties (a collaborator «Solo alcuni», an account outside the org team) is refused on EVERY action of its controllers, reads included, before anything is
/// read or written; a caller who reaches the whole org is served.
/// </summary>
public class OwnerAccountingScopeTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private const string UserId = "auth0|collaborator-solo-alcuni";

    [Fact]
    public async Task OwnerAccounting_CallerWithoutAnOrgWideReach_GetsForbiddenOnEveryAction_Test()
    {
        foreach (var restricted in new[] { new HostScope(OrgId, GrantedToUserId: UserId), new HostScope(OrgId, OwnerId: UserId) })
        {
            var accounting = new Mock<IOwnerAccountingService>(MockBehavior.Strict);
            var payouts = new Mock<IOwnerPayoutService>(MockBehavior.Strict);
            var withholding = new Mock<IWithholdingService>(MockBehavior.Strict);
            var exports = new Mock<IStatementExportService>(MockBehavior.Strict);
            await using var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerAccountingScope_{Guid.NewGuid()}").Options, NullTenantContext.Instance);

            var checkedActions = 0;
            checkedActions += await AssertEveryActionForbiddenAsync(new OwnerAccountingController(accounting.Object, Org(), Scope(restricted), Mock.Of<IAuthorizationService>()));
            checkedActions += await AssertEveryActionForbiddenAsync(new OwnerPayoutsController(payouts.Object, Org(), Scope(restricted)));

            // E18/F01/US01/T02: the withholding of every owner (the F24 data and its export, the annual data, the filing states) needs the whole org too.
            checkedActions += await AssertEveryActionForbiddenAsync(new WithholdingController(
                db, withholding.Object, payouts.Object, exports.Object, Org(), Scope(restricted), Mock.Of<IDocumentTexts>(), NullLogger<WithholdingController>.Instance));

            // Every HTTP action of the three controllers was called (the reads and the writes), and none reached a service (strict mocks would have thrown).
            Assert.True(checkedActions >= 30, $"only {checkedActions} actions were checked");
            accounting.VerifyNoOtherCalls();
            payouts.VerifyNoOtherCalls();
            withholding.VerifyNoOtherCalls();
            exports.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task OwnerAccounting_CallerWithTheWholeOrg_IsServed_Test()
    {
        var accounting = new Mock<IOwnerAccountingService>();
        accounting.Setup(a => a.GetReadinessAsync(OrgId, 2026, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerCloseReadiness(2026, 10, [], []));
        var controller = new OwnerAccountingController(accounting.Object, Org(), Scope(new HostScope(OrgId)), Mock.Of<IAuthorizationService>());
        controller.ControllerContext = Context();

        var result = await controller.Readiness(2026, 10);

        Assert.IsType<OkObjectResult>(result.Result);
        accounting.Verify(a => a.GetReadinessAsync(OrgId, 2026, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Permission (E18/F01/US01, review of S029): an approved rule set decides the withholding of every owner whose profile points at it, the seeded Italian draft
    /// included, so a caller restricted to some properties is refused the approval before anything is read or written (403); a caller of the whole org approves it.
    /// </summary>
    [Fact]
    public async Task ApprovePolicy_CallerWithoutAnOrgWideReach_GetsForbidden_Test()
    {
        var policyId = Guid.NewGuid();
        var request = new ApproveOwnerPolicyRequest { ApprovedByName = "TEST Commercialista", SourceSha256 = new string('c', 64) };
        foreach (var restricted in new[] { new HostScope(OrgId, GrantedToUserId: UserId), new HostScope(OrgId, OwnerId: UserId) })
        {
            var profiles = new Mock<IOwnerFiscalProfileService>(MockBehavior.Strict);
            var controller = new OwnerFiscalProfilesController(
                profiles.Object, Org(), Mock.Of<IAuthorizationService>(), Scope(restricted), NullLogger<OwnerFiscalProfilesController>.Instance);
            controller.ControllerContext = Context();

            var refused = await controller.ApprovePolicy(policyId, request);

            Assert.IsType<ForbidResult>(refused.Result);
            profiles.VerifyNoOtherCalls();
        }

        var approver = new Mock<IOwnerFiscalProfileService>();
        approver.Setup(p => p.ApprovePolicyAsync(OrgId, policyId, "TEST Commercialista", UserId, request.SourceSha256, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OwnerAccountingPolicy { OrgId = OrgId, Jurisdiction = "IT", RuleCode = "IT-STR-WITHHOLDING", Version = "2026.1" });
        var wholeOrg = new OwnerFiscalProfilesController(
            approver.Object, Org(), Mock.Of<IAuthorizationService>(), Scope(new HostScope(OrgId)), NullLogger<OwnerFiscalProfilesController>.Instance);
        wholeOrg.ControllerContext = Context();

        var served = await wholeOrg.ApprovePolicy(policyId, request);

        Assert.IsType<OkObjectResult>(served.Result);
        approver.Verify(p => p.ApprovePolicyAsync(OrgId, policyId, "TEST Commercialista", UserId, request.SourceSha256, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Calls every HTTP action of the controller with empty arguments (a refusal needs none) and checks the answer is a 403.</summary>
    private static async Task<int> AssertEveryActionForbiddenAsync(ControllerBase controller)
    {
        controller.ControllerContext = Context();
        var actions = controller.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute))
            .ToList();
        Assert.NotEmpty(actions);

        foreach (var action in actions)
        {
            var arguments = action.GetParameters()
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            var task = (Task)action.Invoke(controller, arguments)!;
            await task;

            var answer = task.GetType().GetProperty("Result")!.GetValue(task);
            if (answer is not null && answer.GetType().IsGenericType && answer.GetType().GetGenericTypeDefinition() == typeof(ActionResult<>))
                answer = answer.GetType().GetProperty("Result")!.GetValue(answer);

            Assert.True(answer is ForbidResult, $"{controller.GetType().Name}.{action.Name} did not refuse a caller without an org-wide reach");
        }

        return actions.Count;
    }

    private static ControllerContext Context()
    {
        var identity = new ClaimsIdentity([new Claim("sub", UserId), new Claim(ClaimTypes.NameIdentifier, UserId)], "test");
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }

    private static IOrgContextResolver Org()
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        return resolver.Object;
    }

    private static IHostScopeResolver Scope(HostScope scope)
    {
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return resolver.Object;
    }
}
