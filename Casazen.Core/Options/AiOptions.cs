namespace Casazen.Core.Options;

/// <summary>
/// AI provider (section <c>Ai</c>, Railway <c>Ai__*</c>). Runbook: <c>docs/runbooks/ai.md</c>.
/// </summary>
public class AiOptions
{
    public const string SectionName = "Ai";

    public const string StubProvider = "Stub";

    /// <summary>OpenAI-compatible chat completions at <c>https://api.deepseek.com</c>.</summary>
    public const string DeepSeekProvider = "DeepSeek";

    /// <summary>OpenAI chat completions at <c>https://api.openai.com/v1</c>.</summary>
    public const string OpenAiProvider = "OpenAI";

    /// <summary>Anthropic Messages API at <c>https://api.anthropic.com</c>.</summary>
    public const string AnthropicProvider = "Anthropic";

    /// <summary>Any other service that speaks the OpenAI chat completions API. The base URL is required.</summary>
    public const string CompatibleProvider = "OpenAiCompatible";

    /// <summary>
    /// <see cref="StubProvider"/> (default: no external call), <see cref="DeepSeekProvider"/>, <see cref="OpenAiProvider"/>,
    /// <see cref="AnthropicProvider"/> or <see cref="CompatibleProvider"/>.
    /// </summary>
    public string Provider { get; set; } = StubProvider;

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "deepseek-v4-flash";

    public string AnthropicBaseUrl { get; set; } = "https://api.deepseek.com/anthropic";

    public string OpenAiBaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary><c>max_tokens</c> of a chat completion; also the completion part of the budget reservation.</summary>
    public int MaxCompletionTokens { get; set; } = 2048;

    /// <summary><c>max_tokens</c> of a web search call (AI supplier discovery, behind a feature flag).</summary>
    public int WebSearchMaxTokens { get; set; } = 4096;

    /// <summary>How the external provider appears in the subprocessor list (<c>GET api/legal/subprocessors</c>).</summary>
    public AiSubprocessorOptions Subprocessor { get; set; } = new();

    /// <summary>
    /// True when a prompt can leave for an external provider: the provider is not <see cref="StubProvider"/>, an API key
    /// is set, and the call has a base URL. Paid calls then go through the budget guard and the provider is listed
    /// among the subprocessors.
    /// </summary>
    public bool IsExternalProviderActive() =>
        !IsStub()
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ChatBaseUrl());

    public bool IsStub() =>
        string.IsNullOrWhiteSpace(Provider)
        || string.Equals(Provider, StubProvider, StringComparison.OrdinalIgnoreCase);

    public bool UsesAnthropicMessages() =>
        string.Equals(Provider, AnthropicProvider, StringComparison.OrdinalIgnoreCase);

    /// <summary>Base URL of the chat call for the provider in effect. Empty when a compatible provider has none.</summary>
    public string ChatBaseUrl() =>
        UsesAnthropicMessages() ? AnthropicBaseUrl?.Trim() ?? string.Empty : OpenAiBaseUrl?.Trim() ?? string.Empty;
}

/// <summary>
/// Subprocessor entry of the active external AI provider. The legal details (location, transfer mechanism) are not
/// filled in by code: until they are configured the entry is shown as "details to be completed".
/// </summary>
public class AiSubprocessorOptions
{
    /// <summary>Name shown in the list; the value of <c>Ai:Provider</c> when empty.</summary>
    public string? Name { get; set; }

    /// <summary>Purpose shown in the list; a generic "AI text generation" when empty.</summary>
    public string? Purpose { get; set; }

    /// <summary>Where the provider processes the data (e.g. country). To be completed by the product owner.</summary>
    public string? Region { get; set; }

    /// <summary>Legal basis of a transfer outside the EEA (GDPR chapter V). To be completed by the product owner.</summary>
    public string? TransferMechanism { get; set; }

    public string? Website { get; set; }

    /// <summary>Legal entity and registered office of the provider. To be completed by the product owner.</summary>
    public string? Entity { get; set; }

    /// <summary>
    /// Where the legal details above come from: the official URL(s) and the date they were consulted (PL14-SUBP). Not
    /// shown to users; a detail without a source must not be committed.
    /// </summary>
    public string? Source { get; set; }
}
