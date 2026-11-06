using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Whether a flow is active on a property (NF-106, E10/F01/US01/T04): the hook the consumers of epics E02 and E03 can read before an outbound
/// effect. Active means what the flow status means: an activation stands and every prerequisite still holds, so a flow whose connection was lost
/// is not active although its row stays. Nothing reads it yet: gating the outbound effects is a decision of the product owner and of the lead
/// (risks of epic E10).
/// </summary>
/// <remarks>
/// Works in a request (the tenant filter keeps it to the caller's org) and in a job (each property is evaluated in its own org). A property with
/// no activation standing is never evaluated: it has no active flow.
/// </remarks>
public interface IFlowActivationReader
{
    /// <summary>True when <paramref name="flow"/> is active on <paramref name="propertyId"/>.</summary>
    Task<bool> IsActiveAsync(Guid propertyId, OnboardingFlow flow, CancellationToken ct);

    /// <summary>The active flows of each of <paramref name="propertyIds"/>; a property with none is left out of the answer.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<OnboardingFlow>>> GetActiveFlowsAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct);
}
