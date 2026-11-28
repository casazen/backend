namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a service request is in its life. The values are stored as integers in <c>ServiceRequests.Status</c> and the API
/// serializes the <b>names</b>: never reorder or renumber, only append (SP-04 appended <see cref="Annullato"/>). The
/// transitions are in <see cref="Casazen.Core.Suppliers.ServiceRequestStateMachine"/>.
/// </summary>
public enum ServiceRequestStatus
{
    Richiesto = 0,

    PresoInCarico = 1,

    InCorso = 2,

    Completato = 3,

    Pagato = 4,

    Rifiutato = 5,

    Annullato = 6,
}

public enum ServiceRequestUrgency
{
    Normal,
    High,
    Emergency,
}
