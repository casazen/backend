using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The <b>phase</b> of a service request as the supplier console talks about it (<c>phase</c> of the items of
/// <c>GET api/supplier/inbox</c> and <c>today</c> since SP-04e, and of the calendar once SP-03b adopts it), derived <b>once, here</b>,
/// so the frontend never repeats the rules. It is a <b>derived</b> value, never stored: <see cref="ServiceRequestStatus"/> keeps its
/// seven values (the mobile app does not know new ones).
/// </summary>
/// <remarks>
/// <para><b>The set is open.</b> The values of today are the constants below; other areas will add more (the quote of SP-04b,
/// the "su misura" trattative of the showcase, the held payments of SP-15c). A client must show a generic wording for a value it
/// does not know, never fail. <see cref="Of"/> is total: any status it does not know gives <see cref="Unknown"/> (a request
/// forwarded to an external provider, spec #466, would be a new status), it never throws.</para>
/// <para><b>Correspondence</b> with the status: <c>Richiesto</c> is <see cref="ToAnswer"/> (the supplier still has to answer),
/// <see cref="ProposalPending"/> (the supplier proposed another time and the customer has not answered) or
/// <see cref="QuotePending"/> (the supplier sent a quote and the customer has not answered it, SP-04b); <c>PresoInCarico</c> is <see cref="Scheduled"/>,
/// <c>InCorso</c> <see cref="InProgress"/>, <c>Completato</c> <see cref="Completed"/>, <c>Pagato</c> <see cref="Paid"/>,
/// <c>Rifiutato</c> <see cref="Rejected"/> and <c>Annullato</c> <see cref="Cancelled"/>.</para>
/// </remarks>
public static class SupplierRequestPhases
{
    /// <summary>New: the supplier has to answer (take it, refuse it, propose another time).</summary>
    public const string ToAnswer = "to_answer";

    /// <summary>The supplier proposed another time and waits for the customer.</summary>
    public const string ProposalPending = "proposal_pending";

    /// <summary>The supplier sent a quote (SP-04b) and waits for the customer.</summary>
    public const string QuotePending = "quote_pending";

    /// <summary>Taken, the work is yet to start.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The work started.</summary>
    public const string InProgress = "in_progress";

    /// <summary>The work is done and not paid yet.</summary>
    public const string Completed = "completed";

    /// <summary>Paid.</summary>
    public const string Paid = "paid";

    /// <summary>The supplier refused it.</summary>
    public const string Rejected = "rejected";

    /// <summary>Cancelled by the host, the customer, the supplier or CasaZen.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A status this code does not know: the client shows a generic wording.</summary>
    public const string Unknown = "unknown";

    /// <summary>The phase of a request in <paramref name="status"/>.</summary>
    /// <param name="status">Where the request is in its life.</param>
    /// <param name="hasPendingProposal">The supplier proposed another time that nobody answered yet.</param>
    /// <param name="hasPendingQuote">The supplier sent a quote that nobody answered yet (SP-04b).</param>
    public static string Of(ServiceRequestStatus status, bool hasPendingProposal, bool hasPendingQuote = false) => status switch
    {
        ServiceRequestStatus.Richiesto => hasPendingProposal ? ProposalPending : hasPendingQuote ? QuotePending : ToAnswer,
        ServiceRequestStatus.PresoInCarico => Scheduled,
        ServiceRequestStatus.InCorso => InProgress,
        ServiceRequestStatus.Completato => Completed,
        ServiceRequestStatus.Pagato => Paid,
        ServiceRequestStatus.Rifiutato => Rejected,
        ServiceRequestStatus.Annullato => Cancelled,
        _ => Unknown,
    };
}
