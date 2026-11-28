namespace Casazen.Core.Options;

public class AiOptions
{
    public const string SectionName = "Ai";

    public const string StubProvider = "Stub";

    public const string DeepSeekProvider = "DeepSeek";

    public const string OpenAiProvider = "OpenAI";

    public const string AnthropicProvider = "Anthropic";

    public const string CompatibleProvider = "OpenAiCompatible";

    public string Provider { get; set; } = StubProvider;

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "deepseek-v4-flash";

    public string AnthropicBaseUrl { get; set; } = "https://api.deepseek.com/anthropic";

    public string OpenAiBaseUrl { get; set; } = "https://api.deepseek.com";

    public int MaxCompletionTokens { get; set; } = 2048;

    public int WebSearchMaxTokens { get; set; } = 4096;

    public AiSubprocessorOptions Subprocessor { get; set; } = new();

    public bool IsExternalProviderActive() =>
        !IsStub()
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ChatBaseUrl());

    public bool IsStub() =>
        string.IsNullOrWhiteSpace(Provider)
        || string.Equals(Provider, StubProvider, StringComparison.OrdinalIgnoreCase);

    public bool UsesAnthropicMessages() =>
        string.Equals(Provider, AnthropicProvider, StringComparison.OrdinalIgnoreCase);

    public bool AcceptsImages() =>
        !IsStub() && !string.Equals(Provider, DeepSeekProvider, StringComparison.OrdinalIgnoreCase);

    public string ChatBaseUrl() =>
        UsesAnthropicMessages() ? AnthropicBaseUrl?.Trim() ?? string.Empty : OpenAiBaseUrl?.Trim() ?? string.Empty;
}

public class AiSubprocessorOptions
{
    public string? Name { get; set; }

    public string? Purpose { get; set; }

    public string? Region { get; set; }

    /// <summary>Legal basis of a transfer outside the EEA (GDPR chapter V). To be completed by the product owner.</summary>
    public string? TransferMechanism { get; set; }

    public string? Website { get; set; }

    public string? Entity { get; set; }

    public string? Source { get; set; }
}
