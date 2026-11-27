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

    [Theory]
    [InlineData(AiOptions.DeepSeekProvider, "deepseek-v4-flash", true)]
    [InlineData(AiOptions.DeepSeekProvider, "deepseek-chat", false)]
    [InlineData(AiOptions.CompatibleProvider, "deepseek-v4-flash", false)]
    public async Task GenerateAsync_DirectAnswer_OnlySendsTheThinkingToggleToSupportedDeepSeekModels(
        string providerName, string model, bool expectsToggle)
    {
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var options = Options(providerName);
        options.Model = model;

        await Provider(handler).GenerateAsync(Request(0) with { DisableThinking = true }, AiModelTier.Standard, "k", options);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(expectsToggle, body.RootElement.TryGetProperty("thinking", out var thinking));
        if (expectsToggle)
            Assert.Equal("disabled", thinking.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(AiOptions.CompatibleProvider, true)]
    [InlineData(AiOptions.OpenAiProvider, false)]
    [InlineData(AiOptions.DeepSeekProvider, false)]
    public async Task GenerateAsync_DirectAnswer_AsksACompatibleServerToSwitchTheReasoningOffThroughTheChatTemplate(string providerName, bool expectsSwitch)
    {
        // #936: a model that reasons before it answers (Qwen on the compatible server of the platform) spent the short limit of the assistant on its reasoning.
        var handler = new RecordingHandler(_ => Ok(ChatAnswer));
        var options = Options(providerName);
        options.Model = "reasoning-model";

        await Provider(handler).GenerateAsync(Request(0) with { DisableThinking = true }, AiModelTier.Economy, "k", options);
        await Provider(handler).GenerateAsync(Request(0), AiModelTier.Economy, "k", options);

        using var direct = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(expectsSwitch, direct.RootElement.TryGetProperty(DeepSeekAiProvider.ChatTemplateKwargsMember, out var kwargs));
        if (expectsSwitch)
            Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.DoesNotContain(DeepSeekAiProvider.ChatTemplateKwargsMember, handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ACompatibleServerThatRefusesTheSwitch_GetsTheRequestWithoutIt_AndIsNotSentItAgain()
    {
        var handler = new RecordingHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"unknown member chat_template_kwargs\"}") }
            : Ok(ChatAnswer));
        var options = Options(AiOptions.CompatibleProvider);
        options.OpenAiBaseUrl = "https://refusing-switch.example/v1";

        var first = await Provider(handler).GenerateAsync(Request(0) with { DisableThinking = true }, AiModelTier.Economy, "k", options);
        var second = await Provider(handler).GenerateAsync(Request(0) with { DisableThinking = true }, AiModelTier.Economy, "k", options);

        Assert.Equal("{\"title\":\"T\",\"description\":\"D\"}", first.Content);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains(DeepSeekAiProvider.ChatTemplateKwargsMember, handler.Bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain(DeepSeekAiProvider.ChatTemplateKwargsMember, handler.Bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain(DeepSeekAiProvider.ChatTemplateKwargsMember, handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ARequestRefusedForAnotherReason_StillFails_AndTheSwitchIsKeptForTheNextCall()
    {
        var handler = new RecordingHandler(call => call <= 2
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"context too long\"}") }
            : Ok(ChatAnswer));
        var options = Options(AiOptions.CompatibleProvider);
        options.OpenAiBaseUrl = "https://refusing-request.example/v1";

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Provider(handler).GenerateAsync(Request(0) with { DisableThinking = true }, AiModelTier.Economy, "k", options));
        await Provider(handler).GenerateAsync(Request(0) with { DisableThinking = true }, AiModelTier.Economy, "k", options);

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Contains(DeepSeekAiProvider.ChatTemplateKwargsMember, handler.Bodies[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_AReasoningCutByTheLimit_IsAnEmptyAnswer_NotAFailure()
    {
        var handler = new RecordingHandler(_ => Ok(
            "{\"choices\":[{\"message\":{\"content\":null,\"reasoning_content\":\"The guest asks\"},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":500,\"completion_tokens\":300}}"));

        var result = await Provider(handler).GenerateAsync(
            Request(0) with { DisableThinking = true }, AiModelTier.Economy, "k", Options(AiOptions.CompatibleProvider));

        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(300, result.CompletionTokens);
        Assert.Equal("length", DeepSeekAiProvider.ExtractFinishReason("{\"choices\":[{\"finish_reason\":\"length\"}]}"));
        Assert.Null(DeepSeekAiProvider.ExtractFinishReason("{\"choices\":[]}"));
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
