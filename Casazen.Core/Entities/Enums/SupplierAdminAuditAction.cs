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

    /// <summary>
    /// An admin set, changed or removed the supplier's own commission on the services paid inside CasaZen (SP-15b, decision D3).
    /// The reason carries the change (old and new percentage, end date) and the admin's note.
    /// </summary>
    CommissionChanged = 4,

    /// <summary>
    /// An admin decided a complaint about a job paid through CasaZen (SP-15c-c, decision VT-D16): for the supplier (it is paid) or for the customer
    /// (the payer gets back what the admin decided and the supplier is penalized). The reason carries the dispute id, the outcome and the amount
    /// given back; never the words of the parties.
    /// </summary>
    ServiceDisputeResolved = 5,
}
