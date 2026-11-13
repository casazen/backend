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
/// available, not provided», until the epic that owns it registers its own evaluator with a higher order. Today every one of the eleven parts has its
/// evaluator (messages and AI, fiscal documents and partners are in <c>OnboardingCrossEpicProviders</c>, E10/F01/US04/T01) and nothing registers a
/// placeholder.</para>
/// <para><b>Reads only.</b> An evaluator never writes, never calls Octorate and never throws for something that is not done (that is an
/// answer). It counts only the properties of <see cref="OnboardingPartContext.PropertyIds"/>, which the plan built from the caller's reach.</para>
/// </remarks>
public interface IOnboardingPartEvaluator
{
    /// <summary>The part this evaluator answers for.</summary>
    OnboardingPartKey Part { get; }

    /// <summary>The weight of this evaluator among the ones of the same <see cref="Part"/>: the highest wins.</summary>
    int Order { get; }

    /// <summary>How much of the part is done for the caller of <paramref name="context"/>.</summary>
    Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct);
}

/// <summary>What the plan gives each evaluator: the caller's reach and the properties of it the parts count.</summary>
/// <param name="Scope">What the caller reaches (resolved by the web layer, or the whole org for a job).</param>
/// <param name="PropertyIds">
/// The short-rent, active, not deleted properties of the org the scope reaches, in a stable order: the properties every part counts.
/// </param>
public sealed record OnboardingPartContext(HostScope Scope, IReadOnlyList<Guid> PropertyIds)
{
    /// <summary>The org of the plan.</summary>
    public Guid OrgId => Scope.OrgId;
}

/// <summary>The <see cref="IOnboardingPartEvaluator.Order"/> values: the highest of the evaluators of a part is the one asked.</summary>
public static class OnboardingPartEvaluatorOrders
{
    /// <summary>The placeholder that answers «not available, not provided» for a part nobody has built yet.</summary>
    public const int Placeholder = 0;

    /// <summary>An evaluator that really answers for its part: it replaces the placeholder.</summary>
    public const int Evaluator = 10;
}
