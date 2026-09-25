using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// SU-01: the account email used to bind an invite comes from the token or from Auth0, never from the form.
/// SU-02: the claim gets the same email and Auth0's <c>email_verified</c> for it (token claim first, then the Management
/// API, trusted only for the same email).
/// </summary>
public class SuppliersControllerRegisterTests
{
    private const string UserId = "auth0|su01-unit";
    private const string Email = "fornitore@example.com";

    [Fact]
    public async Task Register_CustomEmailClaim_PassesItAsAccountEmailAndCreatesUser()
    {
        var (controller, service, users, auth0) = CreateController(
            new Claim("sub", UserId),
            new Claim("https://casazen.app/email", Email),
            new Claim("email", "other@example.com"));

        await controller.Register(Request(), CancellationToken.None);

        service.Verify(s => s.RegisterAsync(
            It.Is<SupplierRegistration>(r => r.UserId == UserId && r.AccountEmail == Email && r.InviteToken == Token),
            It.IsAny<CancellationToken>()));
        users.Verify(u => u.GetCurrentUserAsync(UserId, Email, string.Empty, string.Empty));
        auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
        auth0.Verify(a => a.AssignRoleAsync(UserId, UserRole.Supplier, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Register_TokenWithoutEmailClaim_UsesTheAuth0ProfileEmail()
    {
        var (controller, service, _, auth0) = CreateController(new Claim("sub", UserId));
        auth0.Setup(a => a.GetUserProfileAsync(UserId)).ReturnsAsync(new Auth0UserProfile(Email, "Mario", "Rossi"));

        await controller.Register(Request(), CancellationToken.None);

        service.Verify(s => s.RegisterAsync(
            It.Is<SupplierRegistration>(r => r.AccountEmail == Email),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Register_Anonymous_HasNoAccountEmailAndAssignsNoRole()
    {
        var (controller, service, users, auth0) = CreateController();

        await controller.Register(Request(), CancellationToken.None);

        service.Verify(s => s.RegisterAsync(
            It.Is<SupplierRegistration>(r => r.UserId == null && r.AccountEmail == null),
            It.IsAny<CancellationToken>()));
        users.VerifyNoOtherCalls();
        auth0.Verify(a => a.AssignRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── Claim (SU-02) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Claim_WithoutTokenAndVerifiedClaim_PassesVerifiedEmailWithoutCallingAuth0()
    {
        var (controller, service, users, auth0) = CreateController(
            new Claim("sub", UserId),
            new Claim("https://casazen.app/email", Email),
            new Claim("https://casazen.app/email_verified", "true", ClaimValueTypes.Boolean));

        var result = await controller.Claim(new SupplierClaimRequest(), CancellationToken.None);

        var body = Assert.IsType<SupplierClaimResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.RolesSynced);
        Assert.Equal("/app/supplier/activation", body.RedirectUrl);
        service.Verify(s => s.ClaimAsync(
            It.Is<SupplierClaim>(c => c.UserId == UserId && c.AccountEmail == Email && c.AccountEmailVerified && c.ClaimToken == null),
            It.IsAny<CancellationToken>()));
        users.Verify(u => u.GetCurrentUserAsync(UserId, Email, string.Empty, string.Empty));
        auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
        auth0.Verify(a => a.AssignRoleAsync(UserId, UserRole.Supplier, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("FORNITORE@example.com", true)]
    [InlineData("someone-else@example.com", false)]
    public async Task Claim_WithoutTokenOrVerifiedClaim_TrustsAuth0VerifiedFlagOnlyForTheTokenEmail(
        string auth0Email, bool expectedVerified)
    {
        var (controller, service, _, auth0) = CreateController(
            new Claim("sub", UserId),
            new Claim("email", Email));
        auth0.Setup(a => a.GetUserProfileAsync(UserId))
            .ReturnsAsync(new Auth0UserProfile(auth0Email, "Mario", "Rossi", EmailVerified: true));

        await controller.Claim(new SupplierClaimRequest(), CancellationToken.None);

        service.Verify(s => s.ClaimAsync(
            It.Is<SupplierClaim>(c => c.AccountEmail == Email && c.AccountEmailVerified == expectedVerified),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Claim_UnverifiedClaim_IsNotOverriddenByAuth0()
    {
        var (controller, service, _, auth0) = CreateController(
            new Claim("sub", UserId),
            new Claim("https://casazen.app/email", Email),
            new Claim("https://casazen.app/email_verified", "false", ClaimValueTypes.Boolean));

        await controller.Claim(new SupplierClaimRequest(), CancellationToken.None);

        service.Verify(s => s.ClaimAsync(
            It.Is<SupplierClaim>(c => !c.AccountEmailVerified),
            It.IsAny<CancellationToken>()));
        auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Claim_WithToken_DoesNotAskAuth0AndPassesTheToken()
    {
        var (controller, service, _, auth0) = CreateController(
            new Claim("sub", UserId),
            new Claim("https://casazen.app/email", Email));

        await controller.Claim(new SupplierClaimRequest { ClaimToken = Token }, CancellationToken.None);

        service.Verify(s => s.ClaimAsync(
            It.Is<SupplierClaim>(c => c.ClaimToken == Token && !c.AccountEmailVerified),
            It.IsAny<CancellationToken>()));
        auth0.Verify(a => a.GetUserProfileAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Claim_RoleSyncFails_ReturnsOkWithRolesNotSynced()
    {
        var (controller, _, _, auth0) = CreateController(
            new Claim("sub", UserId),
            new Claim("https://casazen.app/email", Email));
        auth0.Setup(a => a.AssignRoleAsync(UserId, UserRole.Supplier, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.RateLimitedCode));

        var result = await controller.Claim(new SupplierClaimRequest { ClaimToken = Token }, CancellationToken.None);

        var body = Assert.IsType<SupplierClaimResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(body.RolesSynced);
        Assert.Equal(Auth0SyncResult.RateLimitedCode, body.RolesSyncError);
    }

    [Fact]
    public async Task Register_AnonymousSelfServe_ReturnsTheClaimToken()
    {
        var (controller, service, _, _) = CreateController();
        var org = new OrgEntity { Name = "Pulizie Srl", Slug = "su02-unit", DisplayName = "Pulizie Srl", OrgType = OrgType.Supplier };
        var expiresAt = DateTime.UtcNow.AddDays(7);
        service
            .Setup(s => s.RegisterAsync(It.IsAny<SupplierRegistration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierRegistrationResult(
                org, new SupplierProfile { OrgId = org.Id, Email = Email }, new SupplierClaimTicket(Token, expiresAt)));

        var result = await controller.Register(Request(), CancellationToken.None);

        var body = Assert.IsType<SupplierRegisterResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(Token, body.ClaimToken);
        Assert.Equal(expiresAt, body.ClaimExpiresAt);
    }

    private static readonly string Token = new('b', SupplierInviteTokens.Length);

    private static SupplierRegisterRequest Request() => new()
    {
        Email = Email,
        LegalName = "Pulizie Srl",
        Phone = "+39 06 1",
        ComuneCode = "H501",
        InviteToken = Token,
    };

    private static (SuppliersController, Mock<ISupplierService>, Mock<IUserService>, Mock<IAuth0ManagementService>) CreateController(
        params Claim[] claims)
    {
        var service = new Mock<ISupplierService>();
        var org = new OrgEntity { Name = "Pulizie Srl", Slug = "su01-unit", DisplayName = "Pulizie Srl", OrgType = OrgType.Supplier };
        service
            .Setup(s => s.RegisterAsync(It.IsAny<SupplierRegistration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierRegistrationResult(org, new SupplierProfile { OrgId = org.Id, Email = Email }));

        service
            .Setup(s => s.ClaimAsync(It.IsAny<SupplierClaim>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierClaimResult(org.Id, NewlyLinked: true));

        var users = new Mock<IUserService>();
        users
            .Setup(u => u.GetCurrentUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User { Id = UserId, Email = Email });

        var auth0 = new Mock<IAuth0ManagementService>();
        auth0
            .Setup(a => a.AssignRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var controller = new SuppliersController(
            service.Object,
            users.Object,
            auth0.Object,
            Mock.Of<IUserAuthorizationCache>(),
            Options.Create(new SupplierRegistrationOptions()),
            db,
            Mock.Of<IOrgContextResolver>(),
            Mock.Of<IAuthorizationService>(),
            NullLogger<SuppliersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(claims.Length == 0 ? new ClaimsIdentity() : new ClaimsIdentity(claims, "Test")),
                },
            },
        };

        return (controller, service, users, auth0);
    }
}
