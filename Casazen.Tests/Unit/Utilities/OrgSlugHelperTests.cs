using Casazen.Core.Exceptions;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class OrgSlugHelperTests
{
    [Theory]
    [InlineData("Villa Parco Rentals", "villa-parco-rentals")]
    [InlineData("  CasaZen Milano!!!  ", "casazen-milano")]
    [InlineData("Città---di Roma", "citta-di-roma")]
    [InlineData("Caffè Ñandú", "caffe-nandu")]
    public void Sanitize_FreeText_ReturnsLowercaseAsciiSlug(string input, string expected)
    {
        Assert.Equal(expected, OrgSlugHelper.Sanitize(input));
    }

    [Fact]
    public void NormalizeRequired_FreeText_ReturnsSlugifiedValue()
    {
        Assert.Equal("villa-parco", OrgSlugHelper.NormalizeRequired("Villa Parco"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("ab")]
    public void NormalizeRequired_EmptyOrTooShort_ThrowsInvalid(string? input)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSlugHelper.NormalizeRequired(input));
        Assert.Equal(OrgSlugHelper.InvalidCode, ex.Code);
        Assert.Equal("OrgSlugInvalid", ex.MessageKey);
    }

    [Fact]
    public void NormalizeRequired_LongerThanADnsLabel_ThrowsInvalidInsteadOfTruncating()
    {
        var tooLong = new string('a', OrgSlugHelper.MaxLength + 1);

        var ex = Assert.Throws<DomainRuleException>(() => OrgSlugHelper.NormalizeRequired(tooLong));

        Assert.Equal(OrgSlugHelper.InvalidCode, ex.Code);
        Assert.Equal(OrgSlugHelper.MaxLength, OrgSlugHelper.NormalizeRequired(tooLong[..OrgSlugHelper.MaxLength]).Length);
    }

    [Theory]
    [InlineData("book")]
    [InlineData("Admin")]
    [InlineData("www")]
    [InlineData("api")]
    public void NormalizeRequired_ReservedWord_ThrowsReserved(string reserved)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSlugHelper.NormalizeRequired(reserved));
        Assert.Equal(OrgSlugHelper.ReservedCode, ex.Code);
        Assert.Equal("OrgSlugReserved", ex.MessageKey);
    }

    [Fact]
    public void GenerateNeutral_Always_ReturnsValidSlugWithoutIdentityData()
    {
        var first = OrgSlugHelper.GenerateNeutral();
        var second = OrgSlugHelper.GenerateNeutral();

        Assert.Matches("^org-[a-z0-9]{8}$", first);
        Assert.Equal(first, OrgSlugHelper.NormalizeRequired(first));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IsNeutral_GeneratedSlug_IsTrue()
    {
        // Every slug a new org gets (A1-23) is one the host has not chosen yet (PL-15, A1-37).
        for (var i = 0; i < 50; i++)
            Assert.True(OrgSlugHelper.IsNeutral(OrgSlugHelper.GenerateNeutral()));
    }

    [Theory]
    [InlineData("villa-parco")]
    [InlineData("casa-rossi")]
    [InlineData("org-abc")]
    [InlineData("org-abcd2345-extra")]
    [InlineData("org-ABCD2345")]
    [InlineData("org-abcd0123")]
    [InlineData("")]
    [InlineData(null)]
    public void IsNeutral_SlugTheHostChose_IsFalse(string? slug)
    {
        Assert.False(OrgSlugHelper.IsNeutral(slug));
    }
}
