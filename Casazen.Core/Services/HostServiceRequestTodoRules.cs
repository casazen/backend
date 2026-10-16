using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// When a service request (intervention) of the host asks something of the host (SR-03b), for the things to do of the Home. One
/// definition, translatable by EF and usable in memory, from the state machine of the request
/// (<see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>):
/// <list type="bullet">
/// <item><see cref="HostTodoAction.RespondToServiceRequest"/>: a new request (<see cref="ServiceRequestStatus.Richiesto"/>) on which the
/// supplier proposed another time, which the host accepts or turns down.</item>
/// <item><see cref="HostTodoAction.ConfirmFinalAmount"/>: a finished job (<see cref="ServiceRequestStatus.Completato"/>) whose final
/// amount is above the quote by more than the tolerance and waits for the host (<see cref="ServiceRequest.FinalAmountNeedsConfirmation"/>).</item>
/// <item><see cref="HostTodoAction.PayServiceRequest"/>: a finished job with nothing left to confirm, waiting to be paid (online, or marked
/// paid by hand).</item>
/// </list>
/// The quotes of SP-04b are not in the product yet: when they are, a quote to accept is another case of the first.
/// </summary>
public static class HostServiceRequestTodoRules
{
    /// <summary>The service requests that ask something of the host: the ones <see cref="ActionOf"/> gives an action to.</summary>
    public static Expression<Func<ServiceRequest, bool>> NeedTheHost() =>
        r => (r.Status == ServiceRequestStatus.Richiesto && r.ProposedStartUtc != null) || r.Status == ServiceRequestStatus.Completato;

    /// <summary>
    /// What a request asks of the host, if anything. The order of the things to do among the requests (<see cref="Rank"/>) is the one
    /// of the answers: the time to accept first, then the amount to confirm, then the jobs to pay.
    /// </summary>
    public static HostTodoAction? ActionOf(ServiceRequestStatus status, bool hasProposal, bool finalAmountNeedsConfirmation) => status switch
    {
        ServiceRequestStatus.Richiesto when hasProposal => HostTodoAction.RespondToServiceRequest,
        ServiceRequestStatus.Completato when finalAmountNeedsConfirmation => HostTodoAction.ConfirmFinalAmount,
        ServiceRequestStatus.Completato => HostTodoAction.PayServiceRequest,
        _ => null,
    };

    /// <summary>The position of the action of a request among the requests, as an expression: 0 an answer, 1 an amount, 2 a payment.</summary>
    public static Expression<Func<ServiceRequest, int>> Rank() =>
        r => r.Status == ServiceRequestStatus.Richiesto ? 0 : r.FinalAmountNeedsConfirmation ? 1 : 2;

    /// <summary>What is lacking for the action, with its stable code and field (<see cref="ComplianceMissingCodes"/>).</summary>
    public static ComplianceMissing MissingFor(HostTodoAction action) => action switch
    {
        HostTodoAction.RespondToServiceRequest => new ComplianceMissing(ComplianceMissingCodes.ProposalNotAnswered, "response"),
        HostTodoAction.ConfirmFinalAmount => new ComplianceMissing(ComplianceMissingCodes.FinalAmountNotConfirmed, "amount"),
        HostTodoAction.PayServiceRequest => new ComplianceMissing(ComplianceMissingCodes.ServiceNotPaid, "payment"),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not an action about a service request."),
    };

    /// <summary>
    /// When the answer is due, if something fixes it: the work was asked for a time, and the proposal is to be answered before it.
    /// The amount to confirm and the payment have no term of their own.
    /// </summary>
    public static DateTime? DueAt(HostTodoAction action, DateTime? scheduledStart) =>
        action == HostTodoAction.RespondToServiceRequest ? scheduledStart : null;
}
