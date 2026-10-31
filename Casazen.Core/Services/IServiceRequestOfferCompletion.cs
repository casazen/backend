namespace Casazen.Core.Services;

/// <summary>
/// The second half of the acceptance of an offer that is paid inside CasaZen (SP-10b-b): the customer accepted, its card is blocked, so the request
/// is taken — the time of the offer judged again under the supplier's calendar lock, the card charged before the request changes state, then
/// <c>Richiesto → PresoInCarico</c> at the time and the total of the offer. Implemented by <c>ServiceRequestService</c>; called only by the handler of
/// the Stripe webhook, once the event that says the card is blocked is committed (the customer's own call is its next acceptance).
/// </summary>
/// <remarks>
/// <b>Nothing here trusts a caller:</b> everything is read again under the lock — the offer that stands, the supplier, the payment — and a block that is
/// not the one of the offer that stands (the supplier replaced it) is released, never charged. Idempotent: a request already taken, a payment already
/// charged for it, an offer already gone are left as they are.
/// </remarks>
public interface IServiceRequestOfferCompletion
{
    /// <summary>
    /// Takes the request <paramref name="requestId"/> on its offer because the card is blocked. Never throws: what cannot be done is logged, and the
    /// customer is told (by e-mail) when the time of the offer is gone or the card could not be charged — in both cases nothing stays charged.
    /// </summary>
    Task CompleteOfferAcceptanceAsync(Guid requestId, CancellationToken cancellationToken = default);
}
