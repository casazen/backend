namespace Casazen.Core.Entities.Enums;

public enum OctorateConnectionStatus
{
    Connected = 1,

    /// <summary>
    /// Never connected, disconnected by the host, or the refresh token was refused (ADR-023-R12): no call is made for the org until
    /// the host connects again, and nothing retries by itself (the "Reconnect" To do is raised from this status).
    /// </summary>
    Disconnected = 2,

    Error = 3,
}
