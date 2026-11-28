using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Casazen.Tests.Integration;

public class AiProviderMockFactory : CasazenWebApplicationFactory
{
    public List<string> Prompts { get; } = [];

    public Mock<IAiProvider> AiProviderMock { get; } = new();

    public Mock<IWebSearchClient> WebSearchMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        AiProviderMock
            .Setup(p => p.GenerateAsync(
                It.IsAny<string>(),
                It.IsAny<AiModelTier>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, AiModelTier, string, CancellationToken>((prompt, _, _, _) =>
            {
                lock (Prompts)
                    Prompts.Add(prompt);
            })
            .ReturnsAsync(new AiGenerationResult("Fornitore adatto alla richiesta.", 40, 12, AiModelTier.Economy, false));

        builder.ConfigureTestServices(services =>
        {
            RemoveAllOf<IAiProvider>(services);
            services.AddSingleton(AiProviderMock.Object);
            RemoveAllOf<IWebSearchClient>(services);
            services.AddSingleton(WebSearchMock.Object);
        });
    }
}

public sealed class AiSupplierDiscoveryEnabledFactory : AiProviderMockFactory
{
    public const int UserPermitLimit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:AiSupplierDiscovery"] = "true",
                ["RateLimiting:AiPerUser:PermitLimit"] = UserPermitLimit.ToString(),
                ["RateLimiting:AiPerOrg:PermitLimit"] = "1000",
            }));
    }
}
