namespace Casazen.Core.Entities.Enums;

/// <summary>What a platform admin did to a supplier or to a supplier invite (<see cref="Casazen.Core.Entities.SupplierAdminAuditEntry"/>).</summary>
public enum SupplierAdminAuditAction
{
    /// <summary>The supplier was suspended: no new requests, no status transitions (SU-12).</summary>
    Suspended = 0,

    /// <summary>A suspended supplier was reactivated.</summary>
    Reactivated = 1,

    /// <summary>The invite was sent again with a new link (the previous link stopped working).</summary>
    InviteResent = 2,

    /// <summary>A pending invite was revoked: its link no longer works.</summary>
    InviteRevoked = 3,
}
