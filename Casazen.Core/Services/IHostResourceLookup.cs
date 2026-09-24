using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// Light lookups that turn an id into the <see cref="HostResource"/> the authorization handler checks (TN-3), without
/// loading the whole entity graph. Reads go through the tenant query filter: a row of another org answers <c>null</c>,
/// exactly like a missing one (the caller maps it to 404).
/// </summary>
public interface IHostResourceLookup
{
    Task<HostResource?> ForPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default);
}
