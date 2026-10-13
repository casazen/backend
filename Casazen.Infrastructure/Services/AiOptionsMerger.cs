using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>Merges the server <c>Ai</c> configuration with the admin override, without sharing mutable instances.</summary>
public static class AiOptionsMerger
{
    public const string EnvironmentSource = "environment";
    public const string AdminSource = "admin";

    public static AiOptions Clone(AiOptions source) => new()
    {
        Provider = source.Provider,
        ApiKey = source.ApiKey,
        Model = source.Model,
        AnthropicBaseUrl = source.AnthropicBaseUrl,
        OpenAiBaseUrl = source.OpenAiBaseUrl,
        MaxCompletionTokens = source.MaxCompletionTokens,
        WebSearchMaxTokens = source.WebSearchMaxTokens,
        Subprocessor = new AiSubprocessorOptions
        {
            Name = source.Subprocessor.Name,
            Purpose = source.Subprocessor.Purpose,
            Region = source.Subprocessor.Region,
            TransferMechanism = source.Subprocessor.TransferMechanism,
            Website = source.Subprocessor.Website,
            Entity = source.Subprocessor.Entity,
            Source = source.Subprocessor.Source,
        },
    };

    /// <summary>
    /// A stored provider replaces the server one and gets that provider's default model and base URL, unless the admin
    /// saved their own. A missing row leaves the server configuration untouched.
    /// </summary>
    public static AiOptions Merge(AiOptions environment, Core.Entities.PlatformAiSettings? row)
    {
        var effective = Clone(environment);
        if (row is null)
            return effective;

        effective.Provider = NormalizeProvider(row.Provider);
        if (!string.IsNullOrWhiteSpace(row.ApiKey))
            effective.ApiKey = row.ApiKey;
        ApplyChoice(effective, row, environment);
        if (!string.Equals(effective.Provider, AiOptions.DeepSeekProvider, StringComparison.OrdinalIgnoreCase))
            effective.Subprocessor = ForeignProviderDetails(effective.Provider);
        return effective;
    }

    public static string NormalizeProvider(string? provider)
    {
        var value = provider?.Trim() ?? string.Empty;
        if (string.Equals(value, AiOptions.StubProvider, StringComparison.OrdinalIgnoreCase))
            return AiOptions.StubProvider;
        if (string.Equals(value, AiOptions.DeepSeekProvider, StringComparison.OrdinalIgnoreCase))
            return AiOptions.DeepSeekProvider;
        if (string.Equals(value, AiOptions.OpenAiProvider, StringComparison.OrdinalIgnoreCase))
            return AiOptions.OpenAiProvider;
        if (string.Equals(value, AiOptions.AnthropicProvider, StringComparison.OrdinalIgnoreCase))
            return AiOptions.AnthropicProvider;
        if (string.Equals(value, AiOptions.CompatibleProvider, StringComparison.OrdinalIgnoreCase))
            return AiOptions.CompatibleProvider;
        throw new DomainRuleException(AiSettingsErrorCodes.ProviderInvalid, "AiProviderInvalid");
    }

    /// <summary>An absolute https URL without user info, or null when empty. Anything else is refused.</summary>
    public static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new DomainRuleException(AiSettingsErrorCodes.BaseUrlInvalid, "AiBaseUrlInvalid");

        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.GetLeftPart(UriPartial.Authority) + path;
    }

    public static AiAdminSettingsDto ToAdmin(AiOptions environment, Core.Entities.PlatformAiSettings? row)
    {
        var effective = Merge(environment, row);
        var storedKey = string.IsNullOrWhiteSpace(row?.ApiKey) ? null : row!.ApiKey;
        return new AiAdminSettingsDto(
            effective.Provider,
            effective.IsExternalProviderActive(),
            !string.IsNullOrWhiteSpace(effective.ApiKey),
            Hint(effective.ApiKey),
            effective.Model,
            effective.ChatBaseUrl(),
            row is null ? EnvironmentSource : AdminSource,
            storedKey is not null);
    }

    /// <summary>
    /// Legal details committed for DeepSeek stay with DeepSeek. Another provider is listed by its name only: the entity,
    /// region and transfer basis are not invented here.
    /// </summary>
    private static AiSubprocessorOptions ForeignProviderDetails(string provider) => new()
    {
        Name = provider,
        Website = provider switch
        {
            AiOptions.OpenAiProvider => "https://openai.com",
            AiOptions.AnthropicProvider => "https://www.anthropic.com",
            _ => null,
        },
    };

    private static void ApplyChoice(AiOptions effective, Core.Entities.PlatformAiSettings row, AiOptions environment)
    {
        var customBase = NormalizeBaseUrl(row.BaseUrl);
        var customModel = string.IsNullOrWhiteSpace(row.Model) ? null : row.Model.Trim();
        switch (effective.Provider)
        {
            case AiOptions.DeepSeekProvider:
                effective.OpenAiBaseUrl = customBase ?? "https://api.deepseek.com";
                effective.Model = customModel
                    ?? (string.Equals(environment.Provider, AiOptions.DeepSeekProvider, StringComparison.OrdinalIgnoreCase)
                        ? environment.Model
                        : "deepseek-v4-flash");
                break;
            case AiOptions.OpenAiProvider:
                effective.OpenAiBaseUrl = customBase ?? "https://api.openai.com/v1";
                effective.Model = customModel ?? "gpt-4o-mini";
                break;
            case AiOptions.AnthropicProvider:
                effective.AnthropicBaseUrl = customBase ?? "https://api.anthropic.com";
                effective.Model = customModel ?? "claude-3-5-haiku-latest";
                break;
            case AiOptions.CompatibleProvider:
                effective.OpenAiBaseUrl = customBase ?? string.Empty;
                effective.Model = customModel ?? "gpt-4o-mini";
                break;
        }
    }

    /// <summary>Last four characters, and only when the key is long enough that they do not reveal it.</summary>
    public static string? Hint(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Trim().Length < 8)
            return null;
        var trimmed = apiKey.Trim();
        return $"••••{trimmed[^4..]}";
    }
}
