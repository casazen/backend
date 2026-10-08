using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-02: the rules of the supplier's service catalog on <see cref="SupplierServiceListingRules"/> (no database):
/// validation of every field and its limits, the structured supplements, the publication requirements, the slugs.
/// </summary>
public class SupplierServiceListingRulesTests
{
    private static SupplierServiceListingInput Valid() =>
        new(
            Name: "Pulizia cambio ospiti",
            Category: ServiceCategories.Cleaning,
            Summary: "Pulizia completa tra un ospite e l'altro.",
            Description: "Riga uno.\nRiga due.",
            PriceFromCents: 4500,
            PriceUnit: SupplierServicePriceUnit.PerJob,
            PricesIncludeVat: true,
            RequiresQuote: false,
            DurationMinutes: 120,
            MinNoticeHours: 12,
            Weekdays: [DayOfWeek.Monday, DayOfWeek.Saturday],
            Supplements: [new SupplierServiceSupplement("bagno-extra", "Ogni bagno in più", 1000, "bathroom", 3)],
            Included: ["Biancheria pulita", "Report fotografico"],
            Excluded: ["Lavaggio tende"],
            PhotoUrls: null,
            SortOrder: null);

    private static IReadOnlyList<string> InvalidFields(SupplierServiceListingInput input)
    {
        var ex = Assert.Throws<SupplierServiceRuleException>(() => SupplierServiceListingRules.Normalize(input));
        Assert.Equal(SupplierServiceCatalogErrors.Invalid, ex.Code);
        Assert.Equal("SupplierServiceInvalid", ex.MessageKey);
        Assert.Equal(string.Join(", ", ex.Fields), Assert.Single(ex.MessageArgs));
        return ex.Fields;
    }

    private static SupplierServiceSupplement Supplement(
        string code = "extra",
        string label = "Extra",
        int amountCents = 500,
        string per = "flat",
        int? max = null) =>
        new(code, label, amountCents, per, max);

    // ─── Normalize: the happy path ───────────────────────────────────────────────

    [Fact]
    public void Normalize_ValidInput_ReturnsTheStoredForm()
    {
        var content = SupplierServiceListingRules.Normalize(Valid());

        Assert.Equal("Pulizia cambio ospiti", content.Name);
        Assert.Equal(ServiceCategories.Cleaning, content.Category);
        Assert.Equal(4500, content.PriceFromCents);
        Assert.Equal(SupplierServicePriceUnit.PerJob, content.PriceUnit);
        Assert.True(content.PricesIncludeVat);
        Assert.Equal(120, content.DurationMinutes);
        Assert.Equal(12, content.MinNoticeHours);
        Assert.Equal(0b0100001, content.WeekdaysMask);
        Assert.Equal(new SupplierServiceSupplement("bagno-extra", "Ogni bagno in più", 1000, "bathroom", 3), Assert.Single(content.Supplements));
        Assert.Equal(new[] { "Biancheria pulita", "Report fotografico" }, content.Included);
        Assert.Equal(new[] { "Lavaggio tende" }, content.Excluded);
        Assert.Null(content.PhotoUrls);
        Assert.Null(content.SortOrder);
    }

    [Fact]
    public void Normalize_OnlyNameAndCategory_IsAValidDraft()
    {
        var draft = new SupplierServiceListingInput(
            "Ripasso pre-arrivo", "cleaning", null, null, null, SupplierServicePriceUnit.PerJob, false, false, null, null, null, null, null, null, null, null);

        var content = SupplierServiceListingRules.Normalize(draft);

        Assert.Null(content.PriceFromCents);
        Assert.Null(content.DurationMinutes);
        Assert.Null(content.MinNoticeHours);
        Assert.Equal(SupplierServiceWeekdays.AllMask, content.WeekdaysMask);
        Assert.Empty(content.Supplements);
        Assert.Empty(content.Included);
        Assert.False(content.PricesIncludeVat);
    }

    [Fact]
    public void Normalize_TextsAreTrimmedAndBlankOptionalTextsBecomeNull()
    {
        var content = SupplierServiceListingRules.Normalize(Valid() with
        {
            Name = "  Pulizia  ",
            Category = " Cleaning ",
            Summary = "   ",
            Description = "",
        });

        Assert.Equal("Pulizia", content.Name);
        Assert.Equal("cleaning", content.Category);
        Assert.Null(content.Summary);
        Assert.Null(content.Description);
    }

    // ─── Normalize: name, category, texts ────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankName_IsInvalid(string? name)
    {
        Assert.Equal(new[] { "name" }, InvalidFields(Valid() with { Name = name }));
    }

    [Fact]
    public void Normalize_NameOfExactlyTheLimit_IsAcceptedAndOneMoreIsNot()
    {
        var limit = SupplierServiceCatalogLimits.NameMaxLength;

        Assert.Equal(limit, SupplierServiceListingRules.Normalize(Valid() with { Name = new string('a', limit) }).Name.Length);
        Assert.Equal(new[] { "name" }, InvalidFields(Valid() with { Name = new string('a', limit + 1) }));
    }

    [Fact]
    public void Normalize_ControlCharacterInASingleLineText_IsInvalid()
    {
        Assert.Equal(new[] { "name" }, InvalidFields(Valid() with { Name = "Pulizia\nprofonda" }));
        Assert.Equal(new[] { "summary" }, InvalidFields(Valid() with { Summary = "Riga\tuno" }));
    }

    [Fact]
    public void Normalize_DescriptionKeepsLineBreaksButRefusesOtherControlCharacters()
    {
        var content = SupplierServiceListingRules.Normalize(Valid() with { Description = "Uno\r\nDue\tTre" });

        Assert.Equal("Uno\r\nDue\tTre", content.Description);
        Assert.Equal(new[] { "description" }, InvalidFields(Valid() with { Description = "Uno\u0000Due" }));
    }

    [Fact]
    public void Normalize_SummaryAndDescriptionOverTheirLimits_AreInvalid()
    {
        var fields = InvalidFields(Valid() with
        {
            Summary = new string('s', SupplierServiceCatalogLimits.SummaryMaxLength + 1),
            Description = new string('d', SupplierServiceCatalogLimits.DescriptionMaxLength + 1),
        });

        Assert.Equal(new[] { "summary", "description" }, fields);
    }

    [Theory]
    [InlineData("Pulizie")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("free text")]
    public void Normalize_CategoryThatIsNotACode_ThrowsInvalidServiceCategory(string? category)
    {
        var ex = Assert.Throws<DomainRuleException>(() => SupplierServiceListingRules.Normalize(Valid() with { Category = category }));

        Assert.Equal(ServiceCategories.InvalidCategoryCode, ex.Code);
    }

    [Fact]
    public void Normalize_ElectricalIsAValidCategory()
    {
        var content = SupplierServiceListingRules.Normalize(Valid() with { Category = "electrical" });

        Assert.Equal(ServiceCategories.Electrical, content.Category);
    }

    // ─── Normalize: price, duration, notice, weekdays ────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(SupplierServiceCatalogLimits.MaxAmountCents + 1)]
    public void Normalize_PriceOutOfRange_IsInvalid(int cents)
    {
        Assert.Equal(new[] { "priceFromCents" }, InvalidFields(Valid() with { PriceFromCents = cents }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(SupplierServiceCatalogLimits.MaxAmountCents)]
    public void Normalize_PriceAtTheBounds_IsAccepted(int cents)
    {
        Assert.Equal(cents, SupplierServiceListingRules.Normalize(Valid() with { PriceFromCents = cents }).PriceFromCents);
    }

    [Theory]
    [InlineData(SupplierServiceCatalogLimits.MinDurationMinutes - 1)]
    [InlineData(0)]
    [InlineData(SupplierServiceCatalogLimits.MaxDurationMinutes + 1)]
    public void Normalize_DurationOutOfRange_IsInvalid(int minutes)
    {
        Assert.Equal(new[] { "durationMinutes" }, InvalidFields(Valid() with { DurationMinutes = minutes }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SupplierServiceCatalogLimits.MaxMinNoticeHours + 1)]
    public void Normalize_NoticeOutOfRange_IsInvalid(int hours)
    {
        Assert.Equal(new[] { "minNoticeHours" }, InvalidFields(Valid() with { MinNoticeHours = hours }));
    }

    [Fact]
    public void Normalize_ZeroNoticeIsNoNoticeAndNullIsTheSuppliersDefault()
    {
        Assert.Equal(0, SupplierServiceListingRules.Normalize(Valid() with { MinNoticeHours = 0 }).MinNoticeHours);
        Assert.Null(SupplierServiceListingRules.Normalize(Valid() with { MinNoticeHours = null }).MinNoticeHours);
    }

    [Fact]
    public void Normalize_PriceUnitThatIsNotAMember_IsInvalid()
    {
        Assert.Equal(new[] { "priceUnit" }, InvalidFields(Valid() with { PriceUnit = (SupplierServicePriceUnit)9 }));
    }

    [Fact]
    public void Normalize_Weekdays_NullIsEveryDayAndAListIsItsMask()
    {
        Assert.Equal(127, SupplierServiceListingRules.Normalize(Valid() with { Weekdays = null }).WeekdaysMask);
        Assert.Equal(0, SupplierServiceListingRules.Normalize(Valid() with { Weekdays = [] }).WeekdaysMask);
        Assert.Equal(
            0b0011111,
            SupplierServiceListingRules.Normalize(Valid() with
            {
                Weekdays = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Monday],
            }).WeekdaysMask);
        Assert.Equal(new[] { "weekdays" }, InvalidFields(Valid() with { Weekdays = [(DayOfWeek)9] }));
    }

    // ─── Normalize: supplements ──────────────────────────────────────────────────

    [Fact]
    public void Normalize_Supplement_CodeAndUnitAreLowercasedAndTrimmed()
    {
        var content = SupplierServiceListingRules.Normalize(Valid() with
        {
            Supplements = [Supplement(code: " Bagno-Extra ", label: "  Bagno  ", per: " Bathroom ", max: 2)],
        });

        Assert.Equal(new SupplierServiceSupplement("bagno-extra", "Bagno", 500, "bathroom", 2), Assert.Single(content.Supplements));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("bathroom")]
    [InlineData("sqm30")]
    [InlineData("set")]
    [InlineData("hour")]
    public void Normalize_Supplement_EveryUnitOfTheSpecIsAccepted(string per)
    {
        Assert.Single(SupplierServiceListingRules.Normalize(Valid() with { Supplements = [Supplement(per: per)] }).Supplements);
    }

    [Fact]
    public void Normalize_Supplement_UnitsAreExactlyTheFiveOfTheSpec()
    {
        Assert.Equal(new[] { "flat", "bathroom", "sqm30", "set", "hour" }, SupplierServiceSupplementUnits.All);
    }

    [Fact]
    public void Normalize_Supplement_EveryInvalidFieldIsNamedWithItsPosition()
    {
        var fields = InvalidFields(Valid() with
        {
            Supplements =
            [
                Supplement(),
                Supplement(code: "Not a code", label: "", amountCents: 0, per: "m3", max: 0),
            ],
        });

        Assert.Equal(
            new[]
            {
                "supplements[1].code",
                "supplements[1].label",
                "supplements[1].amountCents",
                "supplements[1].per",
                "supplements[1].max",
            },
            fields);
    }

    [Theory]
    [InlineData("-extra")]
    [InlineData("extra-")]
    [InlineData("ex--tra")]
    [InlineData("extra_bagno")]
    [InlineData("")]
    public void Normalize_Supplement_CodeThatIsNotLowercaseWordsWithHyphens_IsInvalid(string code)
    {
        Assert.Equal(new[] { "supplements[0].code" }, InvalidFields(Valid() with { Supplements = [Supplement(code: code)] }));
    }

    [Fact]
    public void Normalize_Supplement_TwoWithTheSameCode_IsInvalidOnTheSecond()
    {
        var fields = InvalidFields(Valid() with { Supplements = [Supplement(code: "extra"), Supplement(code: "EXTRA", label: "Altro")] });

        Assert.Equal(new[] { "supplements[1].code" }, fields);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SupplierServiceCatalogLimits.MaxSupplementQuantity + 1)]
    public void Normalize_Supplement_MaxOutOfRange_IsInvalid(int max)
    {
        Assert.Equal(new[] { "supplements[0].max" }, InvalidFields(Valid() with { Supplements = [Supplement(max: max)] }));
    }

    [Fact]
    public void Normalize_Supplement_NoMaxIsNoLimit()
    {
        var content = SupplierServiceListingRules.Normalize(Valid() with { Supplements = [Supplement(max: null)] });

        Assert.Null(Assert.Single(content.Supplements).Max);
    }

    [Fact]
    public void Normalize_Supplement_AmountAtTheBounds()
    {
        Assert.Single(SupplierServiceListingRules.Normalize(Valid() with { Supplements = [Supplement(amountCents: 1)] }).Supplements);
        Assert.Equal(
            new[] { "supplements[0].amountCents" },
            InvalidFields(Valid() with { Supplements = [Supplement(amountCents: SupplierServiceCatalogLimits.MaxAmountCents + 1)] }));
    }

    [Fact]
    public void Normalize_Supplement_NullElementIsInvalid()
    {
        Assert.Equal(new[] { "supplements[0]" }, InvalidFields(Valid() with { Supplements = [null] }));
    }

    [Fact]
    public void Normalize_Supplements_MoreThanTheLimit_IsInvalid()
    {
        var many = Enumerable.Range(0, SupplierServiceCatalogLimits.MaxSupplements + 1)
            .Select(i => Supplement(code: $"extra-{i}"))
            .Cast<SupplierServiceSupplement?>()
            .ToList();

        Assert.Equal(new[] { "supplements" }, InvalidFields(Valid() with { Supplements = many }));
    }

    // ─── Normalize: included, excluded, photos, position ─────────────────────────

    [Fact]
    public void Normalize_Lists_DropBlankAndRepeatedLinesKeepingTheFirstOfEach()
    {
        var content = SupplierServiceListingRules.Normalize(Valid() with
        {
            Included = ["  Biancheria ", "", null, "biancheria", "Prodotti", "   "],
            Excluded = [],
        });

        Assert.Equal(new[] { "Biancheria", "Prodotti" }, content.Included);
        Assert.Empty(content.Excluded);
    }

    [Fact]
    public void Normalize_Lists_AnEntryOverTheLimitOrMoreEntriesThanTheLimit_AreInvalid()
    {
        var tooMany = Enumerable.Range(0, SupplierServiceCatalogLimits.MaxListItems + 1).Select(i => (string?)$"Voce {i}").ToList();

        var fields = InvalidFields(Valid() with
        {
            Included = [new string('x', SupplierServiceCatalogLimits.ListItemMaxLength + 1)],
            Excluded = tooMany,
        });

        Assert.Equal(new[] { "included", "excluded" }, fields);
    }

    [Fact]
    public void Normalize_PhotoUrls_NullKeepsThemAndAListIsKeptInOrder()
    {
        Assert.Null(SupplierServiceListingRules.Normalize(Valid() with { PhotoUrls = null }).PhotoUrls);
        Assert.Equal(
            new[] { "https://s/b.jpg", "https://s/a.jpg" },
            SupplierServiceListingRules.Normalize(Valid() with { PhotoUrls = [" https://s/b.jpg ", "https://s/a.jpg"] }).PhotoUrls);
        Assert.Empty(SupplierServiceListingRules.Normalize(Valid() with { PhotoUrls = [] }).PhotoUrls!);
    }

    [Fact]
    public void Normalize_PhotoUrls_RepeatedBlankOrTooManyAreInvalid()
    {
        Assert.Equal(new[] { "photoUrls" }, InvalidFields(Valid() with { PhotoUrls = ["https://s/a.jpg", "https://s/a.jpg"] }));
        Assert.Equal(new[] { "photoUrls" }, InvalidFields(Valid() with { PhotoUrls = ["https://s/a.jpg", " "] }));

        var tooMany = Enumerable.Range(0, SupplierServiceCatalogLimits.MaxPhotos + 1).Select(i => (string?)$"https://s/{i}.jpg").ToList();
        Assert.Equal(new[] { "photoUrls" }, InvalidFields(Valid() with { PhotoUrls = tooMany }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SupplierServiceCatalogLimits.MaxSortOrder + 1)]
    public void Normalize_SortOrderOutOfRange_IsInvalid(int sortOrder)
    {
        Assert.Equal(new[] { "sortOrder" }, InvalidFields(Valid() with { SortOrder = sortOrder }));
    }

    [Fact]
    public void Normalize_SeveralProblems_AreReportedTogetherInFieldOrder()
    {
        var fields = InvalidFields(Valid() with
        {
            Name = "",
            PriceFromCents = 0,
            DurationMinutes = 1,
            MinNoticeHours = -3,
            Included = [new string('x', 500)],
        });

        Assert.Equal(new[] { "name", "priceFromCents", "durationMinutes", "minNoticeHours", "included" }, fields);
    }

    // ─── Apply ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyTo_WritesTheContentAndLeavesSlugStatusPhotosAndPositionAlone()
    {
        var listing = new SupplierServiceListing
        {
            Slug = "keep-me",
            Status = SupplierServiceListingStatus.Active,
            PhotoUrlsJson = """["https://s/a.jpg"]""",
            SortOrder = 7,
        };

        SupplierServiceListingRules.Normalize(Valid() with { PhotoUrls = [], SortOrder = 3 }).ApplyTo(listing);

        Assert.Equal("keep-me", listing.Slug);
        Assert.Equal(SupplierServiceListingStatus.Active, listing.Status);
        Assert.Equal("""["https://s/a.jpg"]""", listing.PhotoUrlsJson);
        Assert.Equal(7, listing.SortOrder);
        Assert.Equal("Pulizia cambio ospiti", listing.Name);
        Assert.Equal(4500, listing.PriceFromCents);
        Assert.Equal(0b0100001, listing.WeekdaysMask);
        Assert.Equal(
            new SupplierServiceSupplement("bagno-extra", "Ogni bagno in più", 1000, "bathroom", 3),
            Assert.Single(SupplierServiceListingJson.ReadSupplements(listing.SupplementsJson)));
        Assert.Equal(new[] { "Biancheria pulita", "Report fotografico" }, SupplierServiceListingJson.ReadStrings(listing.IncludedJson));
        Assert.Equal(new[] { "Lavaggio tende" }, SupplierServiceListingJson.ReadStrings(listing.ExcludedJson));
    }

    // ─── Publication requirements ────────────────────────────────────────────────

    private static SupplierServiceListing Publishable() =>
        new()
        {
            Name = "Pulizia",
            Category = ServiceCategories.Cleaning,
            DurationMinutes = 90,
            PriceFromCents = 3000,
        };

    [Fact]
    public void MissingForPublication_NameCategoryDurationAndPrice_IsPublishable()
    {
        Assert.Empty(SupplierServiceListingRules.MissingForPublication(Publishable()));
        SupplierServiceListingRules.EnsurePublishable(Publishable());
    }

    [Fact]
    public void MissingForPublication_QuoteInPlaceOfAPrice_IsPublishable()
    {
        var listing = Publishable();
        listing.PriceFromCents = null;
        listing.RequiresQuote = true;

        Assert.Empty(SupplierServiceListingRules.MissingForPublication(listing));
    }

    [Fact]
    public void MissingForPublication_NoPriceAndNoQuote_LacksThePrice()
    {
        var listing = Publishable();
        listing.PriceFromCents = null;

        Assert.Equal(new[] { "priceFromCents" }, SupplierServiceListingRules.MissingForPublication(listing));
    }

    [Fact]
    public void MissingForPublication_QuoteDoesNotReplaceTheDuration()
    {
        var listing = Publishable();
        listing.DurationMinutes = null;
        listing.PriceFromCents = null;
        listing.RequiresQuote = true;

        Assert.Equal(new[] { "durationMinutes" }, SupplierServiceListingRules.MissingForPublication(listing));
    }

    [Fact]
    public void MissingForPublication_NothingSet_ListsEveryRequirementInOrder()
    {
        var missing = SupplierServiceListingRules.MissingForPublication(new SupplierServiceListing());

        Assert.Equal(new[] { "name", "category", "durationMinutes", "priceFromCents" }, missing);
    }

    [Fact]
    public void EnsurePublishable_Incomplete_ThrowsNotPublishableNamingWhatIsMissing()
    {
        var listing = Publishable();
        listing.DurationMinutes = 0;

        var ex = Assert.Throws<SupplierServiceRuleException>(() => SupplierServiceListingRules.EnsurePublishable(listing));

        Assert.Equal(SupplierServiceCatalogErrors.NotPublishable, ex.Code);
        Assert.Equal("SupplierServiceNotPublishable", ex.MessageKey);
        Assert.Equal(new[] { "durationMinutes" }, ex.Fields);
        Assert.Equal("durationMinutes", Assert.Single(ex.MessageArgs));
    }

    [Fact]
    public void EnsurePublishable_ACategoryThatIsNoLongerACode_IsMissingToo()
    {
        var listing = Publishable();
        listing.Category = "Pulizie";

        Assert.Equal(new[] { "category" }, SupplierServiceListingRules.MissingForPublication(listing));
    }

    // ─── Slugs and copies ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Pulizia cambio ospiti", "pulizia-cambio-ospiti")]
    [InlineData("  Pulizia   città!  ", "pulizia-citta")]
    [InlineData("Cambio biancheria e lavanderia", "cambio-biancheria-e-lavanderia")]
    [InlineData("€€€", "servizio")]
    [InlineData("", "servizio")]
    [InlineData(null, "servizio")]
    public void SlugFromName_AccentsSymbolsAndEmptyNames(string? name, string expected)
    {
        Assert.Equal(expected, SupplierServiceListingRules.SlugFromName(name));
    }

    [Fact]
    public void SlugFromName_ANameLiterallyCalledFornitore_KeepsItsSlug()
    {
        // The showcase fallback ("fornitore") is not the fallback of a service.
        Assert.Equal("fornitore", SupplierServiceListingRules.SlugFromName("Fornitore"));
        Assert.Equal("fornitore", SupplierShowcaseSlug.FromName(""));
    }

    [Fact]
    public void SlugFromName_LongName_IsCutAtTheShowcaseSlugLength()
    {
        var slug = SupplierServiceListingRules.SlugFromName(new string('a', 200));

        Assert.Equal(SupplierShowcaseSlug.MaxBaseLength, slug.Length);
    }

    [Fact]
    public void NextFreeSlug_FreeSlugIsKeptAndATakenOneGetsTheFirstFreeNumber()
    {
        Assert.Equal("pulizia", SupplierServiceListingRules.NextFreeSlug("pulizia", new HashSet<string>()));
        Assert.Equal("pulizia-2", SupplierServiceListingRules.NextFreeSlug("pulizia", new HashSet<string> { "pulizia" }));
        Assert.Equal("pulizia-4", SupplierServiceListingRules.NextFreeSlug(
            "pulizia", new HashSet<string> { "pulizia", "pulizia-2", "pulizia-3", "altro" }));
        // A gap is filled: the first free one wins.
        Assert.Equal("pulizia-3", SupplierServiceListingRules.NextFreeSlug(
            "pulizia", new HashSet<string> { "pulizia", "pulizia-2", "pulizia-4" }));
    }

    [Theory]
    [InlineData("pulizia", "pulizia", true)]
    [InlineData("pulizia-2", "pulizia", true)]
    [InlineData("pulizia-12", "pulizia", true)]
    [InlineData("pulizia-profonda", "pulizia", false)]
    [InlineData("pulizia-", "pulizia", false)]
    [InlineData("altro", "pulizia", false)]
    public void SlugFollowsName_ABaseSlugWithOrWithoutTheNumericSuffix(string slug, string baseSlug, bool expected)
    {
        Assert.Equal(expected, SupplierServiceListingRules.SlugFollowsName(slug, baseSlug));
    }

    [Theory]
    [InlineData("Pulizia", " (copia)", "Pulizia (copia)")]
    [InlineData("Pulizia", " (copy)", "Pulizia (copy)")]
    [InlineData("Pulizia", "", "Pulizia")]
    [InlineData("Pulizia", null, "Pulizia")]
    public void CopyName_NameFollowedByTheSuffix(string name, string? suffix, string expected)
    {
        Assert.Equal(expected, SupplierServiceListingRules.CopyName(name, suffix));
    }

    [Fact]
    public void CopyName_LongName_IsShortenedSoTheResultFitsTheLimit()
    {
        var name = new string('a', SupplierServiceCatalogLimits.NameMaxLength);

        var copy = SupplierServiceListingRules.CopyName(name, " (copia)");

        Assert.Equal(SupplierServiceCatalogLimits.NameMaxLength, copy.Length);
        Assert.EndsWith(" (copia)", copy);
        Assert.StartsWith("aaaa", copy);
    }

    [Fact]
    public void CopyName_ASuffixAsLongAsTheLimit_IsCutNotThrown()
    {
        var copy = SupplierServiceListingRules.CopyName("Pulizia", new string('x', 80));

        Assert.True(copy.Length <= SupplierServiceCatalogLimits.NameMaxLength);
    }
}
