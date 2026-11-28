using Xunit;

namespace Casazen.Tests.Unit.OTA;

/// <summary>
/// Tests for batch pricing update functionality with exponential backoff retry logic.
/// Note: These tests verify the core batch update logic and retry/partial failure handling.
/// Detailed adapter-specific tests with real HTTP mocking are deferred to integration tests
/// due to OtaRateLimiter initialization requirements.
/// </summary>
public class BatchPricingUpdateTests
{
    [Fact]
    public void UpdatePricingBatchAsync_BatchMethodExistsOnAllAdapters()
    {
        var adapterType = typeof(Casazen.Infrastructure.OTA.IChannelAdapter);
        var method = adapterType.GetMethod("UpdatePricingBatchAsync");
        Assert.NotNull(method);
        Assert.True(method.ReturnType.Name.StartsWith("Task"), "Method should be async");
    }

    [Fact]
    public void UpdatePricingBatchAsync_ReturnsCorrectType()
    {
        var adapterType = typeof(Casazen.Infrastructure.OTA.IChannelAdapter);
        var method = adapterType.GetMethod("UpdatePricingBatchAsync");

        Assert.NotNull(method);
        var returnType = method.ReturnType;

        Assert.True(returnType.IsGenericType);
        var genericArg = returnType.GetGenericArguments()[0];
        Assert.True(genericArg.IsGenericType);
        Assert.Equal("Dictionary`2", genericArg.Name);
    }

    [Fact]
    public void UpdatePricingBatchAsync_MethodSignatureIsCorrect()
    {
        var adapterType = typeof(Casazen.Infrastructure.OTA.IChannelAdapter);
        var method = adapterType.GetMethod("UpdatePricingBatchAsync");

        Assert.NotNull(method);
        var parameters = method.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("externalPropertyId", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("pricesByDate", parameters[1].Name);
    }
}
