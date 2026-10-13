using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Web.Infrastructure;
using Casazen.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Middleware;

/// <summary>
/// PL-03 and AM-01: a deactivated account answers 403 <c>account_inactive</c>, a member the org deactivated answers 403
/// <c>member_inactive</c>, on every authenticated request and before anything else runs; the account code wins when both
/// apply; a user in good standing and an anonymous request are not touched.
/// </summary>
public class InactiveAccountMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_DeactivatedOrgMember_Returns403MemberInactiveWithTheLocalizedMessageAndDoesNotCallNext()
    {
        var (context, nextCalled) = await InvokeAsync(inactiveAccount: false, memberDeactivated: true);

        Assert.False(nextCalled);
        var response = await ReadAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, response.Status);
        Assert.Equal("member_inactive", response.Code);
        Assert.Equal("Il tuo accesso all'organizzazione è stato disattivato. Contatta chi la amministra per riattivarlo.", response.Detail);
        Assert.Equal("application/problem+json", context.Response.ContentType);
    }

    [Fact]
    public async Task InvokeAsync_DeactivatedOrgMember_AnswersInEnglishWhenTheClientAsksForIt()
    {
        var (context, _) = await InvokeAsync(inactiveAccount: false, memberDeactivated: true, culture: "en");

        var response = await ReadAsync(context);
        Assert.Equal("member_inactive", response.Code);
        Assert.StartsWith("Your access to the organization has been deactivated", response.Detail);
    }

    [Fact]
    public async Task InvokeAsync_DeactivatedAccount_Returns403AccountInactive()
    {
        var (context, nextCalled) = await InvokeAsync(inactiveAccount: true, memberDeactivated: false);

        Assert.False(nextCalled);
        Assert.Equal("account_inactive", (await ReadAsync(context)).Code);
    }

    [Fact]
    public async Task InvokeAsync_BothApply_TheAccountCodeWins()
    {
        var (context, nextCalled) = await InvokeAsync(inactiveAccount: true, memberDeactivated: true);

        Assert.False(nextCalled);
        Assert.Equal("account_inactive", (await ReadAsync(context)).Code);
    }

    [Fact]
    public async Task InvokeAsync_ActiveAccountAndMember_CallsNext()
    {
        var (context, nextCalled) = await InvokeAsync(inactiveAccount: false, memberDeactivated: false);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AnonymousRequest_IsNotTouchedAndTheTenantIsNotRead()
    {
        var tenant = new Mock<IRequestTenantContext>(MockBehavior.Strict);
        var (context, nextCalled) = await InvokeAsync(tenant, authenticated: false);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        tenant.VerifyNoOtherCalls();
    }

    private static async Task<(DefaultHttpContext Context, bool NextCalled)> InvokeAsync(
        bool inactiveAccount,
        bool memberDeactivated,
        string culture = "it-IT")
    {
        var tenant = new Mock<IRequestTenantContext>();
        tenant.SetupGet(t => t.IsCallerInactive).Returns(inactiveAccount);
        tenant.SetupGet(t => t.IsCallerOrgMemberDeactivated).Returns(memberDeactivated);
        return await InvokeAsync(tenant, authenticated: true, culture);
    }

    private static async Task<(DefaultHttpContext Context, bool NextCalled)> InvokeAsync(
        Mock<IRequestTenantContext> tenant,
        bool authenticated,
        string culture = "it-IT")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/properties";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(authenticated
            ? new ClaimsIdentity([new Claim("sub", "auth0|caller")], "Test")
            : new ClaimsIdentity());

        var nextCalled = false;
        var middleware = new InactiveAccountMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<InactiveAccountMiddleware>.Instance);

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            await middleware.InvokeAsync(context, tenant.Object);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        return (context, nextCalled);
    }

    private static async Task<(int Status, string? Code, string? Detail)> ReadAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return (
            context.Response.StatusCode,
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }
}
