namespace Casazen.Web.DTOs.Auth;

public record UserContextsResponse(
    string UserId,
    IReadOnlyList<ContextBootstrapDto> Contexts,
    string? LastUsedContextKey
);
