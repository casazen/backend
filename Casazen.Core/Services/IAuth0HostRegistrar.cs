namespace Casazen.Core.Services;

/// <summary>What one registration or removal of a host in the shared Auth0 tenant did (E40/F01/US02/T01, NF-141). Never an exception.</summary>
public enum Auth0HostRegistrationResult
{
    /// <summary>The three URLs of the host were missing (all or some) and were added in one write, read back and found.</summary>
    Registered,

    /// <summary>The three URLs of the host were there already: nothing was written.</summary>
    AlreadyRegistered,

    /// <summary>The three URLs of the host are gone: removed in one write and read back, or none of them was there (nothing written).</summary>
    Removed,

    /// <summary>
    /// The registrar is not configured (<c>Auth0:HostSync:ClientId</c> or the Management credentials are missing): no call was made. The owner sign-in on the
    /// domain waits (<c>Pending</c>), as it does while the flag <c>Auth0HostSync</c> is off.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// Auth0 refused the change (a missing scope, an unknown application, an invalid URL), the read-back did not find it, the host is not a valid
    /// host name, or the change would have touched the first callback URL of the application (the platform origin): nothing is half written.
    /// </summary>
    Rejected,

    /// <summary>Auth0 could not be reached, answered an error of its own or limited the rate, or no Management token could be obtained: retried later.</summary>
    Unavailable,
}

/// <summary>
/// The port of the shared Auth0 tenant for the hosts of the agencies (E40/F01/US02/T01, NF-141, site integration plan §5.7): the owner sign-in on an
/// agency's own domain needs the origin <c>https://{host}</c> in the callback, logout and web-origin lists of the SPA application of the environment.
/// The tenant belongs to CasaZen: an agency never touches it. Both operations are idempotent, change only the three strings of the host, and
/// <b>never throw</b> (a cancellation of the caller excepted): the answer says what happened. The flag <c>Auth0HostSync</c> is the caller's gate.
/// </summary>
public interface IAuth0HostRegistrar
{
    /// <summary>Adds <c>https://{host}</c> to the three lists of the application (appended, nothing else moved); nothing is written when it is everywhere already.</summary>
    Task<Auth0HostRegistrationResult> RegisterAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>Removes exactly <c>https://{host}</c> from the three lists of the application; nothing is written when it is nowhere.</summary>
    Task<Auth0HostRegistrationResult> UnregisterAsync(string host, CancellationToken cancellationToken = default);
}
