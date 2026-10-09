using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.DTOs;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-01 (decisions D16 and D19): the values of <see cref="RentalMode"/> are a storage contract, the compatibility rule
/// turns the old implicit marker (no guests, no rate) into a mode only for a request that does not say it, and the short
/// rent predicate is the same everywhere it is repeated.
/// </summary>
public class PropertyRentalModeRulesTests
{
    [Fact]
    public void RentalMode_Values_AreTheStoredIntegersAndAppendOnly()
    {
        // Stored as an integer in Properties.RentalMode: never renumber or reuse a value (a future Both = 2 has room).
        Assert.Equal(0, (int)RentalMode.Short);
        Assert.Equal(1, (int)RentalMode.Long);
        Assert.Equal(["Short", "Long"], Enum.GetNames<RentalMode>());
    }

    [Fact]
    public void Property_New_IsShortRent()
    {
        // The default of the column: every row that existed before PM-01 keeps working as a short-stay property.
        Assert.Equal(RentalMode.Short, new Property().RentalMode);
    }

    [Theory]
    // No mode: the long-rent form sends no guests and no rate (A7-06) = a long-term property.
    [InlineData(null, 0, 0, RentalMode.Long)]
    // No mode and any guest count or any rate = short stays, whatever the other one is.
    [InlineData(null, 4, 100, RentalMode.Short)]
    [InlineData(null, 2, 0, RentalMode.Short)]
    [InlineData(null, 0, 80.5, RentalMode.Short)]
    [InlineData(null, 0, 0.01, RentalMode.Short)]
    // A mode that is sent is used as it is, whatever the marker says.
    [InlineData(RentalMode.Short, 0, 0, RentalMode.Short)]
    [InlineData(RentalMode.Long, 0, 0, RentalMode.Long)]
    [InlineData(RentalMode.Long, 6, 150, RentalMode.Long)]
    [InlineData(RentalMode.Short, 6, 150, RentalMode.Short)]
    public void ResolveForCreation_Matrix(RentalMode? requested, int maxGuests, double nightlyRate, RentalMode expected)
    {
        Assert.Equal(expected, PropertyRentalModeRules.ResolveForCreation(requested, maxGuests, (decimal)nightlyRate));
    }

    [Fact]
    public void ToProperty_WithoutModeAndWithoutGuestsAndRate_CreatesALongPropertyLikeTheLongRentFormAlwaysDid()
    {
        var property = NewRequest(maxGuests: 0, nightlyRate: 0, mode: null).ToProperty("auth0|owner");

        Assert.Equal(RentalMode.Long, property.RentalMode);
    }

    [Fact]
    public void ToProperty_WithoutModeAndWithGuestsAndRate_CreatesAShortProperty()
    {
        var property = NewRequest(maxGuests: 4, nightlyRate: 120, mode: null).ToProperty("auth0|owner");

        Assert.Equal(RentalMode.Short, property.RentalMode);
    }

    [Fact]
    public void ToProperty_WithExplicitShortModeAndNoGuestsNorRate_KeepsTheShortModeTheClientAskedFor()
    {
        // A short-rent client that creates the property now and sets the rate later says so: the marker is not guessed.
        var property = NewRequest(maxGuests: 0, nightlyRate: 0, mode: RentalMode.Short).ToProperty("auth0|owner");

        Assert.Equal(RentalMode.Short, property.RentalMode);
    }

    [Fact]
    public void ToProperty_WithExplicitLongModeAndARate_KeepsTheLongMode()
    {
        var property = NewRequest(maxGuests: 2, nightlyRate: 50, mode: RentalMode.Long).ToProperty("auth0|owner");

        Assert.Equal(RentalMode.Long, property.RentalMode);
    }

    [Fact]
    public void IsShortRent_Expression_FollowsTheMode()
    {
        var isShort = PropertyRentalModeRules.IsShortRent.Compile();

        Assert.True(isShort(new Property { RentalMode = RentalMode.Short }));
        Assert.False(isShort(new Property { RentalMode = RentalMode.Long }));
        Assert.True(PropertyRentalModeRules.IsShort(new Property { RentalMode = RentalMode.Short }));
        Assert.False(PropertyRentalModeRules.IsShort(new Property { RentalMode = RentalMode.Long }));
    }

    [Fact]
    public void EnsureShortRent_ShortProperty_DoesNothing()
    {
        PropertyRentalModeRules.EnsureShortRent(new Property { RentalMode = RentalMode.Short });
    }

    [Fact]
    public void EnsureShortRent_LongProperty_Throws422WithTheStableCodeAndTheLocalizedKey()
    {
        var error = Assert.Throws<DomainRuleException>(
            () => PropertyRentalModeRules.EnsureShortRent(new Property { RentalMode = RentalMode.Long }));

        Assert.Equal("property_not_bookable_in_long_mode", error.Code);
        Assert.Equal(PropertyRentalModeErrorCodes.NotBookableInLongMode, error.Code);
        Assert.Equal("PropertyNotBookableInLongMode", error.MessageKey);
    }

    [Theory]
    [InlineData(RentalMode.Short, true)]
    [InlineData(RentalMode.Long, false)]
    public void PublicListingIsPublished_OtherwisePublishedProperty_FollowsTheShortRentPredicate(RentalMode mode, bool published)
    {
        // The public listing repeats the mode comparison of IsShortRent inside its own expression; keep them together.
        var isPublished = PublicListing.IsPublished.Compile();
        var property = new Property
        {
            RentalMode = mode,
            IsActive = true,
            IsPaused = false,
            ComplianceStatus = PropertyComplianceStatus.Active,
        };

        Assert.Equal(published, isPublished(property));
        Assert.Equal(PropertyRentalModeRules.IsShortRent.Compile()(property), isPublished(property));
    }

    [Fact]
    public void PublicListingIsPublished_EveryOtherCondition_StillAppliesToAShortProperty()
    {
        var isPublished = PublicListing.IsPublished.Compile();
        var published = new Property
        {
            RentalMode = RentalMode.Short,
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
        };

        Assert.True(isPublished(published));
        Assert.False(isPublished(Copy(published, p => p.IsActive = false)));
        Assert.False(isPublished(Copy(published, p => p.IsPaused = true)));
        Assert.False(isPublished(Copy(published, p => p.ComplianceStatus = PropertyComplianceStatus.Pending)));
        Assert.False(isPublished(Copy(published, p => p.ComplianceStatus = PropertyComplianceStatus.Suspended)));
    }

    [Theory]
    [InlineData("SharedResources.resx")]
    [InlineData("SharedResources.en.resx")]
    public void ResourceFiles_NewKeys_AreInBothLanguagesWithReadableText(string file)
    {
        var path = Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Resources", file);
        var texts = XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

        foreach (var key in new[]
                 {
                     "PropertyNotBookableInLongMode",
                     "PropertyRentalModeChangeNotAllowed",
                     "PropertyListModeInvalid",
                 })
        {
            Assert.True(texts.TryGetValue(key, out var text), $"{key} is missing from {file}");
            Assert.True(text!.Length > 20 && text != key, $"{key} has no real text in {file}");
        }
    }

    private static Property Copy(Property source, Action<Property> change)
    {
        var copy = new Property
        {
            RentalMode = source.RentalMode,
            IsActive = source.IsActive,
            IsPaused = source.IsPaused,
            ComplianceStatus = source.ComplianceStatus,
        };
        change(copy);
        return copy;
    }

    private static CreatePropertyRequest NewRequest(int maxGuests, decimal nightlyRate, RentalMode? mode) => new()
    {
        Name = "Bilocale",
        Address = "Via Roma 1",
        City = "Monza",
        Bathrooms = 1,
        MaxGuests = maxGuests,
        NightlyRate = nightlyRate,
        RentalMode = mode,
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
