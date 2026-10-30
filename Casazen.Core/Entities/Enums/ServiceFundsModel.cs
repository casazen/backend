namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Whose money a service payment is while it is in flight (<see cref="ServiceRequestPayment.FundsModel"/>, SP-15c-a). Stored as an
/// integer: append only, never renumber.
/// </summary>
public enum ServiceFundsModel
{
    /// <summary>
    /// The model of SP-15a: a <b>direct charge</b> on the supplier's own Stripe Express account with the commission as
    /// <c>application_fee_amount</c>; CasaZen holds no funds. Every payment created before SP-15c is one; the host's payments stay so
    /// until SP-15c-b moves them to <see cref="PlatformHeld"/>.
    /// </summary>
    DirectCharge = 0,

    /// <summary>
    /// The model of decision VT-D1: the PaymentIntent lives on the <b>platform</b> account (no <c>Stripe-Account</c>, no
    /// <c>application_fee_amount</c>), the card is blocked (<c>capture_method = manual</c>) and charged when the supplier accepts, CasaZen
    /// holds the money, and releases it to the supplier with a <c>Transfer</c> (<c>source_transaction</c>) minus its commission, or gives
    /// it back to the payer. The cycle is <c>Authorized → Held → Released</c> (or a refund).
    /// </summary>
    PlatformHeld = 1,
}
