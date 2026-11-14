using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E14/F01/US01/T01 (NF-114, recurring rule of the portata, as <see cref="OwnerAccountingScopeTests"/>): the owner invoices and the portal accounts span the
/// properties of the owners and of the organization, so a caller whose reach is restricted to some properties (a collaborator «Solo alcuni», an account outside the
/// org team) is refused on EVERY new action, reads included, before anything is read or written; a caller who reaches the whole org is served.
/// </summary>
public class OwnerInvoicesScopeTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private const string UserId = "auth0|collaborator-solo-alcuni";

    [Fact]
    public async Task OwnerInvoices_CallerWithoutAnOrgWideReach_GetsForbiddenOnEveryAction_Test()
    {
        foreach (var restricted in Restricted())
        {
            var invoices = new Mock<IOwnerInvoiceService>(MockBehavior.Strict);

            var actions = await AssertEveryActionForbiddenAsync(new OwnerInvoicesController(invoices.Object, Org(), Scope(restricted)));

            // The list, the draft, the issue, the credit note and the discard: each refused before the service was asked anything (a strict mock would have thrown).
            Assert.Equal(5, actions);
            invoices.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task AgencyInvoicesGenerate_CallerWithoutAnOrgWideReach_GetsForbidden_Test()
    {
        foreach (var restricted in Restricted())
        {
            var invoices = new Mock<IOwnerInvoiceService>(MockBehavior.Strict);
            var controller = new AgencyInvoicesController(
                Mock.Of<IAgencyInvoiceService>(), invoices.Object, Org(), Scope(restricted), NullLogger<AgencyInvoicesController>.Instance)
            {
                ControllerContext = Context(),
            };

            var answer = await controller.GenerateInvoice(new GenerateOwnerInvoicesRequest { Year = 2026, Month = 10 });

            Assert.IsType<ForbidResult>(answer.Result);
            invoices.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task PortalAccounts_CallerWithoutAnOrgWideReach_GetsForbidden_AndNothingIsReadOrWritten_Test()
    {
        foreach (var restricted in Restricted())
        {
            var billing = new Mock<IBillingSetupService>(MockBehavior.Strict);
            var portalAccounts = new Mock<IPortalAccountSettingsService>(MockBehavior.Strict);
            var controller = new BillingSetupController(billing.Object, Org(), Scope(restricted), NullLogger<BillingSetupController>.Instance, portalAccounts.Object)
            {
                ControllerContext = Context(),
            };
            var holders = new List<ChannelAccountHolderCommand>
            {
                new() { PropertyId = Guid.NewGuid(), Channel = BookingSource.BookingCom, Holder = PortalAccountHolder.Agency },
            };

            var defaults = await controller.PutChannels(new PortalAccountSettingsCommand { PortalAccountHolder = PortalAccountHolder.Agency }, CancellationToken.None);
            var list = await controller.GetAccountHolders(CancellationToken.None);
            var write = await controller.PutAccountHolders(holders, CancellationToken.None);

            Assert.Equal(
                (StatusCodes.Status403Forbidden, StatusCodes.Status403Forbidden, StatusCodes.Status403Forbidden),
                (StatusOf(defaults.Result), StatusOf(list.Result), StatusOf(write.Result)));
            billing.VerifyNoOtherCalls();
            portalAccounts.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public async Task OwnerInvoicesAndPortalAccounts_CallerWithTheWholeOrg_IsServed_Test()
    {
        var invoices = new Mock<IOwnerInvoiceService>();
        invoices.Setup(i => i.ListAsync(OrgId, It.IsAny<OwnerInvoiceCriteria>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var owner = new OwnerInvoicesController(invoices.Object, Org(), Scope(new HostScope(OrgId))) { ControllerContext = Context() };

        var list = await owner.List();

        Assert.IsType<OkObjectResult>(list.Result);
        invoices.Verify(i => i.ListAsync(OrgId, It.IsAny<OwnerInvoiceCriteria>(), It.IsAny<CancellationToken>()), Times.Once);

        var portalAccounts = new Mock<IPortalAccountSettingsService>();
        portalAccounts.Setup(p => p.ListAccountHoldersAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var billing = new BillingSetupController(
            Mock.Of<IBillingSetupService>(), Org(), Scope(new HostScope(OrgId)), NullLogger<BillingSetupController>.Instance, portalAccounts.Object)
        {
            ControllerContext = Context(),
        };

        var holders = await billing.GetAccountHolders(CancellationToken.None);

        Assert.IsType<OkObjectResult>(holders.Result);
        portalAccounts.Verify(p => p.ListAccountHoldersAsync(OrgId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static HostScope[] Restricted() => [new HostScope(OrgId, GrantedToUserId: UserId), new HostScope(OrgId, OwnerId: UserId)];

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

    private static int? StatusOf(IActionResult? result) => (result as IStatusCodeActionResult)?.StatusCode;

    /// <summary>A signed-in caller, and the services the problem answer of a refusal needs.</summary>
    private static ControllerContext Context()
    {
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", UserId), new Claim(ClaimTypes.NameIdentifier, UserId)], "test");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services },
        };
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
