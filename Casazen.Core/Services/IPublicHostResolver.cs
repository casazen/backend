using Casazen.Core.DTOs;

namespace Casazen.Core.Services;

/// <summary>
/// Maps incoming HTTP Host to org branding for edge middleware (F0 #288, custom domain #298).
/// </summary>
public interface IPublicHostResolver
{
    Task<ResolveHostResponseDto?> ResolveAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>Best-effort cache bust for a specific host after a domain set/verify.</summary>
    void InvalidateCacheForHost(string host);
}
