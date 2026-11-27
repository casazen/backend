namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the connection of an organization to its Octorate account stands (ADR-023). Stored as its number; 0 is no status.
/// </summary>
public enum OctorateConnectionStatus
{
    /// <summary>The host gave the consent and CasaZen holds a refresh token that Octorate accepted.</summary>
    Connected = 1,

    /// <summary>
    /// Never connected, disconnected by the host, or the refresh token was refused (ADR-023-R12): no call is made for the org until
    /// the host connects again, and nothing retries by itself (the "Reconnect" To do is raised from this status).
    /// </summary>
    Disconnected = 2,

    /// <summary>The connection exists but cannot be used for a reason that is not the consent (set by the tasks that detect one).</summary>
    Error = 3,
}
