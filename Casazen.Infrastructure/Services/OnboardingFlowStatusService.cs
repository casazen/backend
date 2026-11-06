using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingFlowStatusService" />
/// <remarks>
/// <para><b>Read only.</b> Every read is <c>AsNoTracking</c> and nothing calls <c>SaveChanges</c>: the state is computed on every call from
/// the stored facts by <see cref="OnboardingFlowStatusRules"/>.</para>
/// <para><b>Reach.</b> The properties are bound in SQL to the org of the scope first, then to what the caller reaches
/// (<c>InScope</c>, AM-03); the tenant query filter of the context applies underneath. A property of another org is never in a query here,
/// and the activation rows and every provider are asked only about the ids that came out of it.</para>
/// <para><b>Batches.</b> A page of properties (or, for the summary and the filtered lists, the whole scope) is evaluated in batches of
/// <see cref="EvaluationBatchSize"/> ids: the providers see a bounded list, and the activation rows are one query for each batch.
/// Only the short-rent, active properties are given to the providers: a long-term or switched-off property is not available whatever they
/// would say, so nothing is asked about it.</para>
/// <para><b>The list.</b> Without a state filter a page is cut in SQL (name, then id) and only its properties are evaluated. A state is
/// derived, not stored, so with a state filter every property of the scope is evaluated (in batches) and the page is cut from those in
/// that state: a page is never half empty.</para>
/// </remarks>
public sealed class OnboardingFlowStatusService(
    AppDbContext db,
    IEnumerable<IFlowReadinessProvider> providers) : IOnboardingFlowStatusService
{
    /// <summary>The most property ids a provider or the activation query is given at once.</summary>
    public const int EvaluationBatchSize = 200;

    /// <summary>The longest search text taken: a longer one is cut.</summary>
    public const int SearchMaxLength = 100;

    private static readonly OnboardingFlow[] AllFlows = [.. Enum.GetValues<OnboardingFlow>().OrderBy(flow => (int)flow)];

    public async Task<IReadOnlyList<FlowSummary>> GetSummariesAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var counts = AllFlows.ToDictionary(flow => flow, _ => new int[OnboardingFlowStates.All.Count]);
        var properties = await ReachableFacts(scope, search: null).ToListAsync(ct);
        foreach (var batch in properties.Chunk(EvaluationBatchSize))
        {
            foreach (var status in await EvaluateAsync(scope.OrgId, batch, AllFlows, ct))
            {
                var index = IndexOf(status.State);
                counts[status.Flow][index]++;
            }
        }

        return
        [
            .. AllFlows.Select(flow =>
            {
                var perState = counts[flow];
                return new FlowSummary(
                    flow,
                    perState[IndexOf(OnboardingFlowStates.Active)],
                    perState[IndexOf(OnboardingFlowStates.ToComplete)],
                    perState[IndexOf(OnboardingFlowStates.NotAvailable)],
                    perState.Sum());
            }),
        ];
    }

    public async Task<FlowPropertyPage> GetPropertiesAsync(
        HostScope scope,
        OnboardingFlow flow,
        string? state,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!Enum.IsDefined(flow))
            throw new ArgumentOutOfRangeException(nameof(flow), flow, "Not an onboarding flow.");

        string? wantedState = null;
        if (state is not null)
        {
            wantedState = OnboardingFlowStates.TryParse(state, out var parsed)
                ? parsed
                : throw new ArgumentException("Not a state of a flow.", nameof(state));
        }

        page = Math.Clamp(page, 1, FlowPropertyPage.MaxPage);
        pageSize = Math.Clamp(pageSize, 1, FlowPropertyPage.MaxPageSize);
        var skipped = (page - 1) * pageSize;
        var query = ReachableFacts(scope, search);

        if (wantedState is null)
        {
            // The page is cut in SQL, then only its properties are evaluated.
            var total = await query.CountAsync(ct);
            var rows = await query.Skip(skipped).Take(pageSize).ToListAsync(ct);
            var items = rows.Count == 0 ? [] : await EvaluateAsync(scope.OrgId, rows, [flow], ct);
            return new FlowPropertyPage(items, total, page, pageSize);
        }

        // The state is derived: evaluate the whole scope (in batches) and cut the page from the ones in the wanted state.
        var matching = new List<FlowPropertyStatus>();
        foreach (var batch in (await query.ToListAsync(ct)).Chunk(EvaluationBatchSize))
        {
            matching.AddRange((await EvaluateAsync(scope.OrgId, batch, [flow], ct))
                .Where(status => status.State == wantedState));
        }

        return new FlowPropertyPage([.. matching.Skip(skipped).Take(pageSize)], matching.Count, page, pageSize);
    }

    /// <summary>
    /// The properties of <paramref name="scope"/>, narrowed in SQL (AM-03): the org of the caller first, then what the caller reaches, by
    /// name (the id as the tie-break). The soft-delete and tenant filters of the context apply underneath. The projection names what the
    /// rules read; no photo, description or code is selected.
    /// </summary>
    private IQueryable<FlowPropertyFacts> ReachableFacts(HostScope scope, string? search)
    {
        var properties = db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope);

        var term = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > SearchMaxLength)
                term = term[..SearchMaxLength];
            properties = properties.Where(p => p.Name.ToLower().Contains(term) || p.City.ToLower().Contains(term));
        }

        return properties
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Select(p => new FlowPropertyFacts(p.Id, p.Name, p.City, p.RentalMode, p.IsActive));
    }

    /// <summary>
    /// The status of each of <paramref name="flows"/> on each of <paramref name="properties"/> (at most a batch), property by property in the
    /// order given. The activation rows are one query; each provider is asked once, about the short-rent, active properties.
    /// </summary>
    private async Task<IReadOnlyList<FlowPropertyStatus>> EvaluateAsync(
        Guid orgId,
        IReadOnlyList<FlowPropertyFacts> properties,
        IReadOnlyList<OnboardingFlow> flows,
        CancellationToken ct)
    {
        var ids = properties.Select(p => p.PropertyId).ToList();
        var activations = (await db.PropertyFlowActivations
                .AsNoTracking()
                .Where(a => a.OrgId == orgId && ids.Contains(a.PropertyId))
                .ToListAsync(ct))
            .ToDictionary(a => (a.PropertyId, a.Flow));
        var eligible = properties
            .Where(p => OnboardingFlowStatusRules.IsShortRent(p.RentalMode) && p.IsActive)
            .Select(p => p.PropertyId)
            .ToList();

        var statuses = new List<FlowPropertyStatus>(properties.Count * flows.Count);
        foreach (var flow in flows)
        {
            var readiness = eligible.Count == 0
                ? new Dictionary<Guid, FlowReadiness>()
                : await ReadinessAsync(orgId, flow, eligible, ct);
            foreach (var property in properties)
            {
                activations.TryGetValue((property.PropertyId, flow), out var activation);
                readiness.TryGetValue(property.PropertyId, out var propertyReadiness);
                statuses.Add(OnboardingFlowStatusRules.Evaluate(property, flow, activation, propertyReadiness));
            }
        }

        return statuses;
    }

    /// <summary>What the provider of the flow says about the properties: the one with the highest order; none means «not provided».</summary>
    private async Task<IReadOnlyDictionary<Guid, FlowReadiness>> ReadinessAsync(
        Guid orgId,
        OnboardingFlow flow,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        var provider = providers
            .Where(p => p.Flow == flow)
            .OrderByDescending(p => p.Order)
            .FirstOrDefault();
        return provider is null
            ? new Dictionary<Guid, FlowReadiness>()
            : await provider.EvaluateAsync(orgId, propertyIds, ct);
    }

    private static int IndexOf(string state)
    {
        for (var index = 0; index < OnboardingFlowStates.All.Count; index++)
        {
            if (OnboardingFlowStates.All[index] == state)
                return index;
        }

        throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state of a flow.");
    }
}
