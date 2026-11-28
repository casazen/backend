using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Core.Suppliers;

public static class HostRequestActionKinds
{
    public const string None = "none";

    public const string Remind = "remind";

    public const string AcceptProposal = "acceptProposal";

    public const string ConfirmFinalAmount = "confirmFinalAmount";

    public const string Pay = "pay";

    public const string MarkPaid = "markPaid";

    public const string RequestAgain = "requestAgain";

    public const string RejectProposal = "rejectProposal";

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
