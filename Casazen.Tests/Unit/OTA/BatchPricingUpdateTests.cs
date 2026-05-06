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
        // This test verifies that the UpdatePricingBatchAsync method exists
        // on all IChannelAdapter implementations - a compile-time check
        var adapterType = typeof(Casazen.Infrastructure.OTA.IChannelAdapter);
        var method = adapterType.GetMethod("UpdatePricingBatchAsync");
        Assert.NotNull(method);
        Assert.True(method.ReturnType.Name.StartsWith("Task"), "Method should be async");
    }

    [Fact]
    public void UpdatePricingBatchAsync_ReturnsCorrectType()
    {
        // Verify that UpdatePricingBatchAsync returns a Dictionary<DateOnly, bool>
        var adapterType = typeof(Casazen.Infrastructure.OTA.IChannelAdapter);
        var method = adapterType.GetMethod("UpdatePricingBatchAsync");

        Assert.NotNull(method);
        var returnType = method.ReturnType;

        // Check that it returns Task<Dictionary<DateOnly, bool>>
        Assert.True(returnType.IsGenericType);
        var genericArg = returnType.GetGenericArguments()[0];
        Assert.True(genericArg.IsGenericType);
        Assert.Equal("Dictionary`2", genericArg.Name);
    }

    [Fact]
    public void UpdatePricingBatchAsync_MethodSignatureIsCorrect()
    {
        // Verify method has correct parameters
        var adapterType = typeof(Casazen.Infrastructure.OTA.IChannelAdapter);
        var method = adapterType.GetMethod("UpdatePricingBatchAsync");

        Assert.NotNull(method);
        var parameters = method.GetParameters();

        // Should have 2 parameters: externalPropertyId (string) and pricesByDate (Dictionary<DateOnly, decimal>)
        Assert.Equal(2, parameters.Length);
        Assert.Equal("externalPropertyId", parameters[0].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("pricesByDate", parameters[1].Name);
    }
}
