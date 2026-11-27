using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// CIN format (A5-05, R-02). Real CINs come from <c>.claude/context/regulations/cin.md</c>, section
/// "Formato verificato (2026-09)".
/// </summary>
public class CinFormatTests
{
    [Theory]
    [InlineData("CH", false)]
    [InlineData("ch", false)]
    [InlineData("IT", true)]
    [InlineData(null, true)]
    public void ItalianCin_AppliesByCountry(string? country, bool expected) =>
        Assert.Equal(expected, CinFormat.IsRequiredForCountry(country));

    public static TheoryData<string> RealCins => new()
    {
        "IT058091C27G5FFZDZ",
        "IT027042C2IT3TRNHJ",
        "IT058091A1K2XKTJ9H",
        "IT015146A12HOLV2MZ",
        "IT048017A1O9HCDONC",
        "IT048017A1FOG7WU8P",
        "IT048017B42742QNBZ",
    };

    [Theory]
    [MemberData(nameof(RealCins))]
    public void IsValid_RealEighteenCharacterCin_ReturnsTrue(string cin)
    {
        Assert.Equal(18, cin.Length);
        Assert.True(CinFormat.IsValid(cin));
        Assert.Equal(CinStatus.Valid, CinFormat.GetStatus(cin));
    }

    [Theory]
    [InlineData("IT-058091-C2-7G5FFZDZ")]
    [InlineData("it 058091 c2 7g5ffzdz")]
    [InlineData("IT 058 091 C2 7G5FFZDZ")]
    [InlineData("  IT058091C27G5FFZDZ  ")]
    [InlineData("IT058091\u00A0C27G5FFZDZ")]
    [InlineData("IT058091\u2013C2\u20147G5FFZDZ")]
    [InlineData("IT058091\tC27G5FFZDZ")]
    public void Normalize_WithSpacesHyphensOrLowerCase_ReturnsCompactUpperCaseCin(string input)
    {
        Assert.Equal("IT058091C27G5FFZDZ", CinFormat.Normalize(input));
        Assert.True(CinFormat.IsValid(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" - ")]
    public void Normalize_NothingLeft_ReturnsNullAndStatusMissing(string? input)
    {
        Assert.Null(CinFormat.Normalize(input));
        Assert.False(CinFormat.IsValid(input));
        Assert.Equal(CinStatus.Missing, CinFormat.GetStatus(input));
    }

    [Fact]
    public void Normalize_OtherPunctuation_IsKeptSoThatValidationRejectsIt()
    {
        Assert.Equal("CIN:IT058091C27G5FFZDZ", CinFormat.Normalize("CIN: IT058091C27G5FFZDZ"));
        Assert.False(CinFormat.IsValid("CIN: IT058091C27G5FFZDZ"));
        Assert.False(CinFormat.IsValid("IT058091C2.7G5FFZDZ"));
        Assert.False(CinFormat.IsValid("IT058091C2/7G5FFZDZ"));
    }

    [Theory]
    [InlineData("IT-12345-1234567890")]
    [InlineData("IT-12345-0123456789")]
    [InlineData("IT123451234567890")]
    [InlineData("it 12345 0123456789")]
    public void IsValid_OldInventedFormat_ReturnsFalse(string legacy)
    {
        Assert.False(CinFormat.IsValid(legacy));
        Assert.Equal(CinStatus.Invalid, CinFormat.GetStatus(legacy));
    }

    [Theory]
    [InlineData("IT039007B100000")]
    [InlineData("IT0580911A7G5FFZDZ")]
    public void IsValid_ShorterRandomPartOrDigitCategory_ReturnsTrue(string cin)
    {
        Assert.True(CinFormat.IsValid(cin));
    }

    [Theory]
    [InlineData("015146-CNI-01894")]
    [InlineData("IT058091C27G5FFZDZX")]
    [InlineData("IT058091C2")]
    [InlineData("IT05809C27G5FFZDZ")]
    [InlineData("FR058091C27G5FFZDZ")]
    [InlineData("IT058091C27G5FFZ_Z")]
    [InlineData("IT\u0660\u0665\u0668\u0660\u0669\u0661C27G5FFZDZ")]
    [InlineData("BAD")]
    public void IsValid_NotOfficialFormat_ReturnsFalse(string value)
    {
        Assert.False(CinFormat.IsValid(value));
        Assert.Equal(CinStatus.Invalid, CinFormat.GetStatus(value));
    }

    [Fact]
    public void GetIstatComuneCode_ValidCin_ReturnsEmbeddedSixDigitCode()
    {
        Assert.Equal("058091", CinFormat.GetIstatComuneCode("it-058091-c2-7g5ffzdz"));
        Assert.Null(CinFormat.GetIstatComuneCode("IT-12345-0123456789"));
        Assert.Null(CinFormat.GetIstatComuneCode(null));
    }

    [Theory]
    [InlineData("IT058091C27G5FFZDZ", "058091", false)]
    [InlineData("IT058091C27G5FFZDZ", "015146", true)]
    [InlineData("IT058091C27G5FFZDZ", null, false)]
    [InlineData("IT058091C27G5FFZDZ", "Roma", false)]
    [InlineData("IT-12345-0123456789", "058091", false)]
    public void HasIstatComuneMismatch_ComparesOnlyWithTrustedCode(string cin, string? istat, bool expected)
    {
        Assert.Equal(expected, CinFormat.HasIstatComuneMismatch(cin, istat));
    }
}
