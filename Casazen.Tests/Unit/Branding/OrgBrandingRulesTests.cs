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
