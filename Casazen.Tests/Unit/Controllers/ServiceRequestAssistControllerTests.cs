using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>«Descrivi di cosa hai bisogno» of the guided request (WIZ-AI-REQUEST): who may ask, what is passed on, what comes back.</summary>
public class ServiceRequestAssistControllerTests
{
    private const string OwnerId = "auth0|owner_123";
    private const string OtherId = "auth0|other_456";
    private const string Words = "Il rubinetto della cucina perde acqua, serve qualcuno domani mattina";
    private static readonly Guid TestOrgId = Guid.NewGuid();

    private readonly Mock<IPropertyService> _properties = new();
    private readonly Mock<IServiceRequestAssistService> _assist = new();
    private readonly ServiceRequestAssistController _controller;

    public ServiceRequestAssistControllerTests()
    {
        _controller = new ServiceRequestAssistController(
            _assist.Object,
            _properties.Object,
            HostAuthorizationTestHarness.Create(TestOrgId),
            NullLogger<ServiceRequestAssistController>.Instance);
    }

    // ─── Assist ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assist_TheOwner_GetsTheSuggestion()
    {
        var property = MakeProperty();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);
        _assist
            .Setup(a => a.AssistAsync(property, Words, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServiceRequestAssist(
                true, null, ServiceCategories.Plumbing, ServiceRequestUrgency.High, "Il rubinetto perde.", "È un lavoro da idraulico.", ServiceRequestAssistChannels.CasaZen, 3));

        var result = await _controller.Assist(new ServiceRequestAssistRequest { PropertyId = property.Id, Text = Words }, CancellationToken.None);

        var dto = Assert.IsType<ServiceRequestAssistResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.AiUsed);
        Assert.Null(dto.Reason);
        Assert.Equal("plumbing", dto.Category);
        Assert.Equal(ServiceRequestUrgency.High, dto.Urgency);
        Assert.Equal("Il rubinetto perde.", dto.Summary);
        Assert.Equal("È un lavoro da idraulico.", dto.Explanation);
        Assert.Equal("casazen", dto.Channel);
        Assert.Equal(3, dto.CasazenSuppliers);
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de", "it")]
    public async Task Assist_TheSentenceForTheHost_IsInEnglishForAnEnglishRequestAndItalianOtherwise(string culture, string expected)
    {
        var property = MakeProperty();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);
        _assist
            .Setup(a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceRequestAssist.Without(ServiceRequestAssistReasons.NotUnderstood));
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            await _controller.Assist(new ServiceRequestAssistRequest { PropertyId = property.Id, Text = Words }, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        _assist.Verify(a => a.AssistAsync(property, Words, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Assist_WhenTheAiCannotBeUsed_IsStillAnAnswerWithTheReason()
    {
        var property = MakeProperty();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);
        _assist
            .Setup(a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceRequestAssist.Without(AiSuggestionFallbackReasons.AiNotConfigured));

        var result = await _controller.Assist(new ServiceRequestAssistRequest { PropertyId = property.Id, Text = Words }, CancellationToken.None);

        var dto = Assert.IsType<ServiceRequestAssistResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.AiUsed);
        Assert.Equal("ai_not_configured", dto.Reason);
        Assert.Null(dto.Category);
        Assert.Null(dto.Urgency);
        Assert.Null(dto.Channel);
        Assert.Equal(0, dto.CasazenSuppliers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("perde")]
    [InlineData("  perde   ")]
    public async Task Assist_ADescriptionTooShort_IsAValidationErrorAndNothingIsAsked(string text)
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _controller.ControllerContext.HttpContext.RequestServices = ApiProblemServices();

        var result = await _controller.Assist(new ServiceRequestAssistRequest { PropertyId = propertyId, Text = text }, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        _properties.Verify(p => p.GetPropertyRecordAsync(It.IsAny<Guid>()), Times.Never);
        _assist.Verify(
            a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Assist_APropertyThatDoesNotExistForTheCaller_IsNotFound()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _controller.ControllerContext.HttpContext.RequestServices = ApiProblemServices();
        _properties.Setup(p => p.GetPropertyRecordAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.Assist(new ServiceRequestAssistRequest { PropertyId = propertyId, Text = Words }, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        _assist.Verify(
            a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Assist_AColleagueWhoDoesNotReachTheProperty_IsForbidden_AndNothingIsAsked()
    {
        var property = MakeProperty();
        SetUser(OtherId);
        _properties.Setup(p => p.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);

        var result = await _controller.Assist(new ServiceRequestAssistRequest { PropertyId = property.Id, Text = Words }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        _assist.Verify(
            a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Assist_ARoleWithoutThePermissionToAskForInterventions_IsForbidden()
    {
        var property = MakeProperty();
        var controller = new ServiceRequestAssistController(
            _assist.Object,
            _properties.Object,
            HostAuthorizationTestHarness.Create(TestOrgId, hasPermission: (_, permission) => permission != "servicerequest.write"),
            NullLogger<ServiceRequestAssistController>.Instance)
        {
            ControllerContext = ContextOf(OwnerId),
        };
        _properties.Setup(p => p.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);

        var result = await controller.Assist(new ServiceRequestAssistRequest { PropertyId = property.Id, Text = Words }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        _assist.Verify(
            a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── GetAvailability ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAvailability_IsToldWhetherTheAiIsThere_WithoutAskingTheAi(bool available)
    {
        _assist.Setup(a => a.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(available);

        var result = await _controller.GetAvailability(CancellationToken.None);

        var dto = Assert.IsType<ServiceRequestAssistAvailabilityResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(available, dto.Available);
        _assist.Verify(
            a => a.AssistAsync(It.IsAny<Property>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── The doors ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheController_AsksForThePermissionOfCreatingTheRequest()
    {
        var authorize = typeof(ServiceRequestAssistController).GetCustomAttribute<AuthorizeAttribute>();
        var route = typeof(ServiceRequestAssistController).GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(CasazenPolicies.ServiceRequestWrite, authorize.Policy);
        Assert.Equal("api/service-requests/assist", route?.Template);
    }

    [Fact]
    public void Assist_IsUnderTheAiRateLimit()
    {
        var action = typeof(ServiceRequestAssistController).GetMethod(nameof(ServiceRequestAssistController.Assist));

        Assert.NotNull(action);
        Assert.NotNull(action.GetCustomAttribute<AiRateLimitAttribute>());
        Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
    }

    [Fact]
    public void GetAvailability_CostsNoPermitOfTheAiRateLimit()
    {
        var action = typeof(ServiceRequestAssistController).GetMethod(nameof(ServiceRequestAssistController.GetAvailability));

        Assert.NotNull(action);
        Assert.Null(action.GetCustomAttribute<AiRateLimitAttribute>());
        Assert.NotNull(action.GetCustomAttribute<HttpGetAttribute>());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private void SetUser(string userId) => _controller.ControllerContext = ContextOf(userId);

    private static ControllerContext ContextOf(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "TestAuth")),
        },
    };

    private static Property MakeProperty() =>
        new() { Id = Guid.NewGuid(), OwnerId = OwnerId, OrgId = TestOrgId, Name = "Casa Como", NightlyRate = 0m, ComuneIstatCode = "013075" };

    /// <summary>What <c>ApiProblem</c> needs from the request: the localizer of the messages (the problem details factory).</summary>
    private static IServiceProvider ApiProblemServices() => new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
}
