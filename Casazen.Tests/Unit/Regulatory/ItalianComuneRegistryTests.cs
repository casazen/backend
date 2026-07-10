using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

public class ItalianComuneRegistryTests
{
    [Theory]
    [InlineData("Roma", "H501")]
    [InlineData("Roma", "058091")]
    [InlineData("H501", "058091")]
    [InlineData("Como", "013075")]
    [InlineData("como", "013075")]
    [InlineData("Rome", "H501")]
    [InlineData("Firenze", "F205")]
    [InlineData("Firenze", "048017")]
    public void Matches_RecognizesEquivalentComuneIdentifiers(string a, string b) =>
        Assert.True(ItalianComuneRegistry.Matches(a, b));

    [Theory]
    [InlineData("Roma", "Como")]
    [InlineData("H501", "013075")]
    [InlineData("Milano", "H501")]
    public void Matches_ReturnsFalseForDifferentComuni(string a, string b) =>
        Assert.False(ItalianComuneRegistry.Matches(a, b));
}
