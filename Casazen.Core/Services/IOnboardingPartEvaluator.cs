using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// How much of one part of the activation plan is done (NF-104, E10/F01/US01/T03), read from the stored facts the epic that owns the part
/// writes: the connection, the mapping, the channel states, the senders... Never from a flag that says «done»: nothing marks a part done.
/// </summary>
/// <remarks>
/// <para><b>One evaluator wins.</b> Several evaluators may answer for a part (a placeholder and the real one); the one with the highest
/// <see cref="Order"/> is used. A part nobody has built yet has the placeholder of <see cref="OnboardingPartEvaluatorOrders.Placeholder"/>, «not
/// available, not provided», until the epic that owns it registers its own evaluator with a higher order. Today every one of the twelve parts has its
/// evaluator (messages and AI, fiscal documents and partners are in <c>OnboardingCrossEpicProviders</c>, E10/F01/US04/T01) and nothing registers a
/// placeholder.</para>
/// <para><b>Reads only.</b> An evaluator never writes, never calls Octorate and never throws for something that is not done (that is an
/// answer). It counts only the properties of <see cref="OnboardingPartContext.PropertyIds"/>, which the plan built from the caller's reach.</para>
/// </remarks>
public interface IOnboardingPartEvaluator
{
    OnboardingPartKey Part { get; }

    int Order { get; }

    Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct);
}

public sealed record OnboardingPartContext(HostScope Scope, IReadOnlyList<Guid> PropertyIds)
{
    public Guid OrgId => Scope.OrgId;
}

public static class OnboardingPartEvaluatorOrders
{
    public const int Placeholder = 0;

    public const int Evaluator = 10;
}
