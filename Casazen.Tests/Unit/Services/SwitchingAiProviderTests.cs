using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SwitchingAiProviderTests
{
    [Fact]
    public async Task GenerateAsync_Stub_DoesNotCallTheExternalProvider()
    {
        var handler = new CountingHandler();
        var provider = Provider(new AiOptions { Provider = "Stub", ApiKey = "sk-unused" }, handler);

        var result = await provider.GenerateAsync("Comune: Como\n", AiModelTier.Economy, "k");

        Assert.False(result.ProviderConfigured);
        Assert.Contains("Como", result.Content);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GenerateAsync_DeepSeekWithoutKey_AnswersEmptyWithoutACall()
    {
        var handler = new CountingHandler();
        var provider = Provider(new AiOptions { Provider = AiOptions.DeepSeekProvider }, handler);

        var result = await provider.GenerateAsync("prompt", AiModelTier.Economy, "k");

        Assert.False(result.ProviderConfigured);
        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(0, handler.Calls);
    }

    private static SwitchingAiProvider Provider(AiOptions options, CountingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("DeepSeek")).Returns(new HttpClient(handler));
        var deepSeek = new DeepSeekAiProvider(
            factory.Object,
            Options.Create(new AiOptions()),
            NullLogger<DeepSeekAiProvider>.Instance);
        return new SwitchingAiProvider(
            new FixedAiSettings(options),
            deepSeek,
            new StubAiProvider(NullLogger<StubAiProvider>.Instance),
            Mock.Of<IAiBudgetGuard>(),
            NullLogger<BudgetGuardedAiProvider>.Instance);
    }

    private sealed class FixedAiSettings(AiOptions options) : IAiRuntimeSettings
    {
        public AiOptions Peek() => options;

        public Task<AiOptions> GetEffectiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(options);

        public Task<AiAdminSettingsDto> GetAdminViewAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiAdminSettingsDto> UpdateAsync(UpdateAiSettingsCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
