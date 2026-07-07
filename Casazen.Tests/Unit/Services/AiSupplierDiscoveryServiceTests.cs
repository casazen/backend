using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AiSupplierDiscoveryServiceTests
{
    [Fact]
    public async Task SearchNearbyAsync_WithValidJson_ReturnsSuggestions()
    {
        var webSearch = new Mock<IWebSearchClient>();
        webSearch.Setup(w => w.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Pulizie Roma Srl - Via Nazionale 1");

        var aiProvider = new Mock<IAiProvider>();
        aiProvider.Setup(p => p.GenerateAsync(It.IsAny<string>(), AiModelTier.Economy, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(
                """{"suggestions":[{"name":"Pulizie Roma Srl","address":"Via Nazionale 1, Roma","phone":"06123456","email":null,"rating":4.5,"reviewCount":10,"websiteUrl":null,"mapsUrl":"https://maps.example.com"}]}""",
                100, 50, AiModelTier.Economy, false));

        var service = new AiSupplierDiscoveryService(webSearch.Object, aiProvider.Object, Mock.Of<ILogger<AiSupplierDiscoveryService>>());
        var result = await service.SearchNearbyAsync("Roma", "cleaning");

        Assert.Single(result);
        Assert.Equal("ai_web_search", result[0].Source);
    }
}
