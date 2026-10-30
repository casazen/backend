namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What happened that weighs on the reliability of a supplier (the table <c>SupplierReliabilityEvents</c>, SP-REL): the ranking of the
/// suppliers adds up the events of the last days. Stored as numbers and never renumbered.
/// </summary>
public enum SupplierReliabilityEventKind
{
    /// <summary>The supplier answered a request before its deadline: it took it, refused it, answered with a quote or proposed another time.</summary>
    RepliedInTime = 1,

    /// <summary>The supplier did not answer a request before its deadline (and was reachable: a supplier that was suspended, on time off or outside its hours is not penalized).</summary>
    NoReplyInTime = 2,

    /// <summary>The supplier completed a job (<c>Completato</c>). A dispute on the same job takes this back.</summary>
    JobCompletedClean = 3,

    /// <summary>The payer of a job disputed it: today a chargeback opened on Stripe (<c>charge.dispute.created</c>), later the claim of the host (SP-15c-c).</summary>
    DisputeOpened = 4,
}
