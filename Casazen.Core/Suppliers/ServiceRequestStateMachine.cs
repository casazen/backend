using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The transitions a service request may take (SU-10, A4-19; SP-04), one place for the service and the tests:
/// <c>Richiesto → PresoInCarico</c> (supplier takes it), <c>Richiesto → Rifiutato</c> (supplier rejects it),
/// <c>PresoInCarico → InCorso</c> (supplier starts the work), <c>PresoInCarico/InCorso → Completato</c> (supplier completes
/// it), <c>Completato → Pagato</c> (host marks it paid), and <c>Richiesto/PresoInCarico/InCorso → Annullato</c> (cancelled,
/// by whom is <see cref="CanCancel"/>). Everything else is refused with 422 <c>service_request_invalid_transition</c>.
/// <c>Rifiutato</c>, <c>Pagato</c> and <c>Annullato</c> are final.
/// </summary>
public static class ServiceRequestStateMachine
{
    /// <summary>True when a request in <paramref name="from"/> may move to <paramref name="to"/>, whoever asks.</summary>
    public static bool CanTransition(ServiceRequestStatus from, ServiceRequestStatus to) => (from, to) switch
    {
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico) => true,
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.Rifiutato) => true,
        (ServiceRequestStatus.PresoInCarico, ServiceRequestStatus.InCorso) => true,
        (ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso, ServiceRequestStatus.Completato) => true,
        (ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato) => true,
        (ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso, ServiceRequestStatus.Annullato) => true,
        _ => false,
    };

    /// <summary>
    /// True when <paramref name="actor"/> may cancel a request in <paramref name="from"/>: the host up to and including the
    /// work in progress, the supplier only before it started (<c>Richiesto</c>, <c>PresoInCarico</c>), the customer of the public
    /// showcase the same as the supplier (SP-11: never once the work is in progress or over), and CasaZen itself only a request
    /// nobody answered (<c>Richiesto</c>, decision D8). Rejecting is another transition: it is the supplier's answer to a
    /// <c>Richiesto</c> request, with its own reason and its own status.
    /// </summary>
    public static bool CanCancel(ServiceRequestStatus from, ServiceRequestActorParty actor) =>
        CanTransition(from, ServiceRequestStatus.Annullato)
        && actor switch
        {
            ServiceRequestActorParty.Host => true,
            ServiceRequestActorParty.Supplier or ServiceRequestActorParty.Customer => from != ServiceRequestStatus.InCorso,
            ServiceRequestActorParty.System => from == ServiceRequestStatus.Richiesto,
            _ => false,
        };

    /// <summary>True for a status nothing leaves: <c>Rifiutato</c>, <c>Pagato</c> and <c>Annullato</c>.</summary>
    public static bool IsFinal(ServiceRequestStatus status) =>
        status is ServiceRequestStatus.Rifiutato or ServiceRequestStatus.Pagato or ServiceRequestStatus.Annullato;
}
