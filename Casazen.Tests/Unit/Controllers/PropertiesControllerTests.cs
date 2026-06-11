using System.Security.Claims;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PropertiesControllerTests
{
    private readonly Mock<IPropertyService> _mockService;
    private readonly Mock<IImageStorageService> _mockImageStorage;
    private readonly Mock<IPropertyAuthorizationService> _mockAuthz;
    private readonly Mock<IPropertyDocumentService> _mockDocumentService;
    private readonly Mock<IAdminAccessAuditService> _mockAuditService;
    private readonly Mock<IOrgContextResolver> _mockOrgContextResolver;
    private readonly Mock<IEntitlementService> _mockEntitlementService;
    private readonly Mock<ILogger<PropertiesController>> _mockLogger;
    private readonly PropertiesController _controller;

    public PropertiesControllerTests()
    {
        _mockService = new Mock<IPropertyService>();
        _mockImageStorage = new Mock<IImageStorageService>();
        _mockAuthz = new Mock<IPropertyAuthorizationService>();
        _mockDocumentService = new Mock<IPropertyDocumentService>();
        _mockAuditService = new Mock<IAdminAccessAuditService>();
        _mockOrgContextResolver = new Mock<IOrgContextResolver>();
        _mockEntitlementService = new Mock<IEntitlementService>();
        _mockLogger = new Mock<ILogger<PropertiesController>>();
        _controller = new PropertiesController(
            _mockService.Object,
            _mockImageStorage.Object,
            _mockAuthz.Object,
            _mockDocumentService.Object,
            _mockAuditService.Object,
            _mockOrgContextResolver.Object,
            _mockEntitlementService.Object,
            _mockLogger.Object);

        // Defaults: caller has an org and is under the plan limit. Create-path tests that need
        // the opposite (no org / limit reached) override these per-test.
        _mockOrgContextResolver
            .Setup(x => x.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultOrgId);
        _mockEntitlementService
            .Setup(x => x.ReservePropertySlotAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private static readonly Guid DefaultOrgId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private void AllowAuthorization() =>
        _mockAuthz.Setup(x => x.CanAccess(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>())).Returns(true);

    [Fact]
    public async Task GetAll_WithAuthenticatedUser_ReturnsUserProperties()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);

        var properties = new List<Property>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", OwnerId = userId },
            new() { Id = Guid.NewGuid(), Name = "Property 2", OwnerId = userId }
        };
        _mockService.Setup(x => x.GetOwnerPropertiesAsync(userId)).ReturnsAsync(properties);

        // Act
        var result = await _controller.GetAll();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedProperties = Assert.IsAssignableFrom<IEnumerable<Property>>(okResult.Value);
        Assert.Equal(2, returnedProperties.Count());
        _mockService.Verify(x => x.GetOwnerPropertiesAsync(userId), Times.Once);
    }

    [Fact]
    public async Task GetAll_WithoutSubClaim_ReturnsUnauthorized()
    {
        // Arrange - User without "sub" claim
        var claims = new List<Claim>();
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        // Act
        var result = await _controller.GetAll();

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
        _mockService.Verify(x => x.GetOwnerPropertiesAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetById_WithValidId_ReturnsProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property { Id = propertyId, Name = "Test Property" };
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.GetById(propertyId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedProperty = Assert.IsType<Property>(okResult.Value);
        Assert.Equal(propertyId, returnedProperty.Id);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetById_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.GetById(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task Create_WithAuthenticatedUser_CreatesPropertyWithCorrectOwnerId()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);

        var request = new CreatePropertyRequest
        {
            Name = "New Property",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 50m
        };

        var createdProperty = new Property
        {
            Id = Guid.NewGuid(),
            Name = "New Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = userId
        };

        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(createdProperty);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var returnedProperty = Assert.IsType<Property>(createdResult.Value);
        Assert.Equal(userId, returnedProperty.OwnerId);
        Assert.Equal("New Property", returnedProperty.Name);

        _mockService.Verify(x => x.CreatePropertyAsync(It.Is<Property>(
            p => p.OwnerId == userId)), Times.Once);
    }

    [Fact]
    public async Task Create_WithoutSubClaim_ReturnsUnauthorized()
    {
        // Arrange - User without "sub" claim
        var claims = new List<Claim>();
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        var request = new CreatePropertyRequest { Name = "New Property", Address = "Via Roma 1", City = "Rome", Bedrooms = 1, Bathrooms = 1, MaxGuests = 2, NightlyRate = 50m };

        // Act
        var result = await _controller.Create(request);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
        _mockService.Verify(x => x.CreatePropertyAsync(It.IsAny<Property>()), Times.Never);
    }

    [Theory]
    [InlineData("auth0|123")]
    [InlineData("google-oauth2|1234567890")]
    [InlineData("auth0|very-long-user-id-with-many-characters-1234567890")]
    [InlineData("github|9876543210")]
    public async Task Create_WithVariousAuth0SubFormats_SetsCorrectOwnerId(string userId)
    {
        // Arrange
        SetupUserClaims(userId);

        var request = new CreatePropertyRequest
        {
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 50m
        };

        var createdProperty = new Property
        {
            Id = Guid.NewGuid(),
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = userId
        };

        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(createdProperty);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var returnedProperty = Assert.IsType<Property>(createdResult.Value);
        Assert.Equal(userId, returnedProperty.OwnerId);

        _mockService.Verify(x => x.CreatePropertyAsync(It.Is<Property>(
            p => p.OwnerId == userId)), Times.Once);
    }

    [Fact]
    public async Task Update_WithValidId_UpdatesProperty()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var existingProperty = new Property
        {
            Id = propertyId,
            Name = "Original",
            OwnerId = userId
        };
        var request = new UpdatePropertyRequest
        {
            Name = "Updated",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 50m
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);
        _mockService.Setup(x => x.UpdatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(existingProperty);

        // Act
        var result = await _controller.Update(propertyId, request);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.UpdatePropertyAsync(It.Is<Property>(
            p => p.Id == propertyId)), Times.Once);
    }

    [Fact]
    public async Task Update_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);

        var propertyId = Guid.NewGuid();
        var request = new UpdatePropertyRequest { Name = "Updated", Address = "Via Roma 1", City = "Rome", Bedrooms = 1, Bathrooms = 1, MaxGuests = 2, NightlyRate = 50m };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.Update(propertyId, request);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.UpdatePropertyAsync(It.IsAny<Property>()), Times.Never);
    }

    [Fact]
    public async Task Update_AsOwner_SuccessfullyUpdates()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var existingProperty = new Property
        {
            Id = propertyId,
            Name = "Original",
            OwnerId = userId
        };
        var request = new UpdatePropertyRequest
        {
            Name = "Updated",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 50m
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);
        _mockService.Setup(x => x.UpdatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(existingProperty);

        // Act
        var result = await _controller.Update(propertyId, request);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        // ApplyTo mutates existingProperty in place; OwnerId and Id are preserved
        _mockService.Verify(x => x.UpdatePropertyAsync(It.Is<Property>(
            p => p.Id == propertyId && p.OwnerId == userId)), Times.Once);
    }

    [Fact]
    public async Task Update_AsNonOwner_ReturnsForbidden()
    {
        // Arrange
        var ownerId = "auth0|owner_user_123";
        var attackerId = "auth0|attacker_user_456";
        SetupUserClaims(attackerId);

        var propertyId = Guid.NewGuid();
        var existingProperty = new Property
        {
            Id = propertyId,
            Name = "Original",
            OwnerId = ownerId
        };
        var request = new UpdatePropertyRequest
        {
            Name = "Malicious Update",
            Address = "Via Roma 1",
            City = "Rome",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 50m
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);

        // Act
        var result = await _controller.Update(propertyId, request);

        // Assert
        Assert.IsType<ForbidResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.UpdatePropertyAsync(It.IsAny<Property>()), Times.Never);
    }

    [Fact]
    public async Task Update_WithoutSubClaim_ReturnsUnauthorized()
    {
        // Arrange - User without "sub" claim
        var claims = new List<Claim>();
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        var propertyId = Guid.NewGuid();
        var request = new UpdatePropertyRequest { Name = "Updated", Address = "Via Roma 1", City = "Rome", Bedrooms = 1, Bathrooms = 1, MaxGuests = 2, NightlyRate = 50m };

        // Act
        var result = await _controller.Update(propertyId, request);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(It.IsAny<Guid>()), Times.Never);
        _mockService.Verify(x => x.UpdatePropertyAsync(It.IsAny<Property>()), Times.Never);
    }

    [Fact]
    public async Task Delete_WithValidId_DeletesProperty()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var existingProperty = new Property
        {
            Id = propertyId,
            Name = "To Delete",
            OwnerId = userId
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);
        _mockService.Setup(x => x.DeletePropertyAsync(propertyId)).ReturnsAsync(true);

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.DeletePropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task Delete_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.DeletePropertyAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Delete_AsOwner_SuccessfullyDeletes()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var existingProperty = new Property
        {
            Id = propertyId,
            Name = "To Delete",
            OwnerId = userId
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);
        _mockService.Setup(x => x.DeletePropertyAsync(propertyId)).ReturnsAsync(true);

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.DeletePropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task Delete_AsNonOwner_ReturnsForbidden()
    {
        // Arrange
        var ownerId = "auth0|owner_user_123";
        var attackerId = "auth0|attacker_user_456";
        SetupUserClaims(attackerId);

        var propertyId = Guid.NewGuid();
        var existingProperty = new Property
        {
            Id = propertyId,
            Name = "To Delete",
            OwnerId = ownerId
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<ForbidResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.DeletePropertyAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Delete_WithoutSubClaim_ReturnsUnauthorized()
    {
        // Arrange - User without "sub" claim
        var claims = new List<Claim>();
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        var propertyId = Guid.NewGuid();

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(It.IsAny<Guid>()), Times.Never);
        _mockService.Verify(x => x.DeletePropertyAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Search_WithFilters_ReturnsFilteredProperties()
    {
        // Arrange
        var properties = new List<PublicPropertyDto>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", City = "Rome", Bedrooms = 2, NightlyRate = 100m },
            new() { Id = Guid.NewGuid(), Name = "Property 2", City = "Rome", Bedrooms = 3, NightlyRate = 150m }
        };

        _mockService.Setup(x => x.SearchAsync("Rome", 2, 200m)).ReturnsAsync(properties);

        // Act
        var result = await _controller.Search("Rome", 2, 200m);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedProperties = Assert.IsAssignableFrom<IEnumerable<PublicPropertyDto>>(okResult.Value);
        Assert.Equal(2, returnedProperties.Count());
        _mockService.Verify(x => x.SearchAsync("Rome", 2, 200m), Times.Once);
    }

    [Fact]
    public async Task GetAll_CallsServiceWithCorrectOwnerId()
    {
        // Arrange
        var userId = "auth0|specific_user_id_12345";
        SetupUserClaims(userId);

        _mockService.Setup(x => x.GetOwnerPropertiesAsync(userId))
            .ReturnsAsync(new List<Property>());

        // Act
        await _controller.GetAll();

        // Assert
        _mockService.Verify(x => x.GetOwnerPropertiesAsync(userId), Times.Once);
    }

    [Fact]
    public async Task Create_OverridesOwnerId_WithAuthenticatedUser()
    {
        // Arrange - OwnerId is not part of CreatePropertyRequest; it is always taken from JWT
        var authenticatedUserId = "auth0|authenticated_user";

        SetupUserClaims(authenticatedUserId);

        var request = new CreatePropertyRequest
        {
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 50m
            // No OwnerId field — intentionally excluded from the DTO
        };

        Property? capturedProperty = null;
        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .Callback<Property>(p => capturedProperty = p)
            .ReturnsAsync((Property p) => p);

        // Act
        await _controller.Create(request);

        // Assert - OwnerId is always set from the authenticated user's JWT claim
        Assert.NotNull(capturedProperty);
        Assert.Equal(authenticatedUserId, capturedProperty.OwnerId);
    }

    [Fact]
    public async Task Create_WithNameIdentifierClaim_SetsOwnerId()
    {
        // Arrange — simulate token that carries NameIdentifier instead of "sub"
        var userId = "auth0|nameidentifier_user_789";
        var claims = new List<Claim>
        {
            new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId)
            // Note: no "sub" claim — exercises the fallback path in GetAuthenticatedUserId()
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        var request = new CreatePropertyRequest
        {
            Name = "NameIdentifier Property",
            City = "Milan",
            Address = "Via Milano 1",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 75m
        };

        Property? capturedProperty = null;
        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .Callback<Property>(p => capturedProperty = p)
            .ReturnsAsync((Property p) => p);

        // Act
        var result = await _controller.Create(request);

        // Assert — NameIdentifier fallback is used; OwnerId matches
        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(capturedProperty);
        Assert.Equal(userId, capturedProperty.OwnerId);
        _mockService.Verify(x => x.CreatePropertyAsync(It.Is<Property>(p => p.OwnerId == userId)), Times.Once);
    }

    [Fact]
    public async Task Create_WithoutOwnerIdInRequest_SetsOwnerIdFromJwt()
    {
        // Arrange — CreatePropertyRequest has no OwnerId field; it must always come from JWT
        var jwtSubject = "auth0|jwt_subject_user_456";
        SetupUserClaims(jwtSubject);

        // The DTO deliberately has no OwnerId property — this is the core fix from issue #143
        var request = new CreatePropertyRequest
        {
            Name = "JWT OwnerId Property",
            City = "Florence",
            Address = "Via Firenze 5",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m
        };

        Property? capturedProperty = null;
        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .Callback<Property>(p => capturedProperty = p)
            .ReturnsAsync((Property p) => p);

        // Act
        var result = await _controller.Create(request);

        // Assert — OwnerId is set from JWT, not from request body (there is no such field)
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(capturedProperty);
        Assert.Equal(jwtSubject, capturedProperty.OwnerId);
        _mockService.Verify(x => x.CreatePropertyAsync(It.Is<Property>(p => p.OwnerId == jwtSubject)), Times.Once);
    }

    // Image Management Tests

    [Fact]
    public async Task UploadImages_AsOwner_UploadsSuccessfully()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = userId,
            PhotoUrls = new List<string>()
        };

        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);
        var uploadedUrl = "/uploads/properties/test.jpg";

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);
        _mockImageStorage.Setup(x => x.ValidateImage(mockFile)).Returns(true);
        _mockImageStorage.Setup(x => x.UploadImageAsync(mockFile, propertyId)).ReturnsAsync(uploadedUrl);
        _mockService.Setup(x => x.AddImageAsync(propertyId, uploadedUrl)).ReturnsAsync(property);

        // Act
        var result = await _controller.UploadImages(propertyId, new List<IFormFile> { mockFile });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        _mockImageStorage.Verify(x => x.UploadImageAsync(mockFile, propertyId), Times.Once);
        _mockService.Verify(x => x.AddImageAsync(propertyId, uploadedUrl), Times.Once);
    }

    [Fact]
    public async Task UploadImages_AsNonOwner_ReturnsForbidden()
    {
        // Arrange
        var ownerId = "auth0|owner_user_123";
        var attackerId = "auth0|attacker_user_456";
        SetupUserClaims(attackerId);

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = ownerId
        };

        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.UploadImages(propertyId, new List<IFormFile> { mockFile });

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockImageStorage.Verify(x => x.UploadImageAsync(It.IsAny<IFormFile>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task UploadImages_WithInvalidFile_ReturnsBadRequest()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = userId,
            PhotoUrls = new List<string>()
        };

        var mockFile = CreateMockFormFile("test.txt", "text/plain", 1024);

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);
        _mockImageStorage.Setup(x => x.ValidateImage(mockFile)).Returns(false);

        // Act
        var result = await _controller.UploadImages(propertyId, new List<IFormFile> { mockFile });

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
        _mockImageStorage.Verify(x => x.UploadImageAsync(It.IsAny<IFormFile>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task UploadImages_ExceedingLimit_ReturnsBadRequest()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = userId,
            PhotoUrls = Enumerable.Range(1, 20).Select(i => $"/uploads/{i}.jpg").ToList()
        };

        var mockFile = CreateMockFormFile("test.jpg", "image/jpeg", 1024);

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.UploadImages(propertyId, new List<IFormFile> { mockFile });

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
        _mockImageStorage.Verify(x => x.UploadImageAsync(It.IsAny<IFormFile>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteImage_AsOwner_DeletesSuccessfully()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var imageUrl = "/uploads/properties/test.jpg";
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = userId,
            PhotoUrls = new List<string> { imageUrl, "/uploads/2.jpg" }
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);
        _mockService.Setup(x => x.RemoveImageAsync(propertyId, 0)).ReturnsAsync(property);
        _mockImageStorage.Setup(x => x.DeleteImageAsync(imageUrl)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DeleteImage(propertyId, 0);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        _mockService.Verify(x => x.RemoveImageAsync(propertyId, 0), Times.Once);
        _mockImageStorage.Verify(x => x.DeleteImageAsync(imageUrl), Times.Once);
    }

    [Fact]
    public async Task DeleteImage_AsNonOwner_ReturnsForbidden()
    {
        // Arrange
        var ownerId = "auth0|owner_user_123";
        var attackerId = "auth0|attacker_user_456";
        SetupUserClaims(attackerId);

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = ownerId,
            PhotoUrls = new List<string> { "/uploads/1.jpg" }
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.DeleteImage(propertyId, 0);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(x => x.RemoveImageAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task DeleteImage_WithInvalidIndex_ReturnsBadRequest()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = userId,
            PhotoUrls = new List<string> { "/uploads/1.jpg" }
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.DeleteImage(propertyId, 5);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
        _mockService.Verify(x => x.RemoveImageAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ReorderImages_AsOwner_ReordersSuccessfully()
    {
        // Arrange
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = userId,
            PhotoUrls = new List<string> { "/uploads/1.jpg", "/uploads/2.jpg", "/uploads/3.jpg" }
        };

        var newOrder = new List<string> { "/uploads/3.jpg", "/uploads/1.jpg", "/uploads/2.jpg" };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);
        _mockService.Setup(x => x.ReorderImagesAsync(propertyId, newOrder)).ReturnsAsync(property);

        // Act
        var result = await _controller.ReorderImages(propertyId, newOrder);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        _mockService.Verify(x => x.ReorderImagesAsync(propertyId, newOrder), Times.Once);
    }

    [Fact]
    public async Task ReorderImages_AsNonOwner_ReturnsForbidden()
    {
        // Arrange
        var ownerId = "auth0|owner_user_123";
        var attackerId = "auth0|attacker_user_456";
        SetupUserClaims(attackerId);

        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            OwnerId = ownerId,
            PhotoUrls = new List<string> { "/uploads/1.jpg", "/uploads/2.jpg" }
        };

        var newOrder = new List<string> { "/uploads/2.jpg", "/uploads/1.jpg" };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.ReorderImages(propertyId, newOrder);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockService.Verify(x => x.ReorderImagesAsync(It.IsAny<Guid>(), It.IsAny<List<string>>()), Times.Never);
    }

    [Fact]
    public async Task GetImages_WithValidProperty_ReturnsImages()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            Name = "Test Property",
            PhotoUrls = new List<string> { "/uploads/1.jpg", "/uploads/2.jpg" }
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.GetImages(propertyId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var urls = Assert.IsAssignableFrom<List<string>>(okResult.Value);
        Assert.Equal(2, urls.Count);
    }

    // ─── GetDetail ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_AsOwner_ReturnsOk()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var detail = new PropertyDetailResponse { Id = propertyId, OwnerId = userId };
        _mockService.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(detail);

        var result = await _controller.GetDetail(propertyId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<PropertyDetailResponse>(ok.Value);
        Assert.Equal(propertyId, response.Id);
    }

    [Fact]
    public async Task GetDetail_AsNonOwner_ReturnsForbidden()
    {
        SetupUserClaims("auth0|attacker");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyDetailAsync(propertyId))
            .ReturnsAsync(new PropertyDetailResponse { Id = propertyId, OwnerId = "auth0|owner" });

        var result = await _controller.GetDetail(propertyId);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetDetail_PropertyNotFound_ReturnsNotFound()
    {
        SetupUserClaims("auth0|user");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyDetailAsync(propertyId))
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await _controller.GetDetail(propertyId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ─── GetDocuments ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDocuments_AsOwner_ReturnsOk()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });
        _mockDocumentService.Setup(x => x.GetByPropertyIdAsync(propertyId))
            .ReturnsAsync([new PropertyDocument { Id = Guid.NewGuid(), PropertyId = propertyId, FileName = "doc.pdf" }]);

        var result = await _controller.GetDocuments(propertyId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var docs = Assert.IsAssignableFrom<IEnumerable<PropertyDocumentDto>>(ok.Value);
        Assert.Single(docs);
    }

    [Fact]
    public async Task GetDocuments_AsNonOwner_ReturnsForbidden()
    {
        SetupUserClaims("auth0|attacker");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = "auth0|owner" });

        var result = await _controller.GetDocuments(propertyId);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetDocuments_PropertyNotFound_ReturnsNotFound()
    {
        SetupUserClaims("auth0|user");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.GetDocuments(propertyId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ─── UploadDocument ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadDocument_AsOwner_ReturnsCreated()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });

        var document = new PropertyDocument
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            FileName = "cin.pdf",
            DocumentType = DocumentType.CinCertificate
        };
        var mockFile = CreateMockFormFile("cin.pdf", "application/pdf", 1024);
        _mockDocumentService.Setup(x => x.UploadDocumentAsync(propertyId, mockFile, DocumentType.CinCertificate, userId))
            .ReturnsAsync(document);

        var result = await _controller.UploadDocument(propertyId, mockFile, "CinCertificate");

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task UploadDocument_InvalidDocumentType_ReturnsBadRequest()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });

        var mockFile = CreateMockFormFile("doc.pdf", "application/pdf", 1024);

        var result = await _controller.UploadDocument(propertyId, mockFile, "InvalidType");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UploadDocument_AsNonOwner_ReturnsForbidden()
    {
        SetupUserClaims("auth0|attacker");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = "auth0|owner" });

        var mockFile = CreateMockFormFile("doc.pdf", "application/pdf", 1024);
        var result = await _controller.UploadDocument(propertyId, mockFile, "CinCertificate");

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UploadDocument_PropertyNotFound_ReturnsNotFound()
    {
        SetupUserClaims("auth0|user");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        var mockFile = CreateMockFormFile("doc.pdf", "application/pdf", 1024);
        var result = await _controller.UploadDocument(propertyId, mockFile, "CinCertificate");

        Assert.IsType<NotFoundResult>(result.Result);
        _mockDocumentService.Verify(x => x.UploadDocumentAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<DocumentType>(), It.IsAny<string>()), Times.Never);
    }

    // ─── DeleteDocument ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteDocument_AsOwner_ReturnsNoContent()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });
        _mockDocumentService.Setup(x => x.GetDocumentAsync(docId))
            .ReturnsAsync(new PropertyDocument { Id = docId, PropertyId = propertyId });
        _mockDocumentService.Setup(x => x.DeleteDocumentAsync(docId)).Returns(Task.CompletedTask);

        var result = await _controller.DeleteDocument(propertyId, docId);

        Assert.IsType<NoContentResult>(result);
        _mockDocumentService.Verify(x => x.DeleteDocumentAsync(docId), Times.Once);
    }

    [Fact]
    public async Task DeleteDocument_DocumentBelongsToDifferentProperty_ReturnsNotFound()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var otherPropertyId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });
        _mockDocumentService.Setup(x => x.GetDocumentAsync(docId))
            .ReturnsAsync(new PropertyDocument { Id = docId, PropertyId = otherPropertyId });

        var result = await _controller.DeleteDocument(propertyId, docId);

        Assert.IsType<NotFoundResult>(result);
        _mockDocumentService.Verify(x => x.DeleteDocumentAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteDocument_AsNonOwner_ReturnsForbidden()
    {
        SetupUserClaims("auth0|attacker");
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = "auth0|owner" });

        var result = await _controller.DeleteDocument(propertyId, Guid.NewGuid());

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DeleteDocument_DocumentNotFound_ReturnsNotFound()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });
        _mockDocumentService.Setup(x => x.GetDocumentAsync(It.IsAny<Guid>())).ReturnsAsync((PropertyDocument?)null);

        var result = await _controller.DeleteDocument(propertyId, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetDetail_AsAdminCrossOwner_LogsPrivilegedAccess()
    {
        var adminId = "auth0|admin_user";
        SetupUserClaims(adminId, ["Admin"]);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        var ownerId = "auth0|owner";
        _mockService.Setup(x => x.GetPropertyDetailAsync(propertyId))
            .ReturnsAsync(new PropertyDetailResponse { Id = propertyId, OwnerId = ownerId });

        var result = await _controller.GetDetail(propertyId);

        Assert.IsType<OkObjectResult>(result.Result);
        _mockAuditService.Verify(
            x => x.LogPrivilegedPropertyAccessAsync(adminId, propertyId, ownerId, "PropertyDetail.Read", default),
            Times.Once);
    }

    [Fact]
    public async Task GetDetail_AsOwner_DoesNotLogPrivilegedAccess()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId, ["PropertyOwner"]);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyDetailAsync(propertyId))
            .ReturnsAsync(new PropertyDetailResponse { Id = propertyId, OwnerId = userId });

        await _controller.GetDetail(propertyId);

        _mockAuditService.Verify(
            x => x.LogPrivilegedPropertyAccessAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Never);
    }

    [Fact]
    public async Task UploadDocument_InvalidFile_ReturnsBadRequest()
    {
        var userId = "auth0|owner_user_123";
        SetupUserClaims(userId);
        AllowAuthorization();

        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = userId });

        var mockFile = CreateMockFormFile("virus.exe", "application/octet-stream", 1024);
        _mockDocumentService.Setup(x => x.UploadDocumentAsync(propertyId, mockFile, DocumentType.Other, userId))
            .ThrowsAsync(new InvalidOperationException("Invalid document file type or size"));

        var result = await _controller.UploadDocument(propertyId, mockFile, "Other");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private void SetupUserClaims(string userId, IEnumerable<string>? roles = null)
    {
        var claims = new List<Claim>
        {
            new Claim("sub", userId)
        };

        if (roles != null)
        {
            foreach (var role in roles)
            {
                claims.Add(new Claim("https://casazen.app/roles", role));
            }
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }

    private IFormFile CreateMockFormFile(string fileName, string contentType, long length)
    {
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.ContentType).Returns(contentType);
        mockFile.Setup(f => f.Length).Returns(length);

        var content = new byte[length];
        var stream = new MemoryStream(content);
        mockFile.Setup(f => f.OpenReadStream()).Returns(stream);
        mockFile.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mockFile.Object;
    }
}
