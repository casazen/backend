namespace Casazen.Web.DTOs.Auth;

public record ContextBootstrapDto(
    string ContextKey,
    string DisplayName,
    string RoleKey,
    IReadOnlyList<string> Permissions,
    string DefaultRoute
);
