using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>FD-21 (A8-01, A8-07): the paid AI provider is budgeted before the call, with real or estimated tokens.</summary>
public class BudgetGuardedAiProviderTests
{
    private const int MaxCompletionTokens = 2048;
    private const string Prompt = "Comune: Como\nPageType: ComplianceGuide\nCIN: obbligatorio per affitti brevi in Italia.";

    private readonly Mock<IAiProvider> _inner = new();
    private readonly Mock<IAiBudgetGuard> _budget = new();
    private readonly AiBudgetReservation _reservation;

    public BudgetGuardedAiProviderTests()
    {
        _reservation = new AiBudgetReservation(Guid.NewGuid(), AiTokenEstimator.EstimateCall(Prompt, MaxCompletionTokens));
        _budget.Setup(b => b.ReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(_reservation);
    }

    [Fact]
    public async Task GenerateAsync_BudgetExhausted_NeverCallsProvider()
    {
        _budget.Setup(b => b.ReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        await Assert.ThrowsAsync<AiBudgetExceededException>(() => CreateProvider().GenerateAsync(Prompt, AiModelTier.Economy, "k"));

        _inner.Verify(
            p => p.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateAsync_BeforeTheCall_ReservesPromptEstimatePlusMaxTokens()
    {
        var order = new List<string>();
        _budget.Setup(b => b.ReserveAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("reserve"))
            .ReturnsAsync(_reservation);
        _inner.Setup(p => p.GenerateAsync(Prompt, AiModelTier.Economy, "k", It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("call"))
            .ReturnsAsync(new AiGenerationResult("<p>ok</p>", 10, 5, AiModelTier.Economy, false));

        await CreateProvider().GenerateAsync(Prompt, AiModelTier.Economy, "k");

        Assert.Equal(new[] { "reserve", "call" }, order);
        _budget.Verify(b => b.ReserveAsync(AiTokenEstimator.Estimate(Prompt) + MaxCompletionTokens, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task GenerateAsync_ProviderReportsZeroTokens_CountsEstimatedTokens()
    {
        const string answer = "<article><p>Guida agli affitti brevi a Como: CIN, Alloggiati Web e tassa di soggiorno.</p></article>";
        _inner.Setup(p => p.GenerateAsync(Prompt, AiModelTier.Economy, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(answer, 0, 0, AiModelTier.Economy, false));

        var result = await CreateProvider().GenerateAsync(Prompt, AiModelTier.Economy, "k");

        var expectedPrompt = AiTokenEstimator.Estimate(Prompt);
        var expectedCompletion = AiTokenEstimator.Estimate(answer);
        Assert.True(expectedPrompt > 0 && expectedCompletion > 0);
        Assert.Equal(expectedPrompt, result.PromptTokens);
        Assert.Equal(expectedCompletion, result.CompletionTokens);
        _budget.Verify(b => b.SettleAsync(_reservation, expectedPrompt + expectedCompletion, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_ProviderReportsUsage_CountsPromptAndCompletion()
    {
        _inner.Setup(p => p.GenerateAsync(Prompt, AiModelTier.Economy, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("<p>ok</p>", 321, 654, AiModelTier.Economy, false));

        await CreateProvider().GenerateAsync(Prompt, AiModelTier.Economy, "k");

        _budget.Verify(b => b.SettleAsync(_reservation, 321 + 654, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_CacheHit_ReleasesTheReservation()
    {
        _inner.Setup(p => p.GenerateAsync(Prompt, AiModelTier.Economy, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult("<p>ok</p>", 321, 654, AiModelTier.Economy, FromCache: true));

        await CreateProvider().GenerateAsync(Prompt, AiModelTier.Economy, "k");

        _budget.Verify(b => b.SettleAsync(_reservation, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_ProviderFails_CountsThePromptAndRethrows()
    {
        _inner.Setup(p => p.GenerateAsync(Prompt, AiModelTier.Economy, "k", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider().GenerateAsync(Prompt, AiModelTier.Economy, "k"));

        _budget.Verify(b => b.SettleAsync(_reservation, AiTokenEstimator.Estimate(Prompt), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ExtractUsage_DeepSeekResponse_ReadsPromptAndCompletionTokens()
    {
        const string json = """{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":120,"completion_tokens":280,"total_tokens":400}}""";

        Assert.Equal((120, 280), DeepSeekAiProvider.ExtractUsage(json));
        Assert.Equal((0, 0), DeepSeekAiProvider.ExtractUsage("""{"choices":[]}"""));
    }

    [Fact]
    public void ExtractUsage_WebSearchResponse_ReadsInputAndOutputTokens()
    {
        const string json = """{"content":[{"type":"text","text":"x"}],"usage":{"input_tokens":5000,"output_tokens":700}}""";

        Assert.Equal((5000, 700), DeepSeekWebSearchClient.ExtractUsage(json));
    }

    private BudgetGuardedAiProvider CreateProvider() =>
        new(_inner.Object, _budget.Object, MaxCompletionTokens, Mock.Of<ILogger<BudgetGuardedAiProvider>>());
}
