using Casazen.Core.Entities;
using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

public class EnumNamesTests
{
    [Theory]
    [InlineData("ShortTerm", RentalType.ShortTerm)]
    [InlineData("longterm", RentalType.LongTerm)]
    [InlineData("  Both  ", RentalType.Both)]
    public void TryParseDefined_DeclaredMemberName_ReturnsTrueWithValue(string value, RentalType expected)
    {
        var success = EnumNames.TryParseDefined<RentalType>(value, out var result);

        Assert.True(success);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid")]
    public void TryParseDefined_MissingOrUnknownName_ReturnsFalse(string? value)
    {
        var success = EnumNames.TryParseDefined<RentalType>(value, out var result);

        Assert.False(success);
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("99")]
    public void TryParseDefined_NumericStringWithNoDeclaredMember_ReturnsFalse(string value)
    {
        var success = EnumNames.TryParseDefined<RentalType>(value, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseDefined_NumericStringMatchingDeclaredOrdinal_ReturnsFalse()
    {
        var success = EnumNames.TryParseDefined<RentalType>("1", out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParseDefined_CommaSeparatedNames_ReturnsFalse()
    {
        var success = EnumNames.TryParseDefined<UserRole>("PropertyOwner,PropertyManager", out _);

        Assert.False(success);
    }
}
