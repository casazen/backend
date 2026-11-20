namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Whether the owner sign-in works on the agency's own domain (E40, NF-141): the callback, logout and web-origin URLs of the org's verified
/// custom domain registered in the shared Auth0 tenant. A registration belongs to ONE domain: a change of the custom domain or of the host
/// mode puts it back to <see cref="NotRequested"/>. Explicit values: the column stores the number, the API the name.
/// </summary>
public enum OwnerLoginHostState
{
    /// <summary>Nobody asked for the owner sign-in on the domain (the default).</summary>
    NotRequested = 0,

    /// <summary>The agency turned the owner portal on for its domain and the registration waits (automatic with <c>Auth0HostSync</c>, by CasaZen otherwise).</summary>
    Pending = 1,

    /// <summary>The domain is registered: owners sign in there and their links may point there.</summary>
    Ready = 2,

    /// <summary>The registration failed and can be retried; nothing is half registered.</summary>
    Failed = 3,
}
