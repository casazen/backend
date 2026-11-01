using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// The <see cref="IAiProvider"/> of the process. Each call reads the settings in effect: an active external provider
/// goes through the platform budget, DeepSeek without a key answers empty, and <c>Stub</c> never calls anyone.
/// </summary>
public sealed class SwitchingAiProvider(
    IAiRuntimeSettings settings,
    DeepSeekAiProvider deepSeek,
    StubAiProvider stub,
    IAiBudgetGuard budget,
    ILogger<BudgetGuardedAiProvider> budgetLogger) : IAiProvider
{
    public async Task<AiGenerationResult> GenerateAsync(
        string prompt,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        var options = await settings.GetEffectiveAsync(cancellationToken);
        if (options.IsExternalProviderActive())
        {
            var guarded = new BudgetGuardedAiProvider(
                new BoundDeepSeek(deepSeek, options),
                budget,
                options.MaxCompletionTokens,
                budgetLogger);
            return await guarded.GenerateAsync(prompt, tier, cacheKey, cancellationToken);
        }

        if (!options.IsStub())
            return await deepSeek.GenerateAsync(prompt, tier, cacheKey, options, cancellationToken);

        return await stub.GenerateAsync(prompt, tier, cacheKey, cancellationToken);
    }

    /// <summary>
    /// A call with instructions and images, routed like a text call: through the platform budget when an external provider is active
    /// (the images are counted in the reservation), the stub otherwise. <see cref="AiRequest.MaxCompletionTokens"/> is the completion
    /// part of the reservation when the caller sets it.
    /// </summary>
    public async Task<AiGenerationResult> GenerateAsync(
        AiRequest request,
        AiModelTier tier,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = await settings.GetEffectiveAsync(cancellationToken);
        if (options.IsExternalProviderActive())
        {
            var guarded = new BudgetGuardedAiProvider(
                new BoundDeepSeek(deepSeek, options),
                budget,
                request.MaxCompletionTokens ?? options.MaxCompletionTokens,
                budgetLogger);
            return await guarded.GenerateAsync(request, tier, cacheKey, cancellationToken);
        }

        if (!options.IsStub())
            return await deepSeek.GenerateAsync(request, tier, cacheKey, options, cancellationToken);

        return await ((IAiProvider)stub).GenerateAsync(request, tier, cacheKey, cancellationToken);
    }

    private sealed class BoundDeepSeek(DeepSeekAiProvider inner, AiOptions options) : IAiProvider
    {
        public Task<AiGenerationResult> GenerateAsync(
            string prompt,
            AiModelTier tier,
            string cacheKey,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(prompt, tier, cacheKey, options, cancellationToken);

        public Task<AiGenerationResult> GenerateAsync(
            AiRequest request,
            AiModelTier tier,
            string cacheKey,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(request, tier, cacheKey, options, cancellationToken);
    }
}
