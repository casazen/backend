namespace Casazen.Core.Entities.Enums;

/// <summary>What an actor did to a host invite (<see cref="HostInviteAuditEntry"/>). Stored as an integer: append only.</summary>
public enum HostInviteAuditAction
{
    /// <summary>The invite was created and its email queued.</summary>
    Created = 0,

    /// <summary>The invite was sent again with a new link (the previous link stopped working).</summary>
    Resent = 1,

    /// <summary>A pending invite was revoked: its link no longer works.</summary>
    Revoked = 2,
}
