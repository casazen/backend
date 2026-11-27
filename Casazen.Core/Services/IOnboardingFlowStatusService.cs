using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// The status of the six flows (listings, bookings, check-in and compliance, messages, documents, reports) on the properties a caller
/// reaches (NF-106, ONB-03, E10/F01/US01/T02): for each property and flow, active, to complete (with the reason and the action that fixes
/// it) or not available (with why).
/// </summary>
/// <remarks>
/// <para><b>Read only.</b> Nothing here writes: the state is computed on every read from the stored facts (the connection, the mapping, the
/// senders, the channel states, the activation rows), as the activation checklist of PL-15 does. There is no stored «status» to go stale.</para>
/// <para><b>Reach.</b> The properties are bound in SQL to the org of the scope and to what the caller reaches
/// (<c>InScope</c>, AM-03): a collaborator «Solo alcuni» is counted and listed for the properties it was given, and nobody ever sees a
/// property of another org. The scope is built by the web layer from the org membership, never from client input.</para>
/// <para><b>Which properties.</b> Every property of the scope that is not deleted, whatever its rental mode or state: a long-term property
/// or a switched-off one is <c>notAvailable</c> (<see cref="OnboardingFlowReasons.PropertyLongRent"/>,
/// <see cref="OnboardingFlowReasons.PropertyInactive"/>), not left out, so the counts add up to the properties the caller reaches.</para>
/// </remarks>
public interface IOnboardingFlowStatusService
{
    /// <summary>
    /// The six flows in the order of <see cref="OnboardingFlow"/>, each with how many properties of the scope are active, to complete and not
    /// available. Always six entries, zeros included.
    /// </summary>
    Task<IReadOnlyList<FlowSummary>> GetSummariesAsync(HostScope scope, CancellationToken ct);

    /// <summary>
    /// A page of the properties of the scope with their status for <paramref name="flow"/>, by name (the id as the tie-break, so a property is
    /// never on two pages nor on none).
    /// </summary>
    /// <param name="scope">What the caller reaches (resolved by the web layer from the org membership).</param>
    /// <param name="flow">The flow.</param>
    /// <param name="state">One of <see cref="OnboardingFlowStates"/> to keep only the properties in that state; <c>null</c> for all of them.</param>
    /// <param name="search">A text to find in the name or the city of the property (case-insensitive); blank for all of them.</param>
    /// <param name="page">The page wanted (1-based), brought within <see cref="FlowPropertyPage.MaxPage"/>.</param>
    /// <param name="pageSize">The size wanted, brought within 1 and <see cref="FlowPropertyPage.MaxPageSize"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not null and not one of <see cref="OnboardingFlowStates"/>.</exception>
    Task<FlowPropertyPage> GetPropertiesAsync(
        HostScope scope,
        OnboardingFlow flow,
        string? state,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>
    /// The status of each of <paramref name="flows"/> on the properties of <paramref name="scope"/> (E10/F01/US01/T03, T04): every property the
    /// scope reaches when <paramref name="propertyIds"/> is <c>null</c>, otherwise only the ones among them the scope reaches (an id of another
    /// org, a deleted property or one outside the reach is left out of the answer, never an error that reveals it). Property by property in the
    /// order of the list (by name, the id as the tie-break), each with its flows in the order given. The activation, the plan and the "To do"
    /// sync re-evaluate the prerequisites through it: the client is never trusted.
    /// </summary>
    Task<IReadOnlyList<FlowPropertyStatus>> GetStatusesAsync(
        HostScope scope,
        IReadOnlyCollection<Guid>? propertyIds,
        IReadOnlyCollection<OnboardingFlow> flows,
        CancellationToken ct);
}

/// <summary>A page of the properties with their status for one flow (NF-106).</summary>
/// <param name="Items">The properties of the page, by name (the id as the tie-break).</param>
/// <param name="TotalCount">All the properties that match the state and the search, on every page.</param>
/// <param name="Page">The page taken (1-based) after bringing the request within the limits.</param>
/// <param name="PageSize">The size taken after bringing the request within the limits.</param>
public sealed record FlowPropertyPage(IReadOnlyList<FlowPropertyStatus> Items, int TotalCount, int Page, int PageSize)
{
    /// <summary>The page size when the caller does not say how big.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The most properties a page may hold.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The highest page number taken: the order is fixed, a number past the end is just an empty page.</summary>
    public const int MaxPage = 100_000;
}
