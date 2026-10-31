using Casazen.Core.Entities;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// An offer on a request on measure, sent by the supplier (SP-10b-b): the lines, the time it proposes and an optional note. The total is what
/// the lines add up to (<see cref="ServiceRequestOfferRules.Normalize"/>).
/// </summary>
/// <param name="Lines">From one to twenty lines, each with how many and the price of one.</param>
/// <param name="StartUtc">When the work would start: judged by the slot planner under the supplier's calendar lock, exactly as the time of a take or a proposal.</param>
/// <param name="EndUtc">When it would end; left out, the start plus the duration of the service the request names (422 <c>service_request_time_invalid</c> when it names none).</param>
public record SendServiceRequestOfferCommand(
    IReadOnlyList<ServiceRequestOfferLine> Lines,
    DateTime StartUtc,
    DateTime? EndUtc = null,
    string? Note = null);

/// <summary>
/// The offer of the supplier on a request on measure (SP-10b-b), implemented by <c>ServiceRequestService</c> with the quote it is (decision
/// SP-D30: one engine for the quote of a host and the offer of a showcase customer). A request that does not exist, belongs to another supplier
/// or is not a request on measure is answered with the same 404, whatever the reason.
/// </summary>
public interface IServiceRequestOfferActions
{
    /// <summary>
    /// The supplier answers a new request on measure with an offer: the request stays <c>Richiesto</c> with the offer waiting for the customer, who
    /// is e-mailed with the time to answer by (<c>Suppliers:Showcase:ProposalResponseMinutes</c>, 24 hours). One offer waits at a time: a new one
    /// replaces the old one. The offer holds no slot; the time is judged again when the customer accepts.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">Not a request on measure of this supplier.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ServiceRequestOfferErrors.Invalid"/>, <see cref="ServiceRequestOfferErrors.CannotOffer"/> (the request is not new), <c>service_request_time_invalid</c>, or the supplier is not active.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <c>supplier_slot_unavailable</c> / <c>supplier_commitment_conflict</c>: the time is not one the supplier can do; <c>service_request_state_changed</c>.
    /// </exception>
    Task<ServiceRequest> SendOfferAsync(
        Guid id,
        Guid supplierOrgId,
        string userId,
        SendServiceRequestOfferCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>The supplier takes its offer back: the request stays new and the conversation goes on (no clock runs on the supplier).</summary>
    /// <exception cref="Exceptions.NotFoundException">Not a request on measure of this supplier.</exception>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ServiceRequestOfferErrors.NoOfferToWithdraw"/>, or the supplier is not active.</exception>
    Task<ServiceRequest> WithdrawOfferAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);
}
