namespace Casazen.Core.Entities.Enums;

public enum SupplierAdminAuditAction
{
    Suspended = 0,

    Reactivated = 1,

    InviteResent = 2,

    InviteRevoked = 3,

    CommissionChanged = 4,

    /// <summary>
    /// An admin decided a complaint about a job paid through CasaZen (SP-15c-c, decision VT-D16): for the supplier (it is paid) or for the customer
    /// (the payer gets back what the admin decided and the supplier is penalized). The reason carries the dispute id, the outcome and the amount
    /// given back; never the words of the parties.
    /// </summary>
    ServiceDisputeResolved = 5,
}
