using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>
/// The AI provider the process actually uses: the server configuration (<c>Ai__*</c>), replaced by the row an admin
/// saved (<see cref="Entities.PlatformAiSettings"/>) when there is one. Callers must not log <see cref="AiOptions.ApiKey"/>.
/// </summary>
public interface IAiRuntimeSettings
{
    /// <summary>Last settings loaded or saved. Before the first load, the server configuration.</summary>
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

/// <param name="ApiKey">Null: leave the stored key as it is. A value replaces it.</param>
/// <param name="ClearApiKey">Drops the stored key, so the server key (if any) is used again.</param>
/// <param name="Model">Null: leave the stored model. Empty: drop it and use the server model.</param>
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
