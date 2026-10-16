using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The <b>phase</b> of a service request as the supplier's console names it (SP-03b, SP-04e): derived from the status of the
/// request and from what is waiting on it, in <b>one place</b>, so the calendar, the inbox and every other read of the console
/// say the same thing and the frontend never repeats the rules. A phase is never stored and never a new value of
/// <see cref="ServiceRequestStatus"/> (it travels by name to the mobile app, which does not know new ones).
/// </summary>
/// <remarks>
/// <para><b>An open set.</b> Other areas add phases (a negotiation of the public showcase, a payment held by CasaZen): a client
/// shows a value it does not know with a neutral label. A status this code does not know yet (an enum that grows, for instance
/// a request handed to an external provider) is <see cref="Unknown"/>, never an exception.</para>
/// <list type="table">
/// <listheader><term>Status</term><description>Phase</description></listheader>
/// <item><term><c>Richiesto</c></term><description><see cref="ToAnswer"/>; <see cref="ProposalPending"/> when the supplier proposed another time and waits for the answer; <see cref="QuotePending"/> when a quote waits for the customer</description></item>
/// <item><term><c>PresoInCarico</c></term><description><see cref="Scheduled"/></description></item>
/// <item><term><c>InCorso</c></term><description><see cref="InProgress"/></description></item>
/// <item><term><c>Completato</c></term><description><see cref="Completed"/></description></item>
/// <item><term><c>Pagato</c></term><description><see cref="Paid"/></description></item>
/// <item><term><c>Rifiutato</c></term><description><see cref="Rejected"/></description></item>
/// <item><term><c>Annullato</c></term><description><see cref="Cancelled"/></description></item>
/// </list>
/// </remarks>
public static class SupplierRequestPhases
{
    /// <summary>A new request the supplier has not answered.</summary>
    public const string ToAnswer = "to_answer";

    /// <summary>The supplier proposed another time and the host or the customer has not answered.</summary>
    public const string ProposalPending = "proposal_pending";

    /// <summary>A quote of the supplier waits for the customer (not produced until the quote exists, SP-04b).</summary>
    public const string QuotePending = "quote_pending";

    /// <summary>Taken by the supplier, the work is still to do.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The supplier started the work.</summary>
    public const string InProgress = "in_progress";

    /// <summary>The work is done.</summary>
    public const string Completed = "completed";

    /// <summary>The work is done and paid.</summary>
    public const string Paid = "paid";

    /// <summary>The supplier turned the request down.</summary>
    public const string Rejected = "rejected";

    /// <summary>The request was cancelled.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A status this version does not know: never an error.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The phase of a request in <paramref name="status"/>. <paramref name="hasPendingProposal"/> is true while a time the supplier
    /// proposed has no answer; <paramref name="hasPendingQuote"/> while a quote has none. Both only matter for a request still
    /// <c>Richiesto</c>, and a proposal wins over a quote (the time is what the customer is asked first).
    /// </summary>
    public static string Of(ServiceRequestStatus status, bool hasPendingProposal, bool hasPendingQuote = false) => status switch
    {
        ServiceRequestStatus.Richiesto when hasPendingProposal => ProposalPending,
        ServiceRequestStatus.Richiesto when hasPendingQuote => QuotePending,
        ServiceRequestStatus.Richiesto => ToAnswer,
        ServiceRequestStatus.PresoInCarico => Scheduled,
        ServiceRequestStatus.InCorso => InProgress,
        ServiceRequestStatus.Completato => Completed,
        ServiceRequestStatus.Pagato => Paid,
        ServiceRequestStatus.Rifiutato => Rejected,
        ServiceRequestStatus.Annullato => Cancelled,
        _ => Unknown,
    };
}
