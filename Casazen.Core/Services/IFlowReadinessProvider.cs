using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// What a flow needs on a property, and whether it holds (NF-106, E10/F01/US01/T02): the port through which the epics that own a flow tell the
/// activation how ready it is. The flow status service asks the provider of each flow for the properties of a page and applies
/// <see cref="OnboardingFlowStatusRules"/> to the answer. A provider only reads: it never writes, never calls Octorate and never throws for
/// a prerequisite that does not hold (that is an answer).
/// </summary>
/// <remarks>
/// <para><b>One provider wins.</b> More than one provider may answer for a flow (a placeholder and the real one); the one with the highest
/// <see cref="Order"/> is used, so an epic that builds a flow registers its provider with an <see cref="Order"/> above 0 and the placeholder
/// (<see cref="Order"/> 0, <see cref="FlowReadiness.NotProvided"/>) stops being asked.</para>
/// <para><b>Tenant.</b> The properties asked about all belong to <c>orgId</c>; a provider reads through the caller's context, so a row of
/// another org is never in the answer, and it filters by <c>orgId</c> explicitly where the context has no tenant (a job).</para>
/// </remarks>
public interface IFlowReadinessProvider
{
    OnboardingFlow Flow { get; }

    int Order { get; }

    Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct);
}

public static class FlowProviderOrders
{
    public const int Placeholder = 0;

    public const int Provider = 10;
}
