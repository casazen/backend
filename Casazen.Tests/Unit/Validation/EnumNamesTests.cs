using Casazen.Core.Entities;
using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

/// <summary>PL-07 (A1-35, A7-31): the strict enum parse behind the fix for the numeric-value 500.</summary>
public class EnumNamesTests
{
    [Theory]
    [InlineData("ShortTerm", RentalType.ShortTerm)]
    [InlineData("longterm", RentalType.LongTerm)] // case-insensitive
    [InlineData("  Both  ", RentalType.Both)] // trimmed
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

    /// <summary>
    /// The actual A1-35/A7-31 bug: plain <c>Enum.TryParse</c> accepts "7" (no member of RentalType declares it)
    /// and would hand a value nothing downstream was written to handle.
    /// </summary>
    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("99")]
    public void TryParseDefined_NumericStringWithNoDeclaredMember_ReturnsFalse(string value)
    {
        var success = EnumNames.TryParseDefined<RentalType>(value, out _);

        Assert.False(success);
    }

    /// <summary>
    /// Even a numeric string that happens to match a declared ordinal (1 == RentalType.LongTerm) is rejected: API
    /// input must always spell a member name, never its numeric encoding (established convention, see
    /// DashboardController.TryParsePeriod).
    /// </summary>
    [Fact]
    public void TryParseDefined_NumericStringMatchingDeclaredOrdinal_ReturnsFalse()
    {
        var success = EnumNames.TryParseDefined<RentalType>("1", out _);

        Assert.False(success);
    }

    /// <summary>
    /// Plain <c>Enum.TryParse</c> also bitwise-ORs a comma-separated name list into whatever value that combination
    /// happens to equal, even on a non-[Flags] enum (UserRole.PropertyOwner=1 | PropertyManager=2 == UserRole.Guest=3).
    /// </summary>
    [Fact]
    public void TryParseDefined_CommaSeparatedNames_ReturnsFalse()
    {
        var success = EnumNames.TryParseDefined<UserRole>("PropertyOwner,PropertyManager", out _);

        Assert.False(success);
    }
}
