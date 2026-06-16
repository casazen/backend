using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class VatCalculationServiceTests
{
    private readonly VatCalculationService _service = new();

    [Fact]
    public void Calculate_ItCustomer_Applies22PercentVat()
    {
        var result = _service.Calculate(100m, "IT", null, false, false);
        Assert.Equal(VatTreatments.It22, result.VatTreatment);
        Assert.Equal(22m, result.VatAmount);
        Assert.False(result.OssApplied);
    }

    [Fact]
    public void Calculate_EuB2BWithVies_ReverseCharge()
    {
        var result = _service.Calculate(100m, "DE", "DE123456789", true, false);
        Assert.Equal(VatTreatments.EuReverseCharge, result.VatTreatment);
        Assert.Equal(0m, result.VatAmount);
    }

    [Fact]
    public void Calculate_EuB2C_BelowOssThreshold_NoVat()
    {
        var result = _service.Calculate(100m, "FR", null, false, false);
        Assert.Equal(VatTreatments.EuBelowThreshold, result.VatTreatment);
        Assert.False(result.OssApplied);
    }

    [Fact]
    public void Calculate_EuB2C_AtOssThreshold_AppliesOssVat()
    {
        var result = _service.Calculate(100m, "FR", null, false, true);
        Assert.Equal(VatTreatments.EuOss, result.VatTreatment);
        Assert.Equal(22m, result.VatAmount);
        Assert.True(result.OssApplied);
    }
}
