using Casazen.Core.Validation;
using Xunit;

namespace Casazen.Tests.Unit.Validation;

public class IanaTimeZoneAttributeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Europe/Rome")]
    [InlineData(" Europe/Rome ")]
    [InlineData("America/New_York")]
    public void IsValid_MissingOrIanaId_Passes(string? value)
    {
        Assert.True(new IanaTimeZoneAttribute().IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus")]
    [InlineData("W. Europe Standard Time")]
    public void IsValid_BlankUnknownOrWindowsId_Fails(string value)
    {
        Assert.False(new IanaTimeZoneAttribute().IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Casa")]
    public void NotBlankWhenPresent_MissingOrText_Passes(string? value)
    {
        Assert.True(new NotBlankWhenPresentAttribute().IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void NotBlankWhenPresent_BlankText_Fails(string value)
    {
        Assert.False(new NotBlankWhenPresentAttribute().IsValid(value));
    }
}
