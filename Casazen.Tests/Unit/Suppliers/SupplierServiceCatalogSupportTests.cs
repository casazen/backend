using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-02: the weekday mask, the JSON columns and the texts of the supplier's service catalog.</summary>
public class SupplierServiceCatalogSupportTests
{
    // ─── Weekdays ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DayOfWeek.Monday, 0b0000001)]
    [InlineData(DayOfWeek.Tuesday, 0b0000010)]
    [InlineData(DayOfWeek.Wednesday, 0b0000100)]
    [InlineData(DayOfWeek.Thursday, 0b0001000)]
    [InlineData(DayOfWeek.Friday, 0b0010000)]
    [InlineData(DayOfWeek.Saturday, 0b0100000)]
    [InlineData(DayOfWeek.Sunday, 0b1000000)]
    public void ToMask_EachDayIsItsOwnBit_MondayFirstSundayLast(DayOfWeek day, int expected)
    {
        Assert.Equal(expected, SupplierServiceWeekdays.ToMask([day]));
        Assert.True(SupplierServiceWeekdays.IsOffered(expected, day));
    }

    [Fact]
    public void ToMask_EveryDay_IsTheAllMask()
    {
        Assert.Equal(SupplierServiceWeekdays.AllMask, SupplierServiceWeekdays.ToMask(Enum.GetValues<DayOfWeek>()));
        Assert.Equal(127, SupplierServiceWeekdays.AllMask);
    }

    [Fact]
    public void FromMask_ListsTheDaysMondayFirst_AndRoundTrips()
    {
        var days = SupplierServiceWeekdays.FromMask(0b1100101);

        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday }, days);
        Assert.Equal(0b1100101, SupplierServiceWeekdays.ToMask(days));
        Assert.Empty(SupplierServiceWeekdays.FromMask(0));
        Assert.Equal(7, SupplierServiceWeekdays.FromMask(127).Count);
    }

    [Fact]
    public void FromMask_BitsAboveSunday_AreIgnored()
    {
        Assert.Equal(new[] { DayOfWeek.Monday }, SupplierServiceWeekdays.FromMask(0b1000_0001));
        Assert.Equal(7, SupplierServiceWeekdays.FromMask(0xFFFF).Count);
    }

    [Fact]
    public void IsOffered_ADayOutsideTheMask_IsFalse()
    {
        var weekdaysOnly = SupplierServiceWeekdays.ToMask(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]);

        Assert.False(SupplierServiceWeekdays.IsOffered(weekdaysOnly, DayOfWeek.Sunday));
        Assert.False(SupplierServiceWeekdays.IsOffered(weekdaysOnly, DayOfWeek.Saturday));
        Assert.True(SupplierServiceWeekdays.IsOffered(weekdaysOnly, DayOfWeek.Friday));
    }

    // ─── JSON columns ────────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_Supplements_UsesTheCamelCaseNamesOfTheSpecAndOmitsAMissingMax()
    {
        var json = SupplierServiceListingJson.Serialize(
        [
            new SupplierServiceSupplement("bagno", "Bagno", 1000, "bathroom", 3),
            new SupplierServiceSupplement("set", "Set", 1200, "set", null),
        ]);

        Assert.Equal(
            """[{"code":"bagno","label":"Bagno","amountCents":1000,"per":"bathroom","max":3},{"code":"set","label":"Set","amountCents":1200,"per":"set"}]""",
            json);
    }

    [Fact]
    public void ReadSupplements_RoundTripsWhatSerializeWrote()
    {
        var supplements = new[]
        {
            new SupplierServiceSupplement("bagno", "Ogni bagno in più", 1000, "bathroom", 3),
            new SupplierServiceSupplement("m2", "Oltre 60 m² «ogni 30»", 500, "sqm30", null),
        };

        var read = SupplierServiceListingJson.ReadSupplements(SupplierServiceListingJson.Serialize(supplements));

        Assert.Equal(supplements, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void ReadStrings_MissingOrUnreadable_IsEmpty(string? json)
    {
        Assert.Empty(SupplierServiceListingJson.ReadStrings(json));
    }

    [Fact]
    public void ReadStrings_ASimpleArray_IsReadInOrder()
    {
        Assert.Equal(new[] { "uno", "due" }, SupplierServiceListingJson.ReadStrings("""["uno","due"]"""));
        Assert.Equal(new[] { "uno", "due" }, SupplierServiceListingJson.ReadStrings(SupplierServiceListingJson.Serialize(new[] { "uno", "due" })));
    }

    [Fact]
    public void ReadSupplements_AnArrayWithANull_SkipsIt()
    {
        var read = SupplierServiceListingJson.ReadSupplements(
            """[null,{"code":"a","label":"A","amountCents":100,"per":"flat"}]""");

        Assert.Equal(new SupplierServiceSupplement("a", "A", 100, "flat", null), Assert.Single(read));
    }

    // ─── Texts: SharedResources ──────────────────────────────────────────────────

    [Fact]
    public void MessageKeys_EveryCatalogKey_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly, CultureInfo.InvariantCulture);
        var english = ReadEntries(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly, new CultureInfo("en"));

        Assert.All(SupplierServiceCatalogErrors.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Fact]
    public void MessageKeys_AreUniqueAndEveryErrorCodeHasOne()
    {
        Assert.Equal(SupplierServiceCatalogErrors.MessageKeys.Count, SupplierServiceCatalogErrors.MessageKeys.Distinct().Count());
        // Eleven keys: ten errors (404, invalid, not publishable, limit, cannot pause, changed, four photo ones) + the copy suffix.
        Assert.Equal(11, SupplierServiceCatalogErrors.MessageKeys.Count);
    }

    [Fact]
    public void MessageKeys_TheMessagesWithArguments_TakeThem()
    {
        var italian = ReadEntries(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly, CultureInfo.InvariantCulture);

        Assert.Contains("{0}", italian["SupplierServiceInvalid"]);
        Assert.Contains("{0}", italian["SupplierServiceNotPublishable"]);
        Assert.Contains("{0}", italian["SupplierServiceLimitReached"]);
        Assert.Contains("{0}", italian["SupplierServicePhotoInvalidType"]);
        Assert.Contains("{0}", italian["SupplierServicePhotoInvalidSize"]);
        Assert.Contains("{1}", italian["SupplierServicePhotoInvalidSize"]);
        Assert.Contains("{0}", italian["SupplierServicePhotoLimitReached"]);
        Assert.Contains("{1}", italian["SupplierServicePhotoLimitReached"]);
    }

    [Fact]
    public void CopySuffix_KeepsItsLeadingSpaceInBothLanguages()
    {
        var italian = ReadEntries(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly, CultureInfo.InvariantCulture);
        var english = ReadEntries(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly, new CultureInfo("en"));

        Assert.Equal(" (copia)", italian["SupplierServiceCopySuffix"]);
        Assert.Equal(" (copy)", english["SupplierServiceCopySuffix"]);
    }

    // ─── Texts: the electrical category ──────────────────────────────────────────

    [Fact]
    public void ServiceCategoryLabel_Electrical_HasItalianAndEnglishLabels()
    {
        Assert.Equal("Elettricista", EmailTemplates.ServiceCategoryLabel(CultureInfo.GetCultureInfo("it-IT"), ServiceCategories.Electrical));
        Assert.Equal("Electrician", EmailTemplates.ServiceCategoryLabel(CultureInfo.GetCultureInfo("en"), ServiceCategories.Electrical));
    }

    private static Dictionary<string, string> ReadEntries(string baseName, System.Reflection.Assembly assembly, CultureInfo culture)
    {
        var manager = new ResourceManager(baseName, assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
    }
}
