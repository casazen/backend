using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CinComplianceRulesTests
{
    [Theory]
    [InlineData(null, "missing")]
    [InlineData("", "missing")]
    [InlineData("   ", "missing")]
    [InlineData("IT058091C27G5FFZDZ", "valid")]
    [InlineData("IT-058091-C2-7G5FFZDZ", "valid")]
    [InlineData("it 015146 a1 2holv2mz", "valid")]
    [InlineData("IT-12345-0123456789", "invalid")] // old invented format, never a real CIN
    [InlineData("015146-CNI-01894", "invalid")] // Lombardy CIR, not a CIN
    [InlineData("BAD", "invalid")]
    public void ResolveStatus_OfficialFormat_ReturnsExpected(string? cinCode, string expected)
    {
        Assert.Equal(expected, CinComplianceRules.ResolveStatus(cinCode));
    }

    [Theory]
    [InlineData("IT048017B42742QNBZ", true)]
    [InlineData("IT-12345-0123456789", false)]
    [InlineData(null, false)]
    public void IsCompliant_OfficialFormat_ReturnsExpected(string? cinCode, bool expected)
    {
        Assert.Equal(expected, CinComplianceRules.IsCompliant(cinCode));
    }
}
