using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Logging;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Casazen.Tests.Unit.Identity;

internal sealed class VerificationEndpointHarness
{
    public const string Operator = "auth0|operatore";

    public VerificationEndpointHarness(HostScope? scope, params string[] flagsOn)
    {
        Flags = new TestFeatureFlags(flagsOn);
        Scope = scope;
        var orgId = scope?.OrgId ?? Guid.NewGuid();
        Orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        Scopes.Setup(s => s.ResolveAsync(Operator, It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        Queue
            .Setup(q => q.GetMetricsAsync(It.IsAny<HostScope>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((HostScope _, DateTime from, DateTime to, CancellationToken _) => new VerificationMetrics(from, to, 7, 3, 2, 4, 1, 0.4));
        Controller = new VerificationController(Queue.Object, Scopes.Object, Orgs.Object, Log, Clock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Operator)], "test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    public HostScope? Scope { get; }

    public TestFeatureFlags Flags { get; }

    public Mock<IVerificationQueueService> Queue { get; } = new();

    public Mock<IHostScopeResolver> Scopes { get; } = new();

    public Mock<IOrgContextResolver> Orgs { get; } = new();

    public Mock<IIdentityVerificationMetricsService> Identity { get; } = new();

    public CapturingLogger<VerificationController> Log { get; } = new();

    public FakeTimeProvider Clock { get; } = new(IdentityMetricsWorld.Now);

    public VerificationController Controller { get; }

    public static async Task<(HttpResponse Response, byte[] Body)> ExecuteAsync(IActionResult result)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));

        return (context.Response, body.ToArray());
    }
}
