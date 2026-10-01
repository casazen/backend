using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// LT-14 (A7-28): the fiscal code of a party is checked with the official algorithm (D.M. 23/12/1976: structure, month,
/// day, check character; omocodia) or, for 11 digits, with the check digit. Same vectors as the frontend
/// <c>src/lib/__tests__/fiscal-code.test.ts</c>.
/// </summary>
public class ItalianFiscalCodeTests
{
    [Theory]
    [InlineData("RSSMRA85T10A562S", ItalianFiscalCodeKind.Person)]
    [InlineData("RSSMRA80A01H501U", ItalianFiscalCodeKind.Person)]
    [InlineData("VRDGLI85B42F205E", ItalianFiscalCodeKind.Person)] // woman: day + 40
    [InlineData("RSSMRA85T10A56NH", ItalianFiscalCodeKind.Person)] // omocodia: 2 of the place code written as N
    [InlineData("12345678903", ItalianFiscalCodeKind.Numeric)]
    [InlineData("00123456782", ItalianFiscalCodeKind.Numeric)]
    [InlineData(" rssmra 85t10 a562s ", ItalianFiscalCodeKind.Person)] // normalized first
    public void Classify_ValidCode_ReturnsItsKind(string code, ItalianFiscalCodeKind expected)
    {
        Assert.Equal(expected, ItalianFiscalCode.Classify(code));
        Assert.True(ItalianFiscalCode.IsValid(code));
    }

    [Theory]
    [InlineData("RSSMRA85T10A562X")] // wrong check character
    [InlineData("RSSMRA85Z10A562S")] // Z is not a month
    [InlineData("RSSMRA85T32A562S")] // day 32
    [InlineData("RSSMRA85T00A562S")] // day 0
    [InlineData("RSSMRA85T72A562S")] // day 72 (woman, 32)
    [InlineData("RSSMRA85T10A56KS")] // K is not an omocodia letter
    [InlineData("RSSMRA85T10A562")] // 15 characters
    [InlineData("12345678901")] // wrong check digit
    [InlineData("1234567890A")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValid_InvalidCode_IsFalse(string? code)
    {
        Assert.False(ItalianFiscalCode.IsValid(code));
        Assert.Null(ItalianFiscalCode.Classify(code));
    }

    [Fact]
    public void CheckCharacter_KnownCode_ReturnsItsCheckCharacter()
    {
        Assert.Equal('S', ItalianFiscalCode.CheckCharacter("RSSMRA85T10A562"));
    }

    [Fact]
    public void Normalize_LowerCaseWithSpaces_UpperCaseWithoutSpaces()
    {
        Assert.Equal("RSSMRA85T10A562S", ItalianFiscalCode.Normalize(" rssmra 85t10\ta562s "));
        Assert.Equal(string.Empty, ItalianFiscalCode.Normalize(null));
    }
}
