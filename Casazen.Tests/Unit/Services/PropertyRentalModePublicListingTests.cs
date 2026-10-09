using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-01: a property in long-term mode is not published. <see cref="PublicListing.IsPublished"/> feeds the public search,
/// the property page, the availability, the SEO featured properties, the sitemaps and the activation checklist, so each
/// of them answers as if the property did not exist (404), whatever its other state; the same property in short-rent mode
/// stays visible. One test for each reader of the rule.
/// </summary>
public class PropertyRentalModePublicListingTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly OrgEntity _org;

    public PropertyRentalModePublicListingTests()
    {
        _org = new OrgEntity
        {
            Name = "casa-lago",
            Slug = "casa-lago",
            DisplayName = "Casa Lago",
            ContactEmail = "host@casa-lago.test",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        _db.Orgs.Add(_org);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SearchAsync_LongProperty_IsNotOffered()
    {
        AddProperty("Casa Breve", RentalMode.Short);
        AddProperty("Bilocale Lungo", RentalMode.Long);

        var found = (await NewPropertyService().SearchAsync(PublicPropertySearchCriteria.None)).Select(p => p.Name).ToList();

        Assert.Equal(["Casa Breve"], found);
    }

    [Fact]
    public async Task SearchByOrgAsync_LongProperty_IsNotOnTheOrgSite()
    {
        AddProperty("Casa Breve", RentalMode.Short);
        AddProperty("Bilocale Lungo", RentalMode.Long);

        var found = (await NewPropertyService().SearchByOrgAsync(_org.Id)).Select(p => p.Name).ToList();

        Assert.Equal(["Casa Breve"], found);
    }

    [Fact]
    public async Task GetPublicPropertyAsync_LongProperty_IsNotFoundById()
    {
        var shortStay = AddProperty("Casa Breve", RentalMode.Short);
        var longTerm = AddProperty("Bilocale Lungo", RentalMode.Long);
        var service = NewPropertyService();

        Assert.NotNull(await service.GetPublicPropertyAsync(shortStay.Id));
        Assert.Null(await service.GetPublicPropertyAsync(longTerm.Id));
    }

    [Fact]
    public async Task GetPublicPropertyForOrgAsync_LongProperty_IsNotFoundByIdNorBySlug()
    {
        AddProperty("Casa Breve", RentalMode.Short);
        var longTerm = AddProperty("Bilocale Lungo", RentalMode.Long);
        var service = NewPropertyService();

        Assert.NotNull(await service.GetPublicPropertyForOrgAsync("casa-breve", _org.Id));
        Assert.Null(await service.GetPublicPropertyForOrgAsync(longTerm.Id.ToString(), _org.Id));
        Assert.Null(await service.GetPublicPropertyForOrgAsync(longTerm.Slug!, _org.Id));
    }

    [Fact]
    public async Task GetAsync_PublicAvailability_OfALongProperty_Is404WithTheStableCode()
    {
        var shortStay = AddProperty("Casa Breve", RentalMode.Short);
        var longTerm = AddProperty("Bilocale Lungo", RentalMode.Long);
        var from = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = new PublicAvailabilityService(_db, new ConfigurationBuilder().Build());

        var available = await service.GetAsync(shortStay.Id, from, from.AddDays(7));
        var error = await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(longTerm.Id, from, from.AddDays(7)));

        Assert.Empty(available.BookedNights);
        Assert.Equal(PublicAvailabilityErrorCodes.PropertyNotFound, error.Code);
        Assert.Equal("PublicPropertyNotFound", error.MessageKey);
    }

    [Fact]
    public async Task GetAsync_SeoFeaturedProperties_LeavesTheLongPropertyOut()
    {
        AddProperty("Casa Breve", RentalMode.Short, city: "Como");
        AddProperty("Bilocale Lungo", RentalMode.Long, city: "Como");
        var catalog = new Mock<ISeoComuneCatalog>();
        catalog
            .Setup(c => c.ResolveSlugOrCodeAsync("como", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComuneInfo("013075", "Como", "LOM", "lombardia", "como"));

        var result = await new SeoFeaturedPropertiesService(_db, catalog.Object).GetAsync("como");

        Assert.Equal(["Casa Breve"], result!.Properties.Select(p => p.Name));
    }

    [Fact]
    public async Task LoadPropertyFactsAsync_ActivationChecklist_CountsOnlyTheShortRentProperties()
    {
        // The checklist is the short-rent one (booking site, CIN, activation): a long-term property is not "created", has
        // no CIN to wait for and is not waiting for the compliance activation.
        AddProperty("Casa Breve", RentalMode.Short, cinCode: "IT058091C27G5FFZDZ");
        AddProperty("Casa Breve 2", RentalMode.Short, cinCode: null, published: false);
        AddProperty("Bilocale Lungo", RentalMode.Long, cinCode: null, published: false);
        AddProperty("Trilocale Lungo", RentalMode.Long, cinCode: "IT058091C27G5FFZDZ");
        var service = new OnboardingService(
            _db,
            Mock.Of<ILegalDocumentService>(),
            Mock.Of<IUserAuthorizationCache>(),
            EmailTestHelpers.Links());

        var facts = await service.LoadPropertyFactsAsync(_org.Id, CancellationToken.None);

        Assert.Equal(2, facts.Total);
        Assert.Equal(1, facts.WithValidCin);
        Assert.Equal(1, facts.Published);
        Assert.Equal(0, facts.PausedByHost);
        Assert.Equal(1, facts.ComplianceNotActive);
    }

    [Fact]
    public async Task LoadPropertyFactsAsync_OrgOnlyWithLongProperties_HasNoShortRentProperty()
    {
        AddProperty("Bilocale Lungo", RentalMode.Long);
        var service = new OnboardingService(
            _db,
            Mock.Of<ILegalDocumentService>(),
            Mock.Of<IUserAuthorizationCache>(),
            EmailTestHelpers.Links());

        var facts = await service.LoadPropertyFactsAsync(_org.Id, CancellationToken.None);

        Assert.Equal(ActivationPropertyFacts.None, facts);
    }

    private PropertyService NewPropertyService() =>
        new(
            new PropertyRepository(_db),
            Mock.Of<IPropertyComplianceStatusService>(),
            new CinDeadlineCalendar(Options.Create(new CinOptions()), TimeProvider.System),
            new Mock<ILogger<PropertyService>>().Object);

    /// <summary>A property that is published whatever its mode says (active, not paused, compliance activated).</summary>
    private Property AddProperty(
        string name,
        RentalMode mode,
        string city = "Como",
        string? cinCode = "IT058091C27G5FFZDZ",
        bool published = true)
    {
        var property = new Property
        {
            OwnerId = "auth0|owner",
            OrgId = _org.Id,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            Address = $"Via {name} 1",
            City = city,
            PostalCode = "22100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = cinCode,
            IsActive = true,
            RentalMode = mode,
            ComplianceStatus = published ? PropertyComplianceStatus.Active : PropertyComplianceStatus.Pending,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }
}
