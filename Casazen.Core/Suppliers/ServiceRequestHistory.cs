using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>The dates a service request keeps of its transitions (<c>ServiceRequest</c> columns).</summary>
/// <param name="UpdatedAt">
/// Last update: for a <see cref="ServiceRequestStatus.Rifiutato"/> request it is the rejection, since the status is final
/// (<see cref="ServiceRequestStateMachine"/>) and nothing updates the request afterwards (same rule as the SU-11 KPIs).
/// </param>
public sealed record ServiceRequestMilestones(
    ServiceRequestStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? TakenAt,
    DateTime? CompletedAt,
    DateTime? PaidAt,
    string? RejectionReason);

/// <summary>
/// The history of a service request (SU-08): one step per transition of <see cref="ServiceRequestStateMachine"/>, with
/// its date and the party that made it, rebuilt from the dates the request keeps. Requested (host), taken (supplier,
/// with the member who took it), completed (supplier), paid (host) or rejected (supplier, with the reason).
/// </summary>
/// <remarks>
/// No endpoint leads to <see cref="ServiceRequestStatus.InCorso"/> (it has no date): a request in that status shows its
/// take as the last step.
/// </remarks>
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

        if (request.CompletedAt is { } completedAt)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.Completato, completedAt, ServiceRequestActorParty.Supplier, null, null));
        }

        if (request.PaidAt is { } paidAt)
        {
            steps.Add(new ServiceRequestHistoryEntry(
                ServiceRequestStatus.Pagato, paidAt, ServiceRequestActorParty.Host, null, null));
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

        return steps;
    }
}
