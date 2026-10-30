using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The conversation of a request on measure (SP-10b), as the supplier has it: it reads the thread and writes in it. The customer's side of
/// the same conversation is the next part. Implemented by <c>ServiceRequestService</c>: the request is loaded, judged and saved by the same
/// code (the active-supplier checks, the <c>xmin</c> check) as every other action on a request.
/// </summary>
/// <remarks>
/// A request that does not exist, belongs to another supplier or is not a request on measure is answered with the same 404, whatever the
/// reason. The words are never in a log: ids and counts only.
/// </remarks>
public interface IServiceRequestThreadActions
{
    /// <summary>
    /// The thread of the request, with the messages of the customer marked as read by the supplier. Reading is allowed to a supplier that
    /// is no longer active (it can read what it was told), writing is not.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">Not a request on measure of this supplier.</exception>
    Task<ServiceRequestThread> GetThreadAsSupplierAsync(Guid id, Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message of the supplier to the thread. The first message of the supplier on a new request stops the clock of the hour it has
    /// to answer (<c>ResponseDueAt</c> goes): the conversation is going on, and nobody closes it behind the customer's back. The customer is
    /// told by e-mail (a link, never the words), at most once every few minutes.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">Not a request on measure of this supplier.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ServiceRequestThreadErrors.MessageInvalid"/>, <see cref="ServiceRequestThreadErrors.Full"/>, or the supplier is not active.
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException">
    /// <see cref="ServiceRequestThreadErrors.Closed"/> (refused, cancelled or done), or the request changed meanwhile.
    /// </exception>
    Task<ServiceRequestMessageView> PostMessageAsSupplierAsync(
        Guid id,
        Guid supplierOrgId,
        string? body,
        CancellationToken cancellationToken = default);
}
