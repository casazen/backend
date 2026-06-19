using Casazen.Core.DTOs;

namespace Casazen.Core.Services;

/// <summary>
/// Maps incoming HTTP Host to org branding (F0 spike #288). No custom-domain DB lookup yet.
/// </summary>
public interface IPublicHostResolver
{
    Task<ResolveHostResponseDto?> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
