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
    public static TheoryData<string> RealCins => new()
    {
        "IT058091C27G5FFZDZ", // Roma, apartment
        "IT027042C2IT3TRNHJ", // Venezia, apartment
        "IT058091A1K2XKTJ9H", // Roma, hotel
        "IT015146A12HOLV2MZ", // Milano, hotel
        "IT048017A1O9HCDONC", // Firenze, hotel
        "IT048017A1FOG7WU8P", // Firenze, hotel
        "IT048017B42742QNBZ", // Firenze, B4 category
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
    [InlineData("IT058091\u00A0C27G5FFZDZ")] // non-breaking space
    [InlineData("IT058091\u2013C2\u20147G5FFZDZ")] // en dash, em dash
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
    [InlineData("IT039007B100000")] // random part of 5: "at most 8" in the official composition
    [InlineData("IT0580911A7G5FFZDZ")] // category is "2 characters" in the official text: digits accepted
    public void IsValid_ShorterRandomPartOrDigitCategory_ReturnsTrue(string cin)
    {
        Assert.True(CinFormat.IsValid(cin));
    }

    [Theory]
    [InlineData("015146-CNI-01894")] // Lombardy CIR, not a CIN
    [InlineData("IT058091C27G5FFZDZX")] // 19 characters
    [InlineData("IT058091C2")] // no random part
    [InlineData("IT05809C27G5FFZDZ")] // 5-digit ISTAT code
    [InlineData("FR058091C27G5FFZDZ")]
    [InlineData("IT058091C27G5FFZ_Z")]
    [InlineData("IT\u0660\u0665\u0668\u0660\u0669\u0661C27G5FFZDZ")] // non-ASCII digits
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
    [InlineData("IT058091C27G5FFZDZ", null, false)] // no trusted code: nothing to compare
    [InlineData("IT058091C27G5FFZDZ", "Roma", false)]
    [InlineData("IT-12345-0123456789", "058091", false)] // invalid CIN: the format error is reported elsewhere
    public void HasIstatComuneMismatch_ComparesOnlyWithTrustedCode(string cin, string? istat, bool expected)
    {
        Assert.Equal(expected, CinFormat.HasIstatComuneMismatch(cin, istat));
    }
}
