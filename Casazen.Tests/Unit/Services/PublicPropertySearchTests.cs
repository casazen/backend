using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The public property search (BK-20, A8-13): every filter the console offers is applied by the API, each result carries
/// the slug of its org (the link to its booking site), and a property of an inactive org is never offered.
/// </summary>
public class PublicPropertySearchTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private readonly PropertyService _service;
    private readonly OrgEntity _org;

    public PublicPropertySearchTests()
    {
        _service = new PropertyService(
            new PropertyRepository(_db),
            Mock.Of<IPropertyComplianceStatusService>(),
            new CinDeadlineCalendar(Options.Create(new CinOptions()), TimeProvider.System),
            new Mock<ILogger<PropertyService>>().Object);
        _org = AddOrg("villa-parco");
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private OrgEntity AddOrg(string slug, bool isActive = true)
    {
        var org = new OrgEntity
        {
            Name = slug,
            Slug = slug,
            DisplayName = slug,
            ContactEmail = $"{slug}@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = isActive,
        };
        _db.Orgs.Add(org);
        return org;
    }

    private Property AddProperty(
        string name,
        OrgEntity? org = null,
        string city = "Como",
        decimal rate = 100m,
        int bedrooms = 2,
        int bathrooms = 1,
        int guests = 4,
        bool published = true)
    {
        var property = new Property
        {
            OwnerId = $"auth0|{Guid.NewGuid():N}",
            OrgId = (org ?? _org).Id,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            Address = $"Via {name} 1",
            City = city,
            NightlyRate = rate,
            Bedrooms = bedrooms,
            Bathrooms = bathrooms,
            MaxGuests = guests,
            IsActive = true,
            ComplianceStatus = published ? PropertyComplianceStatus.Active : PropertyComplianceStatus.Pending,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private async Task<List<string>> SearchNamesAsync(PublicPropertySearchCriteria criteria) =>
        (await _service.SearchAsync(criteria)).Select(p => p.Name).Order().ToList();

    [Fact]
    public async Task SearchAsync_Results_CarryTheSlugOfTheirOrgAndOfTheProperty()
    {
        var other = AddOrg("casa-mare");
        _db.SaveChanges();
        AddProperty("Casa Faro", other);

        var result = (await _service.SearchAsync(PublicPropertySearchCriteria.None)).Single();

        Assert.Equal("casa-mare", result.OrgSlug);
        Assert.Equal("casa-faro", result.Slug);
    }

    [Fact]
    public async Task SearchByOrgAsync_AndTheDetailReads_CarryTheOrgSlugToo()
    {
        var property = AddProperty("Casa Lago");

        var list = (await _service.SearchByOrgAsync(_org.Id)).Single();
        var detail = await _service.GetPublicPropertyForOrgAsync("casa-lago", _org.Id);
        var detailById = await _service.GetPublicPropertyAsync(property.Id);

        Assert.Equal("villa-parco", list.OrgSlug);
        Assert.Equal("villa-parco", detail!.OrgSlug);
        Assert.Equal("villa-parco", detailById!.OrgSlug);
    }

    [Fact]
    public async Task SearchAsync_NoCriteria_ReturnsEveryPublishedProperty()
    {
        AddProperty("Uno");
        AddProperty("Due");
        AddProperty("Bozza", published: false);

        Assert.Equal(["Due", "Uno"], await SearchNamesAsync(PublicPropertySearchCriteria.None));
    }

    [Fact]
    public async Task SearchAsync_City_IsAPartOfTheNameIgnoringCaseAndOuterSpaces()
    {
        AddProperty("Lago", city: "Como");
        AddProperty("Colli", city: "Bergamo");

        Assert.Equal(["Lago"], await SearchNamesAsync(new PublicPropertySearchCriteria { City = "  oMO " }));
        Assert.Equal(["Colli", "Lago"], await SearchNamesAsync(new PublicPropertySearchCriteria { City = "   " }));
    }

    [Fact]
    public async Task SearchAsync_PriceRange_IsInclusiveAtBothEnds()
    {
        AddProperty("Economica", rate: 50m);
        AddProperty("Media", rate: 100m);
        AddProperty("Lusso", rate: 300m);

        Assert.Equal(["Media"], await SearchNamesAsync(new PublicPropertySearchCriteria { MinPrice = 100m, MaxPrice = 100m }));
        Assert.Equal(["Lusso", "Media"], await SearchNamesAsync(new PublicPropertySearchCriteria { MinPrice = 100m }));
        Assert.Equal(["Economica", "Media"], await SearchNamesAsync(new PublicPropertySearchCriteria { MaxPrice = 100m }));
        Assert.Empty(await SearchNamesAsync(new PublicPropertySearchCriteria { MinPrice = 200m, MaxPrice = 100m }));
    }

    [Fact]
    public async Task SearchAsync_BedroomsBathroomsAndGuests_AreMinimums()
    {
        AddProperty("Monolocale", bedrooms: 1, bathrooms: 1, guests: 2);
        AddProperty("Trilocale", bedrooms: 3, bathrooms: 2, guests: 6);

        Assert.Equal(["Trilocale"], await SearchNamesAsync(new PublicPropertySearchCriteria { MinBedrooms = 2 }));
        Assert.Equal(["Trilocale"], await SearchNamesAsync(new PublicPropertySearchCriteria { MinBathrooms = 2 }));
        Assert.Equal(["Trilocale"], await SearchNamesAsync(new PublicPropertySearchCriteria { Guests = 3 }));
        Assert.Equal(["Monolocale", "Trilocale"], await SearchNamesAsync(new PublicPropertySearchCriteria { Guests = 2, MinBedrooms = 1 }));
    }

    [Fact]
    public async Task SearchAsync_AllFiltersTogether_AreCombinedWithAnd()
    {
        AddProperty("Giusta", city: "Como", rate: 120m, bedrooms: 2, bathrooms: 1, guests: 4);
        AddProperty("TroppoCara", city: "Como", rate: 400m, bedrooms: 2, bathrooms: 1, guests: 4);
        AddProperty("AltraCitta", city: "Lecco", rate: 120m, bedrooms: 2, bathrooms: 1, guests: 4);
        AddProperty("TroppoPiccola", city: "Como", rate: 120m, bedrooms: 1, bathrooms: 1, guests: 2);

        var result = await SearchNamesAsync(new PublicPropertySearchCriteria
        {
            City = "como",
            MinPrice = 100m,
            MaxPrice = 200m,
            MinBedrooms = 2,
            MinBathrooms = 1,
            Guests = 3,
        });

        Assert.Equal(["Giusta"], result);
    }

    [Fact]
    public async Task SearchAsync_PropertyOfAnInactiveOrg_IsNeverOffered()
    {
        var inactive = AddOrg("casa-chiusa", isActive: false);
        _db.SaveChanges();
        AddProperty("Aperta");
        AddProperty("Chiusa", inactive);

        Assert.Equal(["Aperta"], await SearchNamesAsync(PublicPropertySearchCriteria.None));
        Assert.Empty(await _service.SearchByOrgAsync(inactive.Id));
        Assert.Null(await _service.GetPublicPropertyForOrgAsync("chiusa", inactive.Id));
    }

    [Fact]
    public async Task SearchAsync_ResultsAreCheapestFirstWithinACity()
    {
        AddProperty("Cara", city: "Como", rate: 200m);
        AddProperty("Economica", city: "Como", rate: 60m);

        var names = (await _service.SearchAsync(new PublicPropertySearchCriteria { City = "Como" })).Select(p => p.Name).ToList();

        Assert.Equal(["Economica", "Cara"], names);
    }
}
