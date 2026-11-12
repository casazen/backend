using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The activation of a flow on properties by an org administrator (NF-104, NF-106, NF-108, ONB-01 part 11, E10/F01/US01/T04): a flow is activated
/// only where the server verifies its prerequisites, on every call, through <see cref="IOnboardingFlowStatusService"/> (T02): the client is
/// never trusted. One outcome for each property asked about, so one call can mix them.
/// </summary>
/// <remarks>
/// <para><b>What writes.</b> Only an <see cref="FlowActivationOutcomes.Activated"/> outcome writes: the <c>PropertyFlowActivation</c> row (an
/// activation after a deactivation reuses the row and clears <c>DeactivatedAt</c>) and, in the same unit of work, the outbox event
/// <see cref="DomainEventType.OnboardingFlowActivated"/> (ADR-022-R1). A deactivation sets <c>DeactivatedAt</c> and records
/// <see cref="DomainEventType.OnboardingFlowDeactivated"/>. Every other outcome writes nothing.</para>
/// <para><b>Reach.</b> The properties are bound in SQL to the org of the scope and to what the scope reaches: a property of another org, a
/// deleted one or one the caller does not reach is <see cref="FlowActivationOutcomes.NotFound"/>, never an error that reveals it.</para>
/// <para><b>The list.</b> 1 to <see cref="OnboardingActivationLimits.MaxPropertiesPerCall"/> distinct properties: an empty list is 422
/// <see cref="OnboardingActivationCodes.PropertyIdsEmpty"/>, never «all».</para>
/// </remarks>
public interface IOnboardingFlowActivationService
{
    /// <summary>Activates <paramref name="flow"/> on each of <paramref name="propertyIds"/> whose prerequisites hold, in the order given (duplicates once).</summary>
    Task<IReadOnlyList<FlowActivationOutcome>> ActivateAsync(
        HostScope scope,
        string actorUserId,
        OnboardingFlow flow,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct);

    /// <summary>Deactivates <paramref name="flow"/> on each of <paramref name="propertyIds"/> where it stands activated, in the order given.</summary>
    Task<IReadOnlyList<FlowActivationOutcome>> DeactivateAsync(
        HostScope scope,
        string actorUserId,
        OnboardingFlow flow,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct);
}

/// <summary>
/// The final test of the activation (NF-104, ONB-05 «Prova», E10/F01/US01/T04): a recently imported booking of a property passes through every flow
/// without any effect, and each flow answers <c>pass</c>, <c>fail</c> or <c>skipped</c> with reason codes.
/// </summary>
/// <remarks>
/// Read-only by construction: every read is <c>AsNoTracking</c>, nothing calls <c>SaveChanges</c>, no outbox event is recorded and nothing calls
/// Octorate; an <see cref="InvalidOperationException"/> is thrown when the unit of work holds changes at the end that it did not hold at the start.
/// </remarks>
public interface IOnboardingFlowTestRunService
{
    /// <summary>
    /// Runs the checks on <paramref name="bookingId"/> or, without one, on the most recent booking of the property that has an Octorate reservation.
    /// 404 <see cref="OnboardingActivationCodes.PropertyNotFound"/> for a property the scope does not reach, 404
    /// <see cref="OnboardingActivationCodes.TestBookingNotFound"/> when the property has no such booking (or the one named is not one).
    /// </summary>
    Task<FlowTestRunResult> RunAsync(HostScope scope, Guid propertyId, Guid? bookingId, CancellationToken ct);
}

/// <summary>Stable outcomes of an activation or a deactivation (cross-repo contract): never rename one.</summary>
public static class FlowActivationOutcomes
{
    /// <summary>Every prerequisite holds: the flow is activated on the property (a row and an event were written).</summary>
    public const string Activated = "activated";

    /// <summary>The flow was already active: nothing was written.</summary>
    public const string AlreadyActive = "alreadyActive";

    /// <summary>A prerequisite does not hold: nothing was written; the unmet prerequisite keys say which.</summary>
    public const string PrerequisitesNotMet = "prerequisitesNotMet";

    /// <summary>The flow does not apply to the property: nothing was written; the reason says why.</summary>
    public const string NotAvailable = "notAvailable";

    /// <summary>No such property for the caller (another org, deleted, outside the reach): nothing was written.</summary>
    public const string NotFound = "notFound";

    /// <summary>The flow was deactivated on the property (the row keeps who activated it; an event was written).</summary>
    public const string Deactivated = "deactivated";

    /// <summary>The flow was not activated on the property: nothing was written.</summary>
    public const string AlreadyInactive = "alreadyInactive";
}

/// <summary>The outcome of an activation or a deactivation for one property. No personal data.</summary>
/// <param name="PropertyId">The property, as asked.</param>
/// <param name="Outcome">One of <see cref="FlowActivationOutcomes"/>.</param>
/// <param name="UnmetPrerequisites">The keys of the prerequisites that do not hold (<c>OnboardingPrerequisiteKeys</c>); empty otherwise.</param>
/// <param name="Reason">The reason of a <c>prerequisitesNotMet</c> or <c>notAvailable</c> outcome (<c>OnboardingFlowReasons</c>); <c>null</c> otherwise.</param>
public sealed record FlowActivationOutcome(Guid PropertyId, string Outcome, IReadOnlyList<string> UnmetPrerequisites, string? Reason);

/// <summary>
/// The payload of <see cref="DomainEventType.OnboardingFlowActivated"/> and <see cref="DomainEventType.OnboardingFlowDeactivated"/>: ids and the
/// name of the flow, nothing personal (ADR-022-R21).
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Flow">The member name of the flow.</param>
/// <param name="ActivationId">The <c>PropertyFlowActivation</c> row.</param>
public sealed record OnboardingFlowEventPayload(Guid PropertyId, string Flow, Guid ActivationId);

/// <summary>The stable codes of the activation, the final test and the "To do" sync. Never rename one.</summary>
public static class OnboardingActivationCodes
{
    /// <summary>422: the list of the properties is empty (it never means «all»).</summary>
    public const string PropertyIdsEmpty = "onboarding_property_ids_empty";

    /// <summary>422: the list of the properties is longer than the cap.</summary>
    public const string PropertyIdsTooMany = "onboarding_property_ids_too_many";

    /// <summary>404: the property of the final test is not one the caller reaches.</summary>
    public const string PropertyNotFound = "onboarding_property_not_found";

    /// <summary>404: the property has no booking imported from Octorate to run the final test on.</summary>
    public const string TestBookingNotFound = "test_booking_not_found";

    /// <summary>The entity type of the "To do" of a flow to complete (the id is <c>OnboardingFlowTodo.EntityIdOf</c>).</summary>
    public const string FlowTodoEntityType = "OnboardingFlow";

    /// <summary>The entity type of the "To do" of a lost connection (the id is the <c>OctorateConnection</c> row).</summary>
    public const string ConnectionTodoEntityType = "OctorateConnection";

    /// <summary>The reason of the "To do" of a flow that has properties to complete.</summary>
    public const string ReasonFlowIncomplete = "flow_incomplete";

    /// <summary>The reason of the "To do" of a connection that is disconnected or in error.</summary>
    public const string ReasonConnectionLost = "connection_lost";
}

/// <summary>The limits of the activation.</summary>
public static class OnboardingActivationLimits
{
    /// <summary>The most properties one call may name, whatever the configuration (<c>Onboarding:MaxPropertiesPerCall</c> lowers it).</summary>
    public const int MaxPropertiesPerCall = 500;
}
