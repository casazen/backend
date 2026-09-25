namespace Casazen.Core.Options;

/// <summary>
/// AI provider (section <c>Ai</c>, Railway <c>Ai__*</c>). Runbook: <c>docs/runbooks/ai.md</c>.
/// </summary>
public class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>The only external provider supported today.</summary>
    public const string DeepSeekProvider = "DeepSeek";

    /// <summary><c>Stub</c> (default: no external call) or <c>DeepSeek</c>.</summary>
    public string Provider { get; set; } = "Stub";

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
    /// True when data can leave for an external provider: <see cref="Provider"/> is <c>DeepSeek</c> and an API key is
    /// set. Paid calls then go through the budget guard and the provider is listed among the subprocessors.
    /// </summary>
    public bool IsExternalProviderActive() =>
        string.Equals(Provider, DeepSeekProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey);
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
}
