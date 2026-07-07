namespace Casazen.Core.Options;

public class AiOptions
{
    public const string SectionName = "Ai";

    public string Provider { get; set; } = "Stub";

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "deepseek-v4-flash";

    public string AnthropicBaseUrl { get; set; } = "https://api.deepseek.com/anthropic";

    public string OpenAiBaseUrl { get; set; } = "https://api.deepseek.com";
}
