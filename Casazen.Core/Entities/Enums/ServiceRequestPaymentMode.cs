namespace Casazen.Core.Entities.Enums;

/// <summary>
/// How a service request is paid (SP-15a, decision D2 and D5). Decided <b>once, when the supplier takes the request</b>
/// (<c>take</c>, or the host's acceptance of the time the supplier proposed) and kept from then on, so the KYC rule "no
/// payment before the supplier's Stripe account is ready" cannot be bypassed by a change that comes later. Stored as an
/// integer and serialized by name: append only, never renumber. The requests that exist before SP-15a are
/// <see cref="Manual"/>.
/// </summary>
public enum ServiceRequestPaymentMode
{
    /// <summary>
    /// The payment happens outside CasaZen and is recorded by hand: the host marks the request paid (<c>mark-paid</c>) or the
    /// supplier records it (<c>payment/offline</c>). No commission: no money goes through CasaZen.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Paid inside CasaZen: direct charge on the supplier's Stripe account with the platform commission
    /// (<see cref="ServiceRequestPayment"/>). The host cannot mark it paid by hand; the only way around is the supplier's
    /// traced exception (<c>payment/offline</c> with a reason).
    /// </summary>
    Online = 1,
}
