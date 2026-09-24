using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>
/// The transitions a service request may take (SU-10, A4-19), one place for the service and the tests:
/// <c>Richiesto → PresoInCarico</c> (supplier takes it), <c>Richiesto → Rifiutato</c> (supplier rejects it),
/// <c>PresoInCarico/InCorso → Completato</c> (supplier completes it), <c>Completato → Pagato</c> (host marks it paid).
/// Everything else is refused with 422 <c>service_request_invalid_transition</c>. No endpoint leads to
/// <see cref="ServiceRequestStatus.InCorso"/> yet (audit A4, section 1.c): it is only accepted as a source of the
/// completion, as before.
/// </summary>
public static class ServiceRequestStateMachine
{
    /// <summary>True when a request in <paramref name="from"/> may move to <paramref name="to"/>.</summary>
    public static bool CanTransition(ServiceRequestStatus from, ServiceRequestStatus to) => (from, to) switch
    {
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.PresoInCarico) => true,
        (ServiceRequestStatus.Richiesto, ServiceRequestStatus.Rifiutato) => true,
        (ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso, ServiceRequestStatus.Completato) => true,
        (ServiceRequestStatus.Completato, ServiceRequestStatus.Pagato) => true,
        _ => false,
    };
}
