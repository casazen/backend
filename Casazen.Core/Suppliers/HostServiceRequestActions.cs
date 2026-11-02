using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The words the API uses for what the host can do about a request (SR-08a, <c>hostAction.kind</c> and
/// <c>availableActions[]</c> of <c>GET api/service-requests</c>). <b>An open set</b>: a client shows nothing for a word it does not
/// know. The camel-case values are the wire format (they are not enum names on purpose).
/// </summary>
public static class HostRequestActionKinds
{
    /// <summary>Nothing asks for the host: the supplier is at work, or the request is over.</summary>
    public const string None = "none";

    /// <summary>A new request nobody answered: the host can remind the supplier (not before <c>remindAvailableAt</c>).</summary>
    public const string Remind = "remind";

    /// <summary>The supplier proposed another time: the host accepts it (or turns it down, <see cref="RejectProposal"/>).</summary>
    public const string AcceptProposal = "acceptProposal";

    /// <summary>A finished job whose final amount is above the quote by more than the tolerance: the host confirms it (decision D7).</summary>
    public const string ConfirmFinalAmount = "confirmFinalAmount";

    /// <summary>A finished job paid inside CasaZen (<see cref="ServiceRequestPaymentMode.Online"/>): the host pays it.</summary>
    public const string Pay = "pay";

    /// <summary>A finished job paid outside CasaZen (<see cref="ServiceRequestPaymentMode.Manual"/>): the host marks it paid.</summary>
    public const string MarkPaid = "markPaid";

    /// <summary>A request that was refused or cancelled: the host asks again (a new request, not an endpoint of this one).</summary>
    public const string RequestAgain = "requestAgain";

    /// <summary>Only in <c>availableActions</c>: the host turns the proposed time down.</summary>
    public const string RejectProposal = "rejectProposal";

    /// <summary>Only in <c>availableActions</c>: the host cancels the request (always free, with a reason).</summary>
    public const string Cancel = "cancel";
}

/// <summary>What a request lets the host do right now, as the API says it (<see cref="HostServiceRequestActions.Of"/>).</summary>
/// <param name="Kind">The one action the row of the request offers: a word of <see cref="HostRequestActionKinds"/>.</param>
/// <param name="RemindAvailableAt">
/// Only with <see cref="HostRequestActionKinds.None"/> on a request that waits for the supplier: the instant the host can remind the
/// supplier again (the last reminder was less than the interval ago). <c>null</c> otherwise.
/// </param>
/// <param name="Available">Every action the endpoints would accept now, the <paramref name="Kind"/> first.</param>
public sealed record HostServiceRequestAction(string Kind, DateTime? RemindAvailableAt, IReadOnlyList<string> Available);

/// <summary>What the rule needs to know about a request. Nothing else of the request counts.</summary>
public readonly record struct HostRequestFacts(
    ServiceRequestStatus Status,
    bool HasProposal,
    bool FinalAmountNeedsConfirmation,
    ServiceRequestPaymentMode PaymentMode,
    DateTime? LastRemindedAt);

/// <summary>
/// What the host can do about a request (SR-08a), worked out here and nowhere else: the page of the interventions shows the action
/// the server names and has no state machine of its own. It is the state machine of the request
/// (<see cref="ServiceRequestStateMachine"/>) and the definition of what asks something of the host
/// (<see cref="HostServiceRequestTodoRules"/>, the same one the Home uses) read for one request:
/// <list type="bullet">
/// <item>a new request with a proposed time: <see cref="HostRequestActionKinds.AcceptProposal"/>;</item>
/// <item>a new request without one: <see cref="HostRequestActionKinds.Remind"/>, or <see cref="HostRequestActionKinds.None"/> with the
/// instant it can be done when the supplier was reminded less than the interval ago;</item>
/// <item>a finished job: <see cref="HostRequestActionKinds.ConfirmFinalAmount"/> while the amount waits for the host, then
/// <see cref="HostRequestActionKinds.Pay"/> (paid inside CasaZen) or <see cref="HostRequestActionKinds.MarkPaid"/> (paid outside);</item>
/// <item>a refused or cancelled request: <see cref="HostRequestActionKinds.RequestAgain"/>;</item>
/// <item>anything else: <see cref="HostRequestActionKinds.None"/>.</item>
/// </list>
/// The quote of the supplier (SP-04b) is another offer to answer: it gets its word when that task is in.
/// </summary>
public static class HostServiceRequestActions
{
    /// <summary>What the host can do about a request in <paramref name="facts"/> at <paramref name="nowUtc"/>.</summary>
    /// <param name="remindIntervalHours">Hours between two reminders (<c>Suppliers:ServiceRequests:RemindIntervalHours</c>).</param>
    public static HostServiceRequestAction Of(in HostRequestFacts facts, DateTime nowUtc, int remindIntervalHours)
    {
        var available = new List<string>(4);
        var kind = HostRequestActionKinds.None;
        DateTime? remindAvailableAt = null;

        switch (HostServiceRequestTodoRules.ActionOf(facts.Status, facts.HasProposal, facts.FinalAmountNeedsConfirmation))
        {
            case HostTodoAction.RespondToServiceRequest:
                kind = HostRequestActionKinds.AcceptProposal;
                available.Add(kind);
                available.Add(HostRequestActionKinds.RejectProposal);
                break;

            case HostTodoAction.ConfirmFinalAmount:
                kind = HostRequestActionKinds.ConfirmFinalAmount;
                available.Add(kind);
                break;

            case HostTodoAction.PayServiceRequest:
                kind = facts.PaymentMode == ServiceRequestPaymentMode.Online ? HostRequestActionKinds.Pay : HostRequestActionKinds.MarkPaid;
                available.Add(kind);
                break;

            default:
                switch (facts.Status)
                {
                    case ServiceRequestStatus.Richiesto:
                        // Without a proposal (that case is above): a reminder, if the last one is old enough.
                        if (facts.LastRemindedAt is { } last && last.AddHours(remindIntervalHours) > nowUtc)
                        {
                            remindAvailableAt = last.AddHours(remindIntervalHours);
                        }
                        else
                        {
                            kind = HostRequestActionKinds.Remind;
                            available.Add(kind);
                        }

                        break;

                    case ServiceRequestStatus.Rifiutato or ServiceRequestStatus.Annullato:
                        kind = HostRequestActionKinds.RequestAgain;
                        available.Add(kind);
                        break;

                    case ServiceRequestStatus.Pagato:
                        // Nothing is asked of the host, but a job done can be asked again.
                        available.Add(HostRequestActionKinds.RequestAgain);
                        break;
                }

                break;
        }

        if (ServiceRequestStateMachine.CanCancel(facts.Status, ServiceRequestActorParty.Host))
            available.Add(HostRequestActionKinds.Cancel);

        return new HostServiceRequestAction(kind, remindAvailableAt, available);
    }
}
