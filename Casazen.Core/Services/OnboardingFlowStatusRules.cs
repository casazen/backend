using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// The rules of the flow status (NF-106, ONB-03, E10/F01/US01/T02): for one property and one flow, is the flow active, to complete (and
/// what fixes it) or not available (and why). Pure: no I/O, no clock, no write. The facts are loaded by
/// <see cref="IOnboardingFlowStatusService"/> and the prerequisites by the <see cref="IFlowReadinessProvider"/> of the flow.
/// </summary>
/// <remarks>
/// <para><b>The order</b> is part of the contract:</para>
/// <list type="number">
/// <item><b>Not available</b> when the property is in long-term mode (<see cref="IsShortRent"/> is false), is not active, or the provider says
/// so (nobody answers for the flow, or it names a reason the flow does not apply to the property).</item>
/// <item>The prerequisites of the provider, in its order.</item>
/// <item>All met: <b>active</b> when an activation row without <see cref="PropertyFlowActivation.DeactivatedAt"/> exists; otherwise <b>to
/// complete</b> with <see cref="OnboardingFlowReasons.FlowNotActivated"/> and the action <see cref="OnboardingFlowActions.Activate"/>.</item>
/// <item>Any unmet: <b>to complete</b> with the reason of the first unmet prerequisite and the action that fixes it.</item>
/// </list>
/// <para>A flow that was activated stops being active as soon as a prerequisite stops holding: the activation row stays, the state is
/// derived on every read, and the flow is active again when the prerequisite holds again.</para>
/// </remarks>
public static class OnboardingFlowStatusRules
{
    /// <summary>
    /// True when a property in <paramref name="mode"/> behaves as a short-rent one: the in-memory form of
    /// <see cref="PropertyRentalModeRules.IsShortRent"/> (the facts of a property are read without loading it). A test keeps the two together,
    /// so when <see cref="RentalMode"/> gains a member the definition is revisited in one place.
    /// </summary>
    public static bool IsShortRent(RentalMode mode) => mode == RentalMode.Short;

    /// <summary>
    /// The state of <paramref name="flow"/> on <paramref name="property"/>.
    /// </summary>
    /// <param name="property">The facts of the property.</param>
    /// <param name="flow">The flow.</param>
    /// <param name="activation">
    /// The activation row of the property and flow, if there is one. Only the property and flow of this call are looked at by the caller; a
    /// row with <see cref="PropertyFlowActivation.DeactivatedAt"/> set is a flow the host switched off.
    /// </param>
    /// <param name="readiness">
    /// What the provider of the flow said about the property; <c>null</c> when it said nothing (no provider, or the property was left out of
    /// its answer), which is the same as <see cref="FlowReadiness.NotProvided"/>: a flow is never presumed ready.
    /// </param>
    public static FlowPropertyStatus Evaluate(
        FlowPropertyFacts property,
        OnboardingFlow flow,
        PropertyFlowActivation? activation,
        FlowReadiness? readiness)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (!IsShortRent(property.RentalMode))
            return NotAvailable(property, flow, OnboardingFlowReasons.PropertyLongRent);
        if (!property.IsActive)
            return NotAvailable(property, flow, OnboardingFlowReasons.PropertyInactive);

        readiness ??= FlowReadiness.NotProvided;
        if (!readiness.Provided)
            return NotAvailable(property, flow, readiness.NotAvailableReason ?? OnboardingFlowReasons.NotProvided);
        if (!string.IsNullOrWhiteSpace(readiness.NotAvailableReason))
            return NotAvailable(property, flow, readiness.NotAvailableReason);

        var prerequisites = readiness.Prerequisites;
        var firstUnmet = prerequisites.FirstOrDefault(prerequisite => !prerequisite.Met);
        if (firstUnmet is not null)
        {
            // A provider that forgot the reason of an unmet prerequisite does not turn it into a met one: the reason of its key stands.
            var reason = firstUnmet.Reason ?? OnboardingPrerequisiteKeys.DefaultReasonOf(firstUnmet.Key) ?? OnboardingFlowReasons.NotProvided;
            return Build(property, flow, OnboardingFlowStates.ToComplete, reason, OnboardingFlowActions.ForReason(reason), prerequisites);
        }

        return activation is { DeactivatedAt: null }
            ? Build(property, flow, OnboardingFlowStates.Active, null, null, prerequisites)
            : Build(
                property,
                flow,
                OnboardingFlowStates.ToComplete,
                OnboardingFlowReasons.FlowNotActivated,
                OnboardingFlowActions.Activate,
                prerequisites);
    }

    private static FlowPropertyStatus NotAvailable(FlowPropertyFacts property, OnboardingFlow flow, string reason)
    {
        return Build(property, flow, OnboardingFlowStates.NotAvailable, reason, null, []);
    }

    private static FlowPropertyStatus Build(
        FlowPropertyFacts property,
        OnboardingFlow flow,
        string state,
        string? reason,
        string? action,
        IReadOnlyList<FlowPrerequisite> prerequisites)
    {
        return new FlowPropertyStatus(property.PropertyId, property.Name, property.City, flow, state, reason, action, prerequisites);
    }
}
