using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CinComplianceRulesTests
{
    [Theory]
    [InlineData(null, "missing")]
    [InlineData("", "missing")]
    [InlineData("IT-12345-0123456789", "valid")]
    [InlineData("BAD", "invalid")]
    public void ResolveStatus_ReturnsExpected(string? cinCode, string expected)
    {
        Assert.Equal(expected, CinComplianceRules.ResolveStatus(cinCode));
    }

    [Fact]
    public void DaysUntilDeadline_UsesRegulatoryDate()
    {
        var days = CinComplianceRules.DaysUntilDeadline(new DateOnly(2026, 2, 22));
        Assert.Equal(7, days);
    }
}
