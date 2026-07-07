using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// Unit tests for PublicOrgController — US-003 (#215) branded booking site.
/// Covers AC1 (org branding endpoint), AC2 (property list endpoint), AC3 (property detail
/// scoped by org, cross-org 404), and DTO whitelist (no internal Org fields).
/// </summary>
public class PublicOrgControllerTests
{
    private readonly Mock<IOrgService> _orgService = new();
    private readonly Mock<IPropertyService> _propertyService = new();
    private readonly PublicOrgController _controller;

    public PublicOrgControllerTests()
    {
        _controller = new PublicOrgController(_orgService.Object, _propertyService.Object);
    }

    // ── GetOrg ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrg_WhenOrgExists_Returns200WithBrandingDto()
    {
        // Arrange
        var org = BuildOrg("casazen-milan");
        _orgService.Setup(s => s.GetPublicBySlugAsync("casazen-milan", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        // Act
        var result = await _controller.GetOrg("casazen-milan", CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PublicOrgDto>(ok.Value);
        Assert.Equal("casazen-milan", dto.Slug);
        Assert.Equal("CasaZen Milano", dto.DisplayName);
        Assert.Equal("https://cdn.example.com/logo.png", dto.LogoUrl);
        Assert.Equal("#2563eb", dto.ThemeColor);
        Assert.Equal("contact@casazen-milan.it", dto.ContactEmail);
        Assert.False(dto.ShowPoweredBy);
    }

    [Fact]
    public async Task GetOrg_WhenStarterPlan_ShowPoweredByTrue()
    {
        var org = BuildOrg("starter-org");
        org.PlanTier = PlanTier.Starter;
        org.HeroImageUrl = "https://cdn.example.com/hero.webp";
        org.Tagline = "Il tuo rifugio";
        org.PublicThemeId = "mare";
        _orgService.Setup(s => s.GetPublicBySlugAsync("starter-org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        var result = await _controller.GetOrg("starter-org", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PublicOrgDto>(ok.Value);
        Assert.True(dto.ShowPoweredBy);
        Assert.Equal("https://cdn.example.com/hero.webp", dto.HeroImageUrl);
        Assert.Equal("Il tuo rifugio", dto.Tagline);
        Assert.Equal("mare", dto.PublicThemeId);
    }

    [Fact]
    public async Task GetOrg_WhenOrgNotFound_Returns404()
    {
        // Arrange
        _orgService.Setup(s => s.GetPublicBySlugAsync("unknown-slug", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);

        // Act
        var result = await _controller.GetOrg("unknown-slug", CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetOrg_DtoNeverExposesInternalFields()
    {
        // Arrange — org has sensitive internal data
        var org = BuildOrg("org-with-secrets");
        org.StripeCustomerId = "cus_secret_123";
        org.StripeConnectedAccountId = "acct_secret_456";
        _orgService.Setup(s => s.GetPublicBySlugAsync("org-with-secrets", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);

        // Act
        var result = await _controller.GetOrg("org-with-secrets", CancellationToken.None);

        // Assert — DTO properties do not include Stripe IDs, plan tier, or timestamps
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PublicOrgDto>(ok.Value);
        var dtoProperties = dto.GetType().GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(dtoProperties, p => p.Equals("StripeCustomerId", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dtoProperties, p => p.Equals("PlanTier", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dtoProperties, p => p.Equals("CreatedAt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dtoProperties, p => p.Equals("UpdatedAt", StringComparison.OrdinalIgnoreCase));
    }

    // ── GetProperties ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProperties_WhenOrgExists_Returns200WithPropertyList()
    {
        // Arrange
        var org = BuildOrg("casazen-rome");
        var properties = new List<PublicPropertyDto>
        {
            new() { Id = Guid.NewGuid(), Name = "Villa Roma", City = "Rome", NightlyRate = 120m },
            new() { Id = Guid.NewGuid(), Name = "Appartamento Centro", City = "Rome", NightlyRate = 80m },
        };
        _orgService.Setup(s => s.GetPublicBySlugAsync("casazen-rome", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);
        _propertyService.Setup(s => s.SearchByOrgAsync(org.Id))
            .ReturnsAsync(properties);

        // Act
        var result = await _controller.GetProperties("casazen-rome", CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<IEnumerable<PublicPropertyDto>>(ok.Value);
        Assert.Equal(2, list.Count());
        _propertyService.Verify(s => s.SearchByOrgAsync(org.Id), Times.Once);
    }

    [Fact]
    public async Task GetProperties_WhenOrgNotFound_Returns404()
    {
        // Arrange
        _orgService.Setup(s => s.GetPublicBySlugAsync("ghost-org", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);

        // Act
        var result = await _controller.GetProperties("ghost-org", CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
        _propertyService.Verify(s => s.SearchByOrgAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetProperties_WhenOrgHasNoProperties_Returns200WithEmptyList()
    {
        // Arrange
        var org = BuildOrg("empty-org");
        _orgService.Setup(s => s.GetPublicBySlugAsync("empty-org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);
        _propertyService.Setup(s => s.SearchByOrgAsync(org.Id))
            .ReturnsAsync(new List<PublicPropertyDto>());

        // Act
        var result = await _controller.GetProperties("empty-org", CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<IEnumerable<PublicPropertyDto>>(ok.Value);
        Assert.Empty(list);
    }

    // ── GetProperty ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProperty_WhenPropertyBelongsToOrg_Returns200WithDetailDto()
    {
        // Arrange
        var org = BuildOrg("host-org");
        var propertyId = Guid.NewGuid();
        var detail = new PublicPropertyDetailDto
        {
            Id = propertyId,
            Name = "Villa Detail",
            City = "Florence",
            HouseRules = "No parties",
            Currency = "EUR",
        };
        _orgService.Setup(s => s.GetPublicBySlugAsync("host-org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(org);
        _propertyService.Setup(s => s.GetPublicPropertyForOrgAsync(propertyId, org.Id))
            .ReturnsAsync(detail);

        // Act
        var result = await _controller.GetProperty("host-org", propertyId, CancellationToken.None);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PublicPropertyDetailDto>(ok.Value);
        Assert.Equal("Villa Detail", dto.Name);
        Assert.Equal("No parties", dto.HouseRules);
    }

    [Fact]
    public async Task GetProperty_WhenPropertyBelongsToOtherOrg_Returns404()
    {
        // Arrange — AC3: cross-org access is blocked
        var orgA = BuildOrg("org-a");
        var propertyId = Guid.NewGuid();
        _orgService.Setup(s => s.GetPublicBySlugAsync("org-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(orgA);
        _propertyService.Setup(s => s.GetPublicPropertyForOrgAsync(propertyId, orgA.Id))
            .ReturnsAsync((PublicPropertyDetailDto?)null);

        // Act
        var result = await _controller.GetProperty("org-a", propertyId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetProperty_WhenOrgNotFound_Returns404WithoutCallingPropertyService()
    {
        // Arrange
        _orgService.Setup(s => s.GetPublicBySlugAsync("no-org", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrgEntity?)null);

        // Act
        var result = await _controller.GetProperty("no-org", Guid.NewGuid(), CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
        _propertyService.Verify(s => s.GetPublicPropertyForOrgAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static OrgEntity BuildOrg(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        Name = "CasaZen Milano",
        DisplayName = "CasaZen Milano",
        LogoUrl = "https://cdn.example.com/logo.png",
        ThemeColor = "#2563eb",
        ContactEmail = "contact@casazen-milan.it",
        PlanTier = PlanTier.Pro,
        StripeCustomerId = "cus_secret",
        StripeConnectedAccountId = "acct_secret",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
