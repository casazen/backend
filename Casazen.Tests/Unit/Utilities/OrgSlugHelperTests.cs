using Casazen.Core.Exceptions;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class OrgSlugHelperTests
{
    [Theory]
    [InlineData("Villa Parco Rentals", "villa-parco-rentals")]
    [InlineData("  CasaZen Milano!!!  ", "casazen-milano")]
    [InlineData("Città---di Roma", "citt-di-roma")]
    public void Sanitize_NormalizesFreeText(string input, string expected)
    {
        Assert.Equal(expected, OrgSlugHelper.Sanitize(input));
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var tooLong = new string('a', OrgSlugHelper.MaxLength + 20);

        var sanitized = OrgSlugHelper.Sanitize(tooLong);

        Assert.Equal(OrgSlugHelper.MaxLength, sanitized.Length);
    }

    [Fact]
    public void NormalizeRequired_AcceptsFreeTextAndSlugifiesIt()
    {
        Assert.Equal("villa-parco", OrgSlugHelper.NormalizeRequired("Villa Parco"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void NormalizeRequired_EmptyAfterSanitizing_ThrowsInvalid(string? input)
    {
        var ex = Assert.Throws<DomainRuleException>(() => OrgSlugHelper.NormalizeRequired(input));
        Assert.Equal(OrgSlugHelper.InvalidCode, ex.Code);
        Assert.Equal("OrgSlugInvalid", ex.MessageKey);
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
}
