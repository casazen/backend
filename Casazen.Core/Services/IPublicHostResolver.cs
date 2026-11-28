using Casazen.Core.DTOs;

namespace Casazen.Core.Services;

public interface IPublicHostResolver
{
    Task<ResolveHostResponseDto?> ResolveAsync(string host, CancellationToken cancellationToken = default);

    void InvalidateCacheForHost(string host);
}
