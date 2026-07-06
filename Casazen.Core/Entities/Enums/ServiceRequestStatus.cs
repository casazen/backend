namespace Casazen.Core.Entities.Enums;

public enum ServiceRequestStatus
{
    Richiesto,
    PresoInCarico,
    InCorso,
    Completato,
    Pagato,
    Rifiutato,
}

public enum ServiceRequestUrgency
{
    Normal,
    High,
    Emergency,
}
