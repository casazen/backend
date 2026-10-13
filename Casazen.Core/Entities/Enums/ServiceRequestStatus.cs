namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a service request is in its life. The values are stored as integers in <c>ServiceRequests.Status</c> and the API
/// serializes the <b>names</b>: never reorder or renumber, only append (SP-04 appended <see cref="Annullato"/>). The
/// transitions are in <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>.
/// </summary>
public enum ServiceRequestStatus
{
    /// <summary>Sent by the host, waiting for the supplier's answer.</summary>
    Richiesto = 0,

    /// <summary>The supplier accepted it (the request is a job now).</summary>
    PresoInCarico = 1,

    /// <summary>The supplier started the work (<c>POST api/service-requests/{id}/start</c>, SP-04).</summary>
    InCorso = 2,

    /// <summary>The supplier finished the work.</summary>
    Completato = 3,

    /// <summary>The host marked a completed request as paid (manual flag).</summary>
    Pagato = 4,

    /// <summary>The supplier refused the request before taking it.</summary>
    Rifiutato = 5,

    /// <summary>
    /// The request was cancelled before the work was done: by the host, by the supplier before the start, or automatically
    /// when the supplier did not answer in time (SP-04, decisions D8 and D31). A client that predates this status has no
    /// label for it: the mobile app is to be checked before the status is shown to its users.
    /// </summary>
    Annullato = 6,
}

public enum ServiceRequestUrgency
{
    Normal,
    High,
    Emergency,
}
