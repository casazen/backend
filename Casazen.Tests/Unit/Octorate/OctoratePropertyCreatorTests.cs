using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the creation of a property on Octorate (E02/F03/US01/T06, section 9.1, PO-19, D6): the refresh token that Octorate gives once is
/// saved before anything else can fail (data rule); a property in a proposed or ambiguous row is not created, because it may already exist on Octorate
/// (data rule, OCT-04); a property declared <c>onOctorate</c> or <c>onOtas</c> never reaches the create operation, even when no mapping exists (data
/// rule, D6). Over an in-memory database and a strict client: a call nobody set up fails the test.
/// </summary>
public class OctoratePropertyCreatorTests
{
    private static readonly Guid OrgId = Guid.Parse("6d1c0a2e-3b4f-4c5d-8e6f-7a8b9c0d1e21");
    private const string Publisher = "auth0|publisher";
    private const string OwnerDomain = "owners.casazen.test";

    private readonly string _database = $"octorate-creator-{Guid.NewGuid():N}";
    private readonly Mock<IOctorateApiClient> _client = new(MockBehavior.Strict);
    private readonly Mock<IOctorateTokenService> _tokens = new(MockBehavior.Strict);
    private readonly Mock<IChannelMappingService> _mappings = new(MockBehavior.Strict);
    private readonly Mock<IHostScopeResolver> _scope = new();
    private readonly List<string> _order = [];

    public OctoratePropertyCreatorTests()
    {
        _scope
            .Setup(s => s.CanReachPropertyAsync(Publisher, It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    [Fact]
    public async Task OctoratePropertyCreator_RefreshTokenSavedFirst_Test()
    {
        var propertyId = await SeedAsync(OctorateInventoryScenario.NewInventory);
        _mappings.Setup(m => m.IsWriteBlockedAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _client
            .Setup(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("create"))
            .ReturnsAsync(new OctoratePropertyCreated("585492", "refresh-token-once"));
        _tokens
            .Setup(t => t.SaveTokensAsync(
                OrgId,
                It.Is<OctorateTokenResponse>(r => r.RefreshToken == "refresh-token-once" && r.AccessToken == null),
                It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("save-token"))
            .Returns(Task.CompletedTask);

        // The next step fails: the token must already be stored.
        _mappings
            .Setup(m => m.RefreshAsync(OrgId, It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("refresh-mapping"))
            .ThrowsAsync(new OctorateServerException(500));

        await Assert.ThrowsAsync<OctorateServerException>(() => Creator().CreateAsync(propertyId, Publisher, CancellationToken.None));

        Assert.Equal(["create", "save-token", "refresh-mapping"], _order);
        _tokens.Verify(t => t.SaveTokensAsync(OrgId, It.IsAny<OctorateTokenResponse>(), It.IsAny<CancellationToken>()), Times.Once);
        _mappings.Verify(
            m => m.ConfirmAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()),
            Times.Never);
    }

    [Theory]
    [InlineData(OctorateMappingStatus.Proposed)]
    [InlineData(OctorateMappingStatus.Ambiguous)]
    public async Task OctoratePropertyCreator_UnconfirmedMapping_Refused_Test(OctorateMappingStatus status)
    {
        var propertyId = await SeedAsync(OctorateInventoryScenario.NewInventory);
        await using (var db = NewDb())
        {
            var connection = new OctorateConnection { OrgId = OrgId, Environment = OctorateEnvironment.Test, Status = OctorateConnectionStatus.Connected };
            db.OctorateConnections.Add(connection);
            db.OctorateListingMappings.Add(new OctorateListingMapping
            {
                OrgId = OrgId,
                ConnectionId = connection.Id,
                OctorateAccommodationId = "777001",
                Status = status,
                PropertyId = status == OctorateMappingStatus.Proposed ? propertyId : null,
                CandidatePropertyIdsJson = status == OctorateMappingStatus.Ambiguous ? $"[\"{propertyId}\"]" : OctorateListingMapping.EmptyJsonArray,
            });
            await db.SaveChangesAsync();
        }

        // The real rule of the mapping, not a stub: the property may already exist on Octorate and has to be confirmed or rejected first.
        await using var rules = NewDb();
        var creator = Creator(mappings: MappingService(rules));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => creator.CreateAsync(propertyId, Publisher, CancellationToken.None));

        Assert.Equal("octorate_mapping_unconfirmed", ex.Code);
        _client.Verify(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()), Times.Never);
        _tokens.Verify(t => t.SaveTokensAsync(It.IsAny<Guid>(), It.IsAny<OctorateTokenResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(OctorateInventoryScenario.OnOctorate)]
    [InlineData(OctorateInventoryScenario.OnOtas)]
    [InlineData(null)]
    public async Task OctoratePropertyCreator_ExistingInventory_NeverCreated_Test(OctorateInventoryScenario? scenario)
    {
        // No confirmed mapping, no unconfirmed row: the absence of a mapping is not proof that the inventory is new.
        var propertyId = await SeedAsync(scenario);
        _mappings.Setup(m => m.IsWriteBlockedAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => Creator().CreateAsync(propertyId, Publisher, CancellationToken.None));

        Assert.Equal("octorate_existing_inventory_must_import", ex.Code);
        _client.Verify(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(c => c.GetPropertiesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _tokens.Verify(t => t.SaveTokensAsync(It.IsAny<Guid>(), It.IsAny<OctorateTokenResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_ANewProperty_SendsTheDataOfCasaZenWithTheCasaZenAddressAndConfirmsTheMapping()
    {
        var propertyId = await SeedAsync(OctorateInventoryScenario.NewInventory);
        _mappings.Setup(m => m.IsWriteBlockedAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        OctoratePropertyCreate? sent = null;
        _client
            .Setup(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()))
            .Callback<OctoratePropertyCreate, CancellationToken>((request, _) => sent = request)
            .ReturnsAsync(new OctoratePropertyCreated("585492", "refresh-token-once"));
        _tokens.Setup(t => t.SaveTokensAsync(OrgId, It.IsAny<OctorateTokenResponse>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mappings.Setup(m => m.RefreshAsync(OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ChannelMappingRow>());
        _mappings
            .Setup(m => m.ConfirmAsync(OrgId, "585492", propertyId, null, Publisher, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlySet<string>?>()))
            .Returns(Task.CompletedTask);

        var accommodation = await Creator().CreateAsync(propertyId, Publisher, CancellationToken.None);

        Assert.Equal("585492", accommodation);
        Assert.NotNull(sent);
        Assert.Equal("IT", sent.Country);
        Assert.Equal("EUR", sent.Currency);
        Assert.Equal($"octorate-{OrgId:N}@{OwnerDomain}", sent.AlternativeEmail);
        Assert.NotEqual("host@example.com", sent.AlternativeEmail);
        Assert.Equal("host@example.com", sent.ContactEmail);
        Assert.Equal("Giulia", sent.ContactFirstName);
        Assert.Null(sent.NetworkName);
        Assert.DoesNotContain("host@example.com", sent.ToString());
    }

    [Fact]
    public async Task CreateAsync_APropertyWithoutPositionOrContact_IsRefusedByFieldBeforeAnyCall()
    {
        var propertyId = await SeedAsync(OctorateInventoryScenario.NewInventory, latitude: 0m, longitude: 0m, contactPhone: null);
        _mappings.Setup(m => m.IsWriteBlockedAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Creator().CreateAsync(propertyId, Publisher, CancellationToken.None));

        Assert.Equal("octorate_property_invalid", ex.Code);
        var fields = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ex.Extensions["fields"]).Cast<object>().Select(f => f.ToString()!).ToList();
        Assert.Contains(fields, f => f.Contains("location", StringComparison.Ordinal));
        Assert.Contains(fields, f => f.Contains("contact.phone", StringComparison.Ordinal));
        _client.Verify(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WhenOctorateRefusesTheData_AnswersTheFieldsItNames()
    {
        var propertyId = await SeedAsync(OctorateInventoryScenario.NewInventory);
        _mappings.Setup(m => m.IsWriteBlockedAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _client
            .Setup(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctoratePropertyRejectedException([new OctorateFieldError("zipCode", "not valid")]));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => Creator().CreateAsync(propertyId, Publisher, CancellationToken.None));

        Assert.Equal("octorate_property_invalid", ex.Code);
        Assert.Contains("zipCode", string.Join(' ', Assert.IsAssignableFrom<System.Collections.IEnumerable>(ex.Extensions["fields"]).Cast<object>()), StringComparison.Ordinal);
        _tokens.Verify(t => t.SaveTokensAsync(It.IsAny<Guid>(), It.IsAny<OctorateTokenResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_APropertyOutOfTheReachOfThePerson_IsNotFound()
    {
        var propertyId = await SeedAsync(OctorateInventoryScenario.NewInventory);
        _scope
            .Setup(s => s.CanReachPropertyAsync("auth0|stranger", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<NotFoundException>(() => Creator().CreateAsync(propertyId, "auth0|stranger", CancellationToken.None));

        _client.Verify(c => c.CreatePropertyAsync(It.IsAny<OctoratePropertyCreate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private OctoratePropertyCreator Creator(IChannelMappingService? mappings = null)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        return new OctoratePropertyCreator(
            NewDb(),
            _client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            _tokens.Object,
            mappings ?? _mappings.Object,
            new InventoryScenarioGuard(NewDb()),
            _scope.Object,
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions { OwnerEmailDomain = OwnerDomain }),
            host.Object,
            NullLogger<OctoratePropertyCreator>.Instance);
    }

    private ChannelMappingService MappingService(AppDbContext db)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        var clock = new Casazen.Tests.Unit.FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        return new ChannelMappingService(
            db,
            _client.Object,
            new OctorateOrgScope(NullTenantContext.Instance),
            new OctorateRateLimiter(clock),
            Mock.Of<IHostScopeResolver>(),
            Mock.Of<IPropertyService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IInternalActivityService>(),
            Microsoft.Extensions.Options.Options.Create(new OctorateOptions()),
            host.Object,
            clock,
            NullLogger<ChannelMappingService>.Instance);
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options, NullTenantContext.Instance);

    /// <summary>An org with a contact, the person who publishes, and a property with all the data Octorate asks for, declared as <paramref name="scenario"/>.</summary>
    private async Task<Guid> SeedAsync(
        OctorateInventoryScenario? scenario,
        decimal latitude = 45.4642m,
        decimal longitude = 9.19m,
        string? contactPhone = "+390212345678")
    {
        await using var db = NewDb();
        if (!await db.Orgs.AnyAsync(o => o.Id == OrgId))
        {
            db.Orgs.Add(new OrgEntity
            {
                Id = OrgId,
                Name = "Org test",
                Slug = "org-test",
                DisplayName = "Org test",
                ContactEmail = "host@example.com",
                ContactPhone = contactPhone,
            });
            db.Users.Add(new User { Id = Publisher, Email = "giulia@example.com", FirstName = "Giulia", LastName = "Rossi", OrgId = OrgId });
        }

        var property = new Property
        {
            OrgId = OrgId,
            OwnerId = Publisher,
            Name = "Casa del Lago",
            Address = "Via del Lago 1",
            City = "Como",
            PostalCode = "22100",
            Country = "IT",
            Latitude = latitude,
            Longitude = longitude,
            Currency = "EUR",
            MaxGuests = 4,
        };
        db.Properties.Add(property);
        if (scenario is { } declared)
            db.OctorateInventoryIntents.Add(new OctorateInventoryIntent { OrgId = OrgId, PropertyId = property.Id, Scenario = declared, ActorUserId = Publisher });

        await db.SaveChangesAsync();
        return property.Id;
    }
}
