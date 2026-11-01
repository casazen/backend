using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The calls with images (<see cref="AiRequest"/>): what each provider receives, and the text alone when the images are refused.</summary>
public class DeepSeekAiProviderImagesTests
{
    private const string ChatAnswer =
        "{\"choices\":[{\"message\":{\"content\":\"{\\\"title\\\":\\\"T\\\",\\\"description\\\":\\\"D\\\"}\"}}],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":200}}";

    private const string AnthropicAnswer =
        "{\"content\":[{\"type\":\"text\",\"text\":\"risposta\"}],\"usage\":{\"input_tokens\":800,\"output_tokens\":150}}";

    private static readonly AiImage Photo = new("image/jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0x01]);

    private static AiRequest Request(int photos = 2) =>
        new("I dati dell'alloggio", Enumerable.Repeat(Photo, photos).ToList(), "Le regole", 0.6, 1200);

    [Fact]
    public async Task GenerateAsync_OpenAiCompatible_SendsTheSystemPromptAndTheImagesAsDataUrls()
    {
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var provider = Provider(handler);

        var result = await provider.GenerateAsync(Request(), AiModelTier.Standard, "k", Options(AiOptions.CompatibleProvider));

        Assert.Equal(2, result.ImagesRead);
        Assert.Equal(900, result.PromptTokens);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Le regole", messages[0].GetProperty("content").GetString());
        var parts = messages[1].GetProperty("content");
        Assert.Equal(3, parts.GetArrayLength());
        Assert.StartsWith("data:image/jpeg;base64,", parts[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("text", parts[2].GetProperty("type").GetString());
        Assert.Equal(0.6, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(1200, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("https://ai.example/v1/chat/completions", Assert.Single(handler.Urls));
    }

    [Fact]
    public async Task GenerateAsync_Anthropic_SendsImageBlocksBeforeTheText()
    {
        var handler = new RecordingHandler(_ => Ok(AnthropicAnswer));
        var provider = Provider(handler);

        var result = await provider.GenerateAsync(Request(1), AiModelTier.Standard, "k", Options(AiOptions.AnthropicProvider));

        Assert.Equal("risposta", result.Content);
        Assert.Equal(1, result.ImagesRead);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("Le regole", body.RootElement.GetProperty("system").GetString());
        var blocks = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", blocks[0].GetProperty("type").GetString());
        Assert.Equal("base64", blocks[0].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("text", blocks[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task GenerateAsync_DeepSeek_SendsTheTextAlone()
    {
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var provider = Provider(handler);

        var result = await provider.GenerateAsync(Request(), AiModelTier.Standard, "k", Options(AiOptions.DeepSeekProvider));

        Assert.Equal(0, result.ImagesRead);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("messages")[1].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task GenerateAsync_ImagesRefused_SendsTheTextAloneOnce()
    {
        var handler = new RecordingHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"image input not supported\"}") }
            : Ok(ChatAnswer));
        var provider = Provider(handler);

        var result = await provider.GenerateAsync(Request(), AiModelTier.Standard, "k", Options(AiOptions.CompatibleProvider));

        Assert.Equal(0, result.ImagesRead);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("image_url", handler.Bodies[0]);
        Assert.DoesNotContain("image_url", handler.Bodies[1]);
    }

    [Fact]
    public async Task GenerateAsync_ServerError_IsNotRetried()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = Provider(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GenerateAsync(Request(), AiModelTier.Standard, "k", Options(AiOptions.CompatibleProvider)));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task GenerateAsync_NoKey_AnswersUnconfiguredWithoutACall()
    {
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var provider = Provider(handler);
        var options = Options(AiOptions.CompatibleProvider);
        options.ApiKey = null;

        var result = await provider.GenerateAsync(Request(), AiModelTier.Standard, "k", options);

        Assert.False(result.ProviderConfigured);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task GenerateAsync_Stub_ThroughTheSwitch_CallsNobody()
    {
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var switching = new SwitchingAiProvider(
            new FixedSettings(new AiOptions { Provider = AiOptions.StubProvider }),
            Provider(handler),
            new StubAiProvider(NullLogger<StubAiProvider>.Instance),
            Mock.Of<IAiBudgetGuard>(),
            NullLogger<BudgetGuardedAiProvider>.Instance);

        var result = await switching.GenerateAsync(Request(), AiModelTier.Standard, "k");

        Assert.False(result.ProviderConfigured);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task GenerateAsync_ExternalProvider_ReservesTheImagesInTheBudget()
    {
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var budget = new Mock<IAiBudgetGuard>();
        long reserved = 0;
        budget.Setup(b => b.ReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<long, CancellationToken>((tokens, _) => reserved = tokens)
            .ReturnsAsync(new AiBudgetReservation(Guid.NewGuid(), 1));
        var switching = new SwitchingAiProvider(
            new FixedSettings(Options(AiOptions.CompatibleProvider)),
            Provider(handler),
            new StubAiProvider(NullLogger<StubAiProvider>.Instance),
            budget.Object,
            NullLogger<BudgetGuardedAiProvider>.Instance);

        await switching.GenerateAsync(Request(), AiModelTier.Standard, "k");

        Assert.True(reserved >= 2 * AiTokenEstimator.TokensPerImage + 1200);
        budget.Verify(b => b.SettleAsync(It.IsAny<AiBudgetReservation>(), 1100, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AiOptions Options(string provider) => new()
    {
        Provider = provider,
        ApiKey = "sk-test",
        Model = "vision-model",
        OpenAiBaseUrl = "https://ai.example/v1",
        AnthropicBaseUrl = "https://anthropic.example",
    };

    private static DeepSeekAiProvider Provider(RecordingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new DeepSeekAiProvider(factory.Object, Microsoft.Extensions.Options.Options.Create(new AiOptions()), NullLogger<DeepSeekAiProvider>.Instance);
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Urls.Add(request.RequestUri!.ToString());
            return answer(Bodies.Count);
        }
    }

    private sealed class FixedSettings(AiOptions options) : IAiRuntimeSettings
    {
        public AiOptions Peek() => options;

        public Task<AiOptions> GetEffectiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(options);

        public Task<AiAdminSettingsDto> GetAdminViewAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiAdminSettingsDto> UpdateAsync(UpdateAiSettingsCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
