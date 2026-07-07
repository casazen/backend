using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class PropertySlugHelperTests
{
    [Theory]
    [InlineData("Villa Parco", "villa-parco")]
    [InlineData("  Chrome E2E!!!  ", "chrome-e2e")]
    public void Sanitize_NormalizesNames(string input, string expected)
    {
        Assert.Equal(expected, PropertySlugHelper.Sanitize(input));
    }

    [Fact]
    public void Validate_RejectsReservedSlug()
    {
        Assert.Throws<ArgumentException>(() => PropertySlugHelper.Validate("checkout"));
    }

    [Fact]
    public void NormalizeOptional_AcceptsValidSlug()
    {
        Assert.Equal("villa-parco", PropertySlugHelper.NormalizeOptional("Villa-Parco"));
    }
}
