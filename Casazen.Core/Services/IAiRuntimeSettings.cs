using Casazen.Core.Options;

namespace Casazen.Core.Services;

public interface IAiRuntimeSettings
{
    AiOptions Peek();

    Task<AiOptions> GetEffectiveAsync(CancellationToken cancellationToken = default);

    Task<AiAdminSettingsDto> GetAdminViewAsync(CancellationToken cancellationToken = default);

    Task<AiAdminSettingsDto> UpdateAsync(UpdateAiSettingsCommand command, CancellationToken cancellationToken = default);
}

/// <param name="Source"><c>environment</c> when no admin row exists, otherwise <c>admin</c>.</param>
/// <param name="ApiKeyHint">Last characters of the key in effect, or null. Never the key itself.</param>
/// <param name="HasStoredKey">An admin saved a key that replaces the one of the server.</param>
public record AiAdminSettingsDto(
    string Provider,
    bool ExternalActive,
    bool ApiKeyConfigured,
    string? ApiKeyHint,
    string Model,
    string BaseUrl,
    string Source,
    bool HasStoredKey);

public record UpdateAiSettingsCommand(
    string Provider,
    string? ApiKey,
    bool ClearApiKey,
    string? Model,
    string? BaseUrl,
    string ActorUserId);

public static class AiSettingsErrorCodes
{
    public const string ProviderInvalid = "ai_provider_invalid";
    public const string BaseUrlInvalid = "ai_base_url_invalid";
}
