using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Pricing;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>«Calcola con l'AI» of the step «Prezzi» (WIZ-AI-PRICE): who may ask, what is passed on, what comes back.</summary>
public class PropertyPriceSuggestionControllerTests
{
    private const string OwnerId = "auth0|owner_123";
    private const string OtherId = "auth0|other_456";
    private static readonly Guid TestOrgId = Guid.NewGuid();

    private static readonly PriceSuggestionBasis Basis = new("Como", "CO", "Lombardia", 4, 3, [PriceSuggestionFields.Guests]);

    private readonly Mock<IPropertyService> _properties = new();
    private readonly Mock<IListingPriceSuggestionService> _suggestions = new();
    private readonly PropertyPriceSuggestionController _controller;

    public PropertyPriceSuggestionControllerTests()
    {
        _controller = new PropertyPriceSuggestionController(
            _properties.Object,
            _suggestions.Object,
            HostAuthorizationTestHarness.Create(TestOrgId),
            NullLogger<PropertyPriceSuggestionController>.Instance);
    }

    // ─── Suggest ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_TheOwner_GetsTheSuggestion()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _suggestions
            .Setup(s => s.SuggestAsync(It.IsAny<Property>(), PricePositioning.Higher, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NightlyPriceSuggestion(true, null, 140m, 120m, 165m, 60m, 15m, 2, "Zona centrale.", Basis));

        var result = await _controller.Suggest(propertyId, new PriceSuggestionRequest { Positioning = PricePositioning.Higher }, CancellationToken.None);

        var dto = Assert.IsType<PriceSuggestionResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.AiUsed);
        Assert.Null(dto.Reason);
        Assert.Equal(140m, dto.NightlyRate);
        Assert.Equal(120m, dto.RangeMin);
        Assert.Equal(165m, dto.RangeMax);
        Assert.Equal(60m, dto.CleaningFee);
        Assert.Equal(15m, dto.WeekendSurchargePercent);
        Assert.Equal(2, dto.MinNights);
        Assert.Equal("Zona centrale.", dto.Rationale);
        Assert.Equal("Como", dto.BasedOn.Comune);
        Assert.Equal(4, dto.BasedOn.SimilarListings);
        Assert.Equal(new[] { PriceSuggestionFields.Guests }, dto.BasedOn.UsedFields);
    }

    [Fact]
    public async Task Suggest_WithoutABody_IsInLineWithTheArea()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _suggestions
            .Setup(s => s.SuggestAsync(It.IsAny<Property>(), It.IsAny<PricePositioning>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Nothing());

        await _controller.Suggest(propertyId, request: null, CancellationToken.None);

        _suggestions.Verify(
            s => s.SuggestAsync(It.IsAny<Property>(), PricePositioning.Market, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("it", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de", "it")]
    public async Task Suggest_TheExplanation_IsInEnglishForAnEnglishRequestAndItalianOtherwise(string culture, string expected)
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _suggestions
            .Setup(s => s.SuggestAsync(It.IsAny<Property>(), It.IsAny<PricePositioning>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Nothing());
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            await _controller.Suggest(propertyId, request: null, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        _suggestions.Verify(
            s => s.SuggestAsync(It.IsAny<Property>(), It.IsAny<PricePositioning>(), expected, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Suggest_WhenTheAiCannotBeUsed_IsStillAnAnswerWithTheReason()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _suggestions
            .Setup(s => s.SuggestAsync(It.IsAny<Property>(), It.IsAny<PricePositioning>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Nothing());

        var result = await _controller.Suggest(propertyId, request: null, CancellationToken.None);

        var dto = Assert.IsType<PriceSuggestionResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.AiUsed);
        Assert.Equal(PriceSuggestionFallbackReasons.AiNotConfigured, dto.Reason);
        Assert.Null(dto.NightlyRate);
    }

    [Fact]
    public async Task Suggest_APropertyThatDoesNotExistForTheCaller_IsNotFound()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.Suggest(propertyId, request: null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        _suggestions.Verify(
            s => s.SuggestAsync(It.IsAny<Property>(), It.IsAny<PricePositioning>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Suggest_AColleagueWhoDoesNotReachTheProperty_IsForbidden_AndNothingIsAsked()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var result = await _controller.Suggest(propertyId, request: null, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        _suggestions.Verify(
            s => s.SuggestAsync(It.IsAny<Property>(), It.IsAny<PricePositioning>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── GetAvailability ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAvailability_TheOwner_IsToldWhetherTheAiIsThere(bool available)
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _suggestions.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(available);

        var result = await _controller.GetAvailability(propertyId, CancellationToken.None);

        var dto = Assert.IsType<PriceSuggestionAvailabilityResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(available, dto.Available);
    }

    [Fact]
    public async Task GetAvailability_APropertyThatDoesNotExistForTheCaller_IsNotFound()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.GetAvailability(propertyId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetAvailability_AColleagueWhoDoesNotReachTheProperty_IsForbidden()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _properties.Setup(p => p.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var result = await _controller.GetAvailability(propertyId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        _suggestions.Verify(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── The doors ───────────────────────────────────────────────────────────────

    [Fact]
    public void Suggest_IsUnderTheAiRateLimit_AndAsksForTheWritePermission()
    {
        var action = typeof(PropertyPriceSuggestionController).GetMethod(nameof(PropertyPriceSuggestionController.Suggest));

        Assert.NotNull(action);
        Assert.NotNull(action.GetCustomAttribute<AiRateLimitAttribute>());
        Assert.Contains(
            action.GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.Policy == CasazenPolicies.PropertyWrite);
    }

    [Fact]
    public void GetAvailability_CostsNoPermitOfTheAiRateLimit()
    {
        var action = typeof(PropertyPriceSuggestionController).GetMethod(nameof(PropertyPriceSuggestionController.GetAvailability));

        Assert.NotNull(action);
        Assert.Null(action.GetCustomAttribute<AiRateLimitAttribute>());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private void SetUser(string userId)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "TestAuth")),
            },
        };
    }

    private static Property MakeProperty(Guid id) =>
        new() { Id = id, OwnerId = OwnerId, OrgId = TestOrgId, Name = "Casa Como", NightlyRate = 0m };

    private static NightlyPriceSuggestion Nothing() =>
        new(false, PriceSuggestionFallbackReasons.AiNotConfigured, null, null, null, null, null, null, null, Basis);
}
