namespace Casazen.Core.Services;

/// <summary>What one registration or removal of a host in the shared Auth0 tenant did (E40/F01/US02/T01, NF-141). Never an exception.</summary>
public enum Auth0HostRegistrationResult
{
    Registered,

    AlreadyRegistered,

    Removed,

    NotConfigured,

    Rejected,

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
    Task<Auth0HostRegistrationResult> RegisterAsync(string host, CancellationToken cancellationToken = default);

    Task<Auth0HostRegistrationResult> UnregisterAsync(string host, CancellationToken cancellationToken = default);
}
