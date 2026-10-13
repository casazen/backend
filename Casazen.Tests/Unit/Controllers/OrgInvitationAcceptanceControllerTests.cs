using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// AM-02: the two calls of the invited person. The lookup answers the same 410 for every link that does not work; the
/// acceptance checks the consents first, takes the email from the token or from Auth0 (never from the body) and tells the
/// service whether the caller is staff.
/// </summary>
public class OrgInvitationAcceptanceControllerTests
{
    private const string Sub = "auth0|anna";
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly Mock<IOrgInvitationService> _invitations = new();
    private readonly Mock<IOnboardingService> _onboarding = new();
    private readonly Mock<IUserService> _users = new();
    private readonly Mock<IAccountEmailResolver> _emails = new();

    public OrgInvitationAcceptanceControllerTests()
    {
        _onboarding.Setup(o => o.ValidateConsents(It.IsAny<OnboardingConsentsInput?>(), true)).Returns((true, null));
        _emails.Setup(e => e.ResolveAsync(It.IsAny<ClaimsPrincipal>(), Sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("anna.leone@example.com", true));
        _users.Setup(u => u.GetCurrentUserAsync(Sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new User { Id = Sub });
    }

    private OrgInvitationAcceptanceController Build(params Claim[] claims)
    {
        var controller = new OrgInvitationAcceptanceController(_invitations.Object, _onboarding.Object, _users.Object, _emails.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
        controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        return controller;
    }

    private static OnboardingConsentsDto Consents() => new()
    {
        TosAccepted = true,
        TosVersion = "v1",
        PrivacyAccepted = true,
        PrivacyVersion = "v1",
        DpaAccepted = true,
        DpaVersion = "v1",
        SubprocessorsAcknowledged = true,
        SubprocessorsVersion = "v1",
    };

    // ─── Lookup ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lookup_AValidLink_AnswersWithWhatItIsForAndNeverCachesIt()
    {
        var expires = new DateTime(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc);
        _invitations.Setup(i => i.LookupAsync(Token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgInvitationPreview("Casa Rossi", "anna.leone@example.com", "Anna Leone", OrgRole.Collaborator, ["short-rent"], expires));
        var controller = Build();

        var result = await controller.Lookup(new OrgInvitationLookupRequest { Token = Token }, CancellationToken.None);

        var response = Assert.IsType<OrgInvitationLookupResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(("Casa Rossi", "anna.leone@example.com", "Anna Leone", OrgRole.Collaborator, expires), (response.OrgName, response.Email, response.Name, response.Role, response.ExpiresAt));
        Assert.Equal(["short-rent"], response.Areas);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Lookup_ALinkThatDoesNotWork_Gets410InvitationInvalid_WhateverTheReason()
    {
        _invitations.Setup(i => i.LookupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((OrgInvitationPreview?)null);
        var controller = Build();

        var result = await controller.Lookup(new OrgInvitationLookupRequest { Token = "anything" }, CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Equal(StatusCodes.Status410Gone, problem.Status);
        Assert.Equal(OrgInvitationErrors.Invalid, problem.Extensions["code"]);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.NotEqual("InvitationInvalid", problem.Detail);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    // ─── Accept ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_Success_PassesTheAccountTheEmailFromTheTokenAndTheClientIp()
    {
        AcceptOrgInvitation? received = null;
        _invitations.Setup(i => i.AcceptAsync(It.IsAny<AcceptOrgInvitation>(), It.IsAny<CancellationToken>()))
            .Callback<AcceptOrgInvitation, CancellationToken>((r, _) => received = r)
            .ReturnsAsync(new OrgInvitationAccepted(Guid.NewGuid(), "Casa Rossi", OrgRole.Collaborator, ["short-rent"], false));
        var controller = Build(new Claim("sub", Sub));

        var result = await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = Consents() }, CancellationToken.None);

        var response = Assert.IsType<AcceptOrgInvitationResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(("Casa Rossi", OrgRole.Collaborator, false), (response.OrgName, response.Role, response.LeftEmptyOrg));
        Assert.NotNull(received);
        Assert.Equal((Token, Sub, "anna.leone@example.com", true, false, "203.0.113.7"), (received.Token, received.UserId, received.AccountEmail, received.AccountEmailVerified, received.CallerIsPlatformAdmin, received.ClientIp));
        Assert.True(received.Consents.TosAccepted);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Accept_TheAccountRowIsEnsuredBeforeTheAcceptance()
    {
        _invitations.Setup(i => i.AcceptAsync(It.IsAny<AcceptOrgInvitation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgInvitationAccepted(Guid.NewGuid(), "Casa Rossi", OrgRole.Collaborator, ["short-rent"], false));
        var controller = Build(new Claim("sub", Sub), new Claim("given_name", "Anna"), new Claim("family_name", "Leone"));

        await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = Consents() }, CancellationToken.None);

        _users.Verify(u => u.GetCurrentUserAsync(Sub, "anna.leone@example.com", "Anna", "Leone"), Times.Once);
    }

    [Theory]
    [InlineData("Admin")]
    public async Task Accept_ACallerWithTheAdminRole_IsToldToTheServiceAsPlatformAdmin(string role)
    {
        AcceptOrgInvitation? received = null;
        _invitations.Setup(i => i.AcceptAsync(It.IsAny<AcceptOrgInvitation>(), It.IsAny<CancellationToken>()))
            .Callback<AcceptOrgInvitation, CancellationToken>((r, _) => received = r)
            .ReturnsAsync(new OrgInvitationAccepted(Guid.NewGuid(), "Casa Rossi", OrgRole.Collaborator, ["short-rent"], false));
        var controller = Build(new Claim("sub", Sub), new Claim(ClaimTypes.Role, role));

        await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = Consents() }, CancellationToken.None);

        Assert.True(received!.CallerIsPlatformAdmin);
    }

    [Fact]
    public async Task Accept_NoSubject_Is401AndNothingIsCalled()
    {
        var controller = Build();

        var result = await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = Consents() }, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        _invitations.Verify(i => i.AcceptAsync(It.IsAny<AcceptOrgInvitation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Accept_NoConsents_Is400ConsentsIncompleteBeforeAnythingElse()
    {
        _onboarding.Setup(o => o.ValidateConsents(null, true))
            .Returns((false, new ConsentValidationError(ConsentValidationErrorType.Incomplete, "ConsentsIncomplete")));
        var controller = Build(new Claim("sub", Sub));

        var result = await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = null }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        Assert.Equal(UsersController.ConsentsIncompleteCode, Assert.IsType<ProblemDetails>(objectResult.Value).Extensions["code"]);
        _emails.Verify(e => e.ResolveAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _invitations.Verify(i => i.AcceptAsync(It.IsAny<AcceptOrgInvitation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Accept_StaleConsents_Is400StaleDocumentsWithTheList()
    {
        _onboarding.Setup(o => o.ValidateConsents(It.IsAny<OnboardingConsentsInput?>(), true))
            .Returns((false, new ConsentValidationError(ConsentValidationErrorType.StaleVersion, "ConsentsStale", ["tos", "dpa"])));
        var controller = Build(new Claim("sub", Sub));

        var result = await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = Consents() }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(UsersController.StaleDocumentsCode, problem.Extensions["code"]);
        Assert.Equal(["tos", "dpa"], Assert.IsType<string[]>(problem.Extensions["staleDocuments"]));
    }

    [Fact]
    public async Task Accept_NoEmailAnywhere_IsHandedToTheServiceAsAnUnverifiedEmptyEmail()
    {
        _emails.Setup(e => e.ResolveAsync(It.IsAny<ClaimsPrincipal>(), Sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string?)null, false));
        AcceptOrgInvitation? received = null;
        _invitations.Setup(i => i.AcceptAsync(It.IsAny<AcceptOrgInvitation>(), It.IsAny<CancellationToken>()))
            .Callback<AcceptOrgInvitation, CancellationToken>((r, _) => received = r)
            .ReturnsAsync(new OrgInvitationAccepted(Guid.NewGuid(), "Casa Rossi", OrgRole.Collaborator, ["short-rent"], false));
        var controller = Build(new Claim("sub", Sub));

        await controller.Accept(new AcceptOrgInvitationRequest { Token = Token, Consents = Consents() }, CancellationToken.None);

        Assert.Equal((string.Empty, false), (received!.AccountEmail, received.AccountEmailVerified));
    }
}
