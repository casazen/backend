using Casazen.Core.Branding;
using Casazen.Core.Exceptions;
using Xunit;

namespace Casazen.Tests.Unit.Branding;

public class OrgBrandingRulesTests
{
    [Theory]
    [InlineData("#1A6B8F", "#1a6b8f")]
    [InlineData("1a6b8f", "#1a6b8f")]
    [InlineData("  #abc ", "#aabbcc")]
    [InlineData("#FFF", "#ffffff")]
    public void NormalizePrimaryColor_HexColor_ReturnsLowerCaseSixDigitForm(string input, string expected)
    {
        Assert.Equal(expected, OrgBrandingRules.NormalizePrimaryColor(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePrimaryColor_Empty_ReturnsNullForThemeColor(string? input)
    {
        Assert.Null(OrgBrandingRules.NormalizePrimaryColor(input));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#1a6b8f80")]
    [InlineData("rgb(0,0,0)")]
    [InlineData("#1a6b8f;background:url(x)")]
    [InlineData("#gggggg")]
    public void NormalizePrimaryColor_NotAHexColor_ThrowsColorInvalid(string input)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgBrandingRules.NormalizePrimaryColor(input));
        Assert.Equal(OrgBrandingRules.ColorInvalidCode, ex.Code);
    }

    [Theory]
    [InlineData("mare", "mare")]
    [InlineData(" Montagna ", "montagna")]
    [InlineData("URBAN", "urban")]
    public void NormalizeThemeId_SupportedTheme_ReturnsLowerCaseId(string input, string expected)
    {
        Assert.Equal(expected, OrgBrandingRules.NormalizeThemeId(input));
    }

    [Fact]
    public void NormalizeThemeId_Empty_ReturnsNullForDefaultTheme()
    {
        Assert.Null(OrgBrandingRules.NormalizeThemeId(" "));
    }

    [Theory]
    [InlineData("collina")]
    [InlineData("dark")]
    public void NormalizeThemeId_UnsupportedTheme_ThrowsThemeInvalid(string input)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgBrandingRules.NormalizeThemeId(input));
        Assert.Equal(OrgBrandingRules.ThemeInvalidCode, ex.Code);
    }

    [Fact]
    public void NormalizeTagline_WhitespaceAndNewLines_CollapsesIntoSingleSpaces()
    {
        Assert.Equal("Case vacanza al mare", OrgBrandingRules.NormalizeTagline("  Case vacanza\n\n  al   mare "));
    }

    [Fact]
    public void NormalizeTagline_Blank_ReturnsNull()
    {
        Assert.Null(OrgBrandingRules.NormalizeTagline(" \n "));
    }

    [Fact]
    public void NormalizeTagline_AtMaxLength_IsAccepted()
    {
        var tagline = new string('a', OrgBrandingRules.TaglineMaxLength);

        Assert.Equal(tagline, OrgBrandingRules.NormalizeTagline(tagline));
    }

    [Fact]
    public void NormalizeTagline_OverMaxLength_ThrowsTaglineTooLongWithLimit()
    {
        var ex = Assert.Throws<DomainRuleException>(
            () => OrgBrandingRules.NormalizeTagline(new string('a', OrgBrandingRules.TaglineMaxLength + 1)));

        Assert.Equal(OrgBrandingRules.TaglineTooLongCode, ex.Code);
        Assert.Equal(new object[] { OrgBrandingRules.TaglineMaxLength }, ex.MessageArgs);
    }

    // ─── Public profile of the booking site (DB-03) ─────────────────────────────────────────────────

    [Fact]
    public void NormalizeSubtitle_WhitespaceAndNewLines_CollapsesIntoSingleSpaces()
    {
        Assert.Equal(
            "Trulli, case sul mare e dimore barocche.",
            OrgBrandingRules.NormalizeSubtitle("  Trulli, case sul mare\n e   dimore\tbarocche. "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void NormalizeSubtitle_Blank_ReturnsNull(string? input)
    {
        Assert.Null(OrgBrandingRules.NormalizeSubtitle(input));
    }

    [Fact]
    public void NormalizeSubtitle_AtMaxLength_IsAcceptedAndOverIsRefusedWithTheLimit()
    {
        var atLimit = new string('a', OrgBrandingRules.SubtitleMaxLength);
        Assert.Equal(atLimit, OrgBrandingRules.NormalizeSubtitle(atLimit));

        var ex = Assert.Throws<DomainRuleException>(() => OrgBrandingRules.NormalizeSubtitle(atLimit + "a"));
        Assert.Equal(OrgBrandingRules.SubtitleTooLongCode, ex.Code);
        Assert.Equal("OrgBrandingSubtitleTooLong", ex.MessageKey);
        Assert.Equal(new object[] { OrgBrandingRules.SubtitleMaxLength }, ex.MessageArgs);
    }

    [Fact]
    public void NormalizeSubtitle_LengthIsMeasuredAfterTheWhitespaceIsCollapsed()
    {
        // 150 words of one letter separated by runs of spaces: the raw text is long, the collapsed one is 299 characters.
        var raw = string.Join("    ", Enumerable.Repeat("a", 150));

        Assert.Equal(string.Join(' ', Enumerable.Repeat("a", 150)), OrgBrandingRules.NormalizeSubtitle(raw));
    }

    [Fact]
    public void NormalizeHostName_CollapsesWhitespaceAndKeepsTheName()
    {
        Assert.Equal("Giulia Rinaldi", OrgBrandingRules.NormalizeHostName("  Giulia \n  Rinaldi "));
        Assert.Null(OrgBrandingRules.NormalizeHostName("   "));
    }

    [Fact]
    public void NormalizeHostName_OverMaxLength_ThrowsHostNameTooLongWithLimit()
    {
        var ex = Assert.Throws<DomainRuleException>(
            () => OrgBrandingRules.NormalizeHostName(new string('a', OrgBrandingRules.HostNameMaxLength + 1)));

        Assert.Equal(OrgBrandingRules.HostNameTooLongCode, ex.Code);
        Assert.Equal(new object[] { OrgBrandingRules.HostNameMaxLength }, ex.MessageArgs);
    }

    [Theory]
    [InlineData("+39 333 123 4567", "+393331234567")]
    [InlineData("+39 (0832) 12-34-56", "+390832123456")]
    [InlineData("333.1234567", "3331234567")]
    [InlineData("0832/123456", "0832123456")]
    [InlineData("  +44 20 7946 0958  ", "+442079460958")]
    [InlineData("123456", "123456")]
    [InlineData("+123456789012345", "+123456789012345")]
    public void NormalizePublicPhone_DigitsWithTheUsualSeparators_IsStoredAsPlusAndDigits(string input, string expected)
    {
        Assert.Equal(expected, OrgBrandingRules.NormalizePublicPhone(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePublicPhone_Blank_ReturnsNullForNoPublicPhone(string? input)
    {
        Assert.Null(OrgBrandingRules.NormalizePublicPhone(input));
    }

    [Theory]
    [InlineData("12345")] // 5 digits
    [InlineData("+1234567890123456")] // 16 digits
    [InlineData("333 123 4567 ext 5")] // letters
    [InlineData("333-12+34567")] // a plus that is not the first character
    [InlineData("++393331234567")]
    [InlineData("tel:+393331234567")]
    [InlineData("<script>")]
    [InlineData("333 1234567; DROP TABLE Orgs")]
    [InlineData("+")]
    [InlineData("---")]
    public void NormalizePublicPhone_NotAPhone_ThrowsPhoneInvalid(string input)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgBrandingRules.NormalizePublicPhone(input));

        Assert.Equal(OrgBrandingRules.PhoneInvalidCode, ex.Code);
        Assert.Equal("OrgBrandingPhoneInvalid", ex.MessageKey);
    }

    [Fact]
    public void ValidateImage_LogoPngWithinLimits_ReturnsDetectedFormat()
    {
        var info = OrgBrandingRules.ValidateImage(OrgBrandingRules.Logo, TestImageBytes.Png(400, 120));

        Assert.Equal(ImageFormat.Png, info.Format);
    }

    [Fact]
    public void ValidateImage_Empty_ThrowsImageEmpty()
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgBrandingRules.ValidateImage(OrgBrandingRules.Logo, []));
        Assert.Equal(OrgBrandingRules.ImageEmptyCode, ex.Code);
    }

    [Fact]
    public void ValidateImage_LogoOverTwoMegabytes_ThrowsImageTooLarge()
    {
        var bytes = TestImageBytes.Png(400, 120, totalLength: (int)OrgBrandingRules.Logo.MaxBytes + 1);

        var ex = Assert.Throws<DomainRuleException>(() => OrgBrandingRules.ValidateImage(OrgBrandingRules.Logo, bytes));

        Assert.Equal(OrgBrandingRules.ImageTooLargeCode, ex.Code);
    }

    [Fact]
    public void ValidateImage_Svg_ThrowsImageTypeInvalid()
    {
        var ex = Assert.Throws<DomainRuleException>(
            () => OrgBrandingRules.ValidateImage(OrgBrandingRules.Logo, TestImageBytes.Svg()));

        Assert.Equal(OrgBrandingRules.ImageTypeInvalidCode, ex.Code);
    }

    [Theory]
    [InlineData(800, 300)] // too narrow for a desktop cover
    [InlineData(1600, 200)] // too short
    [InlineData(9000, 3000)] // over the decompression guard
    public void ValidateImage_HeroOutsideDimensions_ThrowsDimensionsInvalid(int width, int height)
    {
        var ex = Assert.Throws<DomainRuleException>(
            () => OrgBrandingRules.ValidateImage(OrgBrandingRules.Hero, TestImageBytes.Jpeg(width, height)));

        Assert.Equal(OrgBrandingRules.ImageDimensionsInvalidCode, ex.Code);
    }

    [Fact]
    public void ValidateImage_HeroWideJpeg_IsAccepted()
    {
        var info = OrgBrandingRules.ValidateImage(OrgBrandingRules.Hero, TestImageBytes.Jpeg(2400, 1000));

        Assert.Equal(ImageFormat.Jpeg, info.Format);
    }

    [Theory]
    [InlineData(null, "mare")]
    [InlineData("montagna", "montagna")]
    [InlineData("Urban", "urban")]
    [InlineData("legacy-theme", "mare")]
    public void PublicSiteThemesResolve_StoredValue_ReturnsRenderedTheme(string? stored, string expected)
    {
        Assert.Equal(expected, PublicSiteThemes.Resolve(stored));
    }
}
