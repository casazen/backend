using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// PM-01 on the property endpoints: <c>GET /api/properties?mode=short|long</c> (without the parameter nothing changes),
/// the rental mode in the records the host reads, the compatibility rule of the creation, and the update that accepts
/// <c>rentalMode</c> without ever changing the stored one.
/// </summary>
public class PropertiesControllerRentalModeTests
{
    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Mock<IPropertyService> _service = new();
    private readonly Mock<IAuthorizationService> _hostAuthorization = new();
    private readonly PropertiesController _controller;

    public PropertiesControllerRentalModeTests()
    {
        _hostAuthorization
            .Setup(x => x.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(AuthorizationResult.Success());
        var orgContext = new Mock<IOrgContextResolver>();
        orgContext
            .Setup(x => x.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrgId);
        var leases = new Mock<ILeaseContractRepository>();
        leases.Setup(x => x.GetByPropertyAsync(It.IsAny<Guid>())).ReturnsAsync(Array.Empty<LeaseContract>());
        var entitlements = new Mock<IEntitlementService>();
        entitlements
            .Setup(x => x.CreatePropertyWithinLimitAsync(
                It.IsAny<Guid>(), It.IsAny<Func<Task<Property>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Func<Task<Property>> create, CancellationToken _) => (Property?)await create());

        _controller = new PropertiesController(
            _service.Object,
            Mock.Of<IPropertyPhotoService>(),
            Mock.Of<IPropertyAuthorizationService>(),
            leases.Object,
            Mock.Of<IPropertyDocumentService>(),
            Mock.Of<IAdminAccessAuditService>(),
            orgContext.Object,
            entitlements.Object,
            ICalTestServices.PropertySync(
                new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options),
                Mock.Of<ISafeExternalHttpClient>(),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:ApiBaseUrl"] = "https://api.test" }).Build()),
            Mock.Of<IComplianceWizardService>(),
            _hostAuthorization.Object,
            Mock.Of<IPropertyComuneResolver>(),
            Mock.Of<ILogger<PropertiesController>>());
        SetUser("auth0|host");
    }

    // ─── GET /api/properties?mode= ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("short", RentalMode.Short)]
    [InlineData("long", RentalMode.Long)]
    [InlineData("Long", RentalMode.Long)]
    [InlineData(" LONG ", RentalMode.Long)]
    public async Task GetAll_WithMode_ListsOnlyThatMode(string mode, RentalMode expected)
    {
        var scope = new HostScope(OrgId, "auth0|host");
        _service
            .Setup(x => x.GetPropertiesAsync(scope, expected))
            .ReturnsAsync([new Property { Id = Guid.NewGuid(), Name = "Casa", RentalMode = expected }]);

        var result = await _controller.GetAll(mode);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var row = Assert.Single(Assert.IsAssignableFrom<IEnumerable<PropertyResponse>>(ok.Value));
        Assert.Equal(expected, row.RentalMode);
        _service.Verify(x => x.GetPropertiesAsync(scope, expected), Times.Once);
        _service.Verify(x => x.GetPropertiesAsync(It.IsAny<HostScope>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAll_WithoutMode_ListsEveryPropertyAsItAlwaysDid(string? mode)
    {
        var scope = new HostScope(OrgId, "auth0|host");
        _service
            .Setup(x => x.GetPropertiesAsync(scope))
            .ReturnsAsync([
                new Property { Id = Guid.NewGuid(), Name = "Casa breve", RentalMode = RentalMode.Short },
                new Property { Id = Guid.NewGuid(), Name = "Casa lungo", RentalMode = RentalMode.Long },
            ]);

        var result = await _controller.GetAll(mode);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(
            [RentalMode.Short, RentalMode.Long],
            Assert.IsAssignableFrom<IEnumerable<PropertyResponse>>(ok.Value).Select(p => p.RentalMode));
        _service.Verify(x => x.GetPropertiesAsync(It.IsAny<HostScope>(), It.IsAny<RentalMode>()), Times.Never);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("medium")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("short,long")]
    public async Task GetAll_WithAValueThatIsNotAMode_Is400AndListsNothing(string mode)
    {
        var result = await _controller.GetAll(mode);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        Assert.Equal(ProblemCodes.ValidationError, details.Extensions["code"]?.ToString());
        _service.Verify(x => x.GetPropertiesAsync(It.IsAny<HostScope>()), Times.Never);
        _service.Verify(x => x.GetPropertiesAsync(It.IsAny<HostScope>(), It.IsAny<RentalMode>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_ModeFilter_KeepsTheScopeOfTheCaller()
    {
        // An org-wide role lists the whole org (no owner filter); the mode only narrows it.
        SetUser("auth0|manager", "PropertyManager");
        _service
            .Setup(x => x.GetPropertiesAsync(It.IsAny<HostScope>(), RentalMode.Long))
            .ReturnsAsync(new List<Property>());

        await _controller.GetAll("long");

        _service.Verify(x => x.GetPropertiesAsync(new HostScope(OrgId, null), RentalMode.Long), Times.Once);
    }

    // ─── Create: the compatibility rule ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, null, RentalMode.Long)]
    [InlineData(4, 120, null, RentalMode.Short)]
    [InlineData(0, 0, RentalMode.Short, RentalMode.Short)]
    [InlineData(3, 90, RentalMode.Long, RentalMode.Long)]
    public async Task Create_RentalMode_FollowsTheRequestOrTheCompatibilityRule(
        int maxGuests, double nightlyRate, RentalMode? sent, RentalMode expected)
    {
        Property? received = null;
        _service
            .Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync((Property p) =>
            {
                received = p;
                return p;
            });

        var result = await _controller.Create(new CreatePropertyRequest
        {
            Name = "Bilocale",
            Address = "Via Roma 1",
            City = "Monza",
            Bathrooms = 1,
            MaxGuests = maxGuests,
            NightlyRate = (decimal)nightlyRate,
            RentalMode = sent,
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(expected, Assert.IsType<Property>(created.Value).RentalMode);
        Assert.Equal(expected, received!.RentalMode);
    }

    [Fact]
    public void CreatePropertyRequest_RentalModeInTheBody_IsReadByName()
    {
        var request = JsonSerializer.Deserialize<CreatePropertyRequest>(
            """{ "name": "Bilocale", "address": "Via Roma 1", "city": "Monza", "bathrooms": 1, "rentalMode": "Long" }""",
            WebJson)!;
        var withoutMode = JsonSerializer.Deserialize<CreatePropertyRequest>(
            """{ "name": "Bilocale", "address": "Via Roma 1", "city": "Monza", "bathrooms": 1 }""",
            WebJson)!;

        Assert.Equal(RentalMode.Long, request.RentalMode);
        Assert.Null(withoutMode.RentalMode);
    }

    // ─── Update: accepted, never changed ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RentalMode.Short, RentalMode.Long)]
    [InlineData(RentalMode.Long, RentalMode.Short)]
    public async Task Update_RentalModeDifferentFromTheStoredOne_Is422AndSavesNothing(RentalMode stored, RentalMode requested)
    {
        var property = StoredProperty(stored);
        _service.Setup(x => x.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);

        var result = await _controller.Update(
            property.Id, new UpdatePropertyRequest { Name = "Rinominata", RentalMode = requested });

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);
        Assert.Equal("property_rental_mode_change_not_allowed", details.Extensions["code"]?.ToString());
        _service.Verify(x => x.UpdatePropertyAsync(It.IsAny<Property>()), Times.Never);
        Assert.Equal(stored, property.RentalMode);
        Assert.Equal("Casa", property.Name);
    }

    [Theory]
    [InlineData(RentalMode.Short)]
    [InlineData(RentalMode.Long)]
    public async Task Update_RentalModeEqualToTheStoredOne_SavesTheOtherFieldsAndKeepsTheMode(RentalMode stored)
    {
        // A form that sends the whole record back (it got rentalMode from the GET) keeps working.
        var property = StoredProperty(stored);
        _service.Setup(x => x.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);
        _service.Setup(x => x.UpdatePropertyAsync(It.IsAny<Property>())).ReturnsAsync(property);

        var result = await _controller.Update(
            property.Id, new UpdatePropertyRequest { Name = "Rinominata", RentalMode = stored });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Rinominata", property.Name);
        Assert.Equal(stored, property.RentalMode);
        _service.Verify(x => x.UpdatePropertyAsync(property), Times.Once);
    }

    [Theory]
    [InlineData(RentalMode.Short)]
    [InlineData(RentalMode.Long)]
    public async Task Update_WithoutRentalMode_KeepsTheStoredMode(RentalMode stored)
    {
        var property = StoredProperty(stored);
        _service.Setup(x => x.GetPropertyRecordAsync(property.Id)).ReturnsAsync(property);
        _service.Setup(x => x.UpdatePropertyAsync(It.IsAny<Property>())).ReturnsAsync(property);

        var result = await _controller.Update(property.Id, new UpdatePropertyRequest { Name = "Rinominata" });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(stored, property.RentalMode);
    }

    [Fact]
    public void UpdatePropertyRequest_ApplyTo_NeverTouchesTheRentalMode()
    {
        var property = StoredProperty(RentalMode.Short);

        JsonSerializer.Deserialize<UpdatePropertyRequest>("""{ "rentalMode": "Long", "name": "Altra" }""", WebJson)!
            .ApplyTo(property);

        Assert.Equal("Altra", property.Name);
        Assert.Equal(RentalMode.Short, property.RentalMode);
    }

    // ─── The records the host reads ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RentalMode.Short, "Short")]
    [InlineData(RentalMode.Long, "Long")]
    public void PropertyResponse_From_CarriesTheRentalModeByName(RentalMode mode, string name)
    {
        var response = PropertyResponse.From(StoredProperty(mode));

        Assert.Equal(mode, response.RentalMode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, WebJson));
        Assert.Equal(name, json.RootElement.GetProperty("rentalMode").GetString());
    }

    [Fact]
    public void PropertyDetailResponse_Serialized_CarriesTheRentalModeByName()
    {
        var detail = new PropertyDetailResponse { Id = Guid.NewGuid(), RentalMode = RentalMode.Long };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail, WebJson));

        Assert.Equal("Long", json.RootElement.GetProperty("rentalMode").GetString());
    }

    private static Property StoredProperty(RentalMode mode) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = OrgId,
        OwnerId = "auth0|host",
        Name = "Casa",
        RentalMode = mode,
    };

    private void SetUser(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new("sub", userId) };
        claims.AddRange(roles.Select(role => new Claim("https://casazen.app/roles", role)));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            },
        };
    }
}
