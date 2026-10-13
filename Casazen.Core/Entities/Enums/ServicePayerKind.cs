namespace Casazen.Core.Entities.Enums;

/// <summary>Who pays a service request (<see cref="ServiceRequestPayment.PayerKind"/>). Stored as an integer: append only.</summary>
public enum ServicePayerKind
{
    /// <summary>The host org that sent the request (<see cref="ServiceRequestPayment.PayerOrgId"/> is its org).</summary>
    Host = 0,

    /// <summary>
    /// A private customer of the supplier's showcase (SP-10): no org of its own, identified only by the link. Not used before
    /// the showcase booking exists.
    /// </summary>
    Private = 1,
}
