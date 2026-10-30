namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a complaint about a job paid through CasaZen stands (SP-15c-c, decision VT-D16). Stored as an integer: append only. While a dispute is
/// open (<see cref="AwaitingSupplier"/> or <see cref="AwaitingDecision"/>) the money CasaZen holds is not released.
/// </summary>
public enum ServiceDisputeStatus
{
    /// <summary>
    /// The complaint was made (the task's <c>Open</c>) and the supplier has <c>SupplierPayments:DisputeSupplierReplyHours</c> to reply. The
    /// payment is <see cref="ServicePaymentStatus.Disputed"/>.
    /// </summary>
    AwaitingSupplier = 0,

    /// <summary>The supplier replied, or its time to reply passed: an admin decides (the target is <c>DisputeDecisionTargetHours</c>).</summary>
    AwaitingDecision = 1,

    /// <summary>The complaint was unfounded: the supplier is paid as if nothing had happened.</summary>
    ResolvedForSupplier = 2,

    /// <summary>The supplier was at fault: the payer gets back what the admin decided (at most the price minus the commission), which CasaZen keeps.</summary>
    ResolvedForCustomer = 3,

    /// <summary>The payer took the complaint back: the supplier is paid as if nothing had happened.</summary>
    Withdrawn = 4,
}
