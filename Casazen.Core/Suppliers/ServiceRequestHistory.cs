using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>The dates a service request keeps of its transitions (<c>ServiceRequest</c> columns).</summary>
/// <param name="UpdatedAt">
/// Last update: for a <see cref="ServiceRequestStatus.Rifiutato"/> request it is the rejection, since the status is final
/// (<see cref="ServiceRequestStateMachine"/>) and nothing updates the request afterwards (same rule as the SU-11 KPIs).
/// </param>
/// <param name="StartedAt">When the supplier started the work (SP-04); <c>null</c> for a request that was never started.</param>
/// <param name="CancelledAt">When the request was cancelled (SP-04).</param>
/// <param name="CancellationReason">The reason of the cancellation: the text of the host or the supplier, or <c>NoResponse</c>.</param>
/// <param name="CancelledBy">Who cancelled it (SP-04).</param>
/// <param name="PaidBy">Who made the request paid (SP-15a); <c>null</c> is the host, who did before SP-15a.</param>
public sealed record ServiceRequestMilestones(
    ServiceRequestStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? TakenAt,
    DateTime? CompletedAt,
    DateTime? PaidAt,
    string? RejectionReason,
    DateTime? StartedAt = null,
    DateTime? CancelledAt = null,
    string? CancellationReason = null,
    ServiceRequestActorParty? CancelledBy = null,
    ServiceRequestActorParty? PaidBy = null);

/// <summary>
/// The history of a service request (SU-08): one step per transition of <see cref="ServiceRequestStateMachine"/>, with
/// its date and the party that made it, rebuilt from the dates the request keeps. Requested (host), taken (supplier,
/// with the member who took it), started (supplier), completed (supplier), paid (the host, or the supplier that recorded a
/// payment received outside CasaZen: <see cref="ServiceRequestMilestones.PaidBy"/>), rejected (supplier, with the
/// reason) or cancelled (the host, the supplier or CasaZen, with the reason).
/// </summary>
public static class ServiceRequestHistory
{
    /// <summary>The steps of the request, in the order of the state machine.</summary>
    /// <param name="takenByName">Name of the supplier member who took the request, when known.</param>
    public static IReadOnlyList<ServiceRequestHistoryEntry> Build(ServiceRequestMilestones request, string? takenByName)
    {
        ArgumentNullException.ThrowIfNull(request);

        var steps = new List<ServiceRequestHistoryEntry>
        {
            new(ServiceRequestStatus.Richiesto, request.CreatedAt, ServiceRequestActorParty.Host, null, null),
        };

        if (request.TakenAt is { } takenAt)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.PresoInCarico,
                takenAt,
                ServiceRequestActorParty.Supplier,
                string.IsNullOrWhiteSpace(takenByName) ? null : takenByName.Trim(),
                null));
        }

        if (request.StartedAt is { } startedAt)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.InCorso, startedAt, ServiceRequestActorParty.Supplier, null, null));
        }

        if (request.CompletedAt is { } completedAt)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.Completato, completedAt, ServiceRequestActorParty.Supplier, null, null));
        }

        if (request.PaidAt is { } paidAt)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.Pagato, paidAt, request.PaidBy ?? ServiceRequestActorParty.Host, null, null));
        }

        if (request.Status == ServiceRequestStatus.Rifiutato)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.Rifiutato,
                request.UpdatedAt,
                ServiceRequestActorParty.Supplier,
                null,
                string.IsNullOrWhiteSpace(request.RejectionReason) ? null : request.RejectionReason));
        }

        if (request.Status == ServiceRequestStatus.Annullato)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.Annullato,
                request.CancelledAt ?? request.UpdatedAt,
                request.CancelledBy ?? ServiceRequestActorParty.Host,
                null,
                string.IsNullOrWhiteSpace(request.CancellationReason) ? null : request.CancellationReason));
        }

        return steps;
    }
}
