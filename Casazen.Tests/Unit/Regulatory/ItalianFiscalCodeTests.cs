using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

public class ItalianFiscalCodeTests
{
    [Theory]
    [InlineData("RSSMRA85T10A562S", ItalianFiscalCodeKind.Person)]
    [InlineData("RSSMRA80A01H501U", ItalianFiscalCodeKind.Person)]
    [InlineData("VRDGLI85B42F205E", ItalianFiscalCodeKind.Person)]
    [InlineData("RSSMRA85T10A56NH", ItalianFiscalCodeKind.Person)]
    [InlineData("12345678903", ItalianFiscalCodeKind.Numeric)]
    [InlineData("00123456782", ItalianFiscalCodeKind.Numeric)]
    [InlineData(" rssmra 85t10 a562s ", ItalianFiscalCodeKind.Person)]
    public void Classify_ValidCode_ReturnsItsKind(string code, ItalianFiscalCodeKind expected)
    {
        Assert.Equal(expected, ItalianFiscalCode.Classify(code));
        Assert.True(ItalianFiscalCode.IsValid(code));
    }

    [Theory]
    [InlineData("RSSMRA85T10A562X")]
    [InlineData("RSSMRA85Z10A562S")]
    [InlineData("RSSMRA85T32A562S")]
    [InlineData("RSSMRA85T00A562S")]
    [InlineData("RSSMRA85T72A562S")]
    [InlineData("RSSMRA85T10A56KS")]
    [InlineData("RSSMRA85T10A562")]
    [InlineData("12345678901")]
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
