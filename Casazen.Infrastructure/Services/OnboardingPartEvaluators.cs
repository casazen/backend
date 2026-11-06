using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The part «Octorate connection» (E10/F01/US01/T03, NF-104): the durable inventory intent of E02 (where the listings of each property are today)
/// and, for the listings that already exist on OTAs, the import batch of D6. One thing to do for each property counted by the plan and, for a
/// caller who reaches the whole org, for each group of the portfolio that has no property yet.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>onOctorate</c> is done when the org's connection of this environment is connected.</item>
/// <item><c>onOtas</c> is done when every channel the person chose in the batches that cover the property (or its group) has its access given and its
/// rooms imported, so the mapping can start (the import reached <see cref="OctorateInventoryImportStatus.AwaitingMapping"/>): the part
/// «Properties» owns the completion of the mapping. It needs the connection, which it imports through.</item>
/// <item><c>newInventory</c> is done once the person declared it: the listing wizard can create the listing, and the creation itself brings the
/// connection of a new host.</item>
/// <item>A property with no declaration is never taken for new inventory (<see cref="OnboardingPartReasons.InventoryScenarioMissing"/>), and a missing
/// connection never turns <c>onOtas</c> into <c>newInventory</c>. A connection that exists and no longer works stops every scenario
/// (<see cref="OnboardingPartReasons.ConnectionLost"/>).</item>
/// </list>
/// </remarks>
public sealed class OctorateConnectionPartEvaluator(AppDbContext db, FlowPrerequisiteReader prerequisites) : IOnboardingPartEvaluator
{
    /// <summary>The reasons of the things not done, the most important first: the part shows the first one any of them has.</summary>
    private static readonly string[] ReasonPrecedence =
    [
        OnboardingPartReasons.ConnectionLost,
        OnboardingPartReasons.InventoryScenarioMissing,
        OnboardingPartReasons.ConnectionMissing,
        OnboardingPartReasons.ExistingOtaImportIncomplete,
    ];

    public OnboardingPartKey Part => OnboardingPartKey.OctorateConnection;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var orgId = context.OrgId;
        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var connected = connection.Met;
        var lost = !connected && connection.Reason == OnboardingFlowReasons.ConnectionLost;

        // The intents of the org (one for each property, plus the groups that have no property yet): few rows, filtered here by the reach.
        var intents = await db.OctorateInventoryIntents
            .AsNoTracking()
            .Where(i => i.OrgId == orgId)
            .Select(i => new IntentRow(i.PropertyId, i.Scenario, i.WizardBatch))
            .ToListAsync(ct);
        var byProperty = intents.Where(i => i.PropertyId is not null).ToDictionary(i => i.PropertyId!.Value);

        var items = new List<IntentRow?>(context.PropertyIds.Count);
        items.AddRange(context.PropertyIds.Select(id => byProperty.TryGetValue(id, out var intent) ? intent : null));

        // A group with no property is an org-level decision: only a caller who reaches the whole org counts it.
        if (context.Scope.IsOrgWide)
            items.AddRange(intents.Where(i => i.PropertyId is null));

        var imports = items.Any(i => i?.Scenario == OctorateInventoryScenario.OnOtas)
            ? await ImportsAsync(orgId, ct)
            : ImportFacts.None;

        var done = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var reason = ReasonOf(item, connected, lost, imports);
            if (reason is null)
                done++;
            else
                reasons.Add(reason);
        }

        if (items.Count == 0)
            reasons.Add(lost ? OnboardingPartReasons.ConnectionLost : OnboardingPartReasons.InventoryScenarioMissing);

        return PartFacts.Counted(done, items.Count, ReasonPrecedence.FirstOrDefault(reasons.Contains));
    }

    /// <summary>Why one assignment is not done; <c>null</c> when it is.</summary>
    private static string? ReasonOf(IntentRow? intent, bool connected, bool lost, ImportFacts imports)
    {
        if (intent is null)
            return OnboardingPartReasons.InventoryScenarioMissing;
        if (lost)
            return OnboardingPartReasons.ConnectionLost;

        return intent.Scenario switch
        {
            OctorateInventoryScenario.OnOctorate => connected ? null : OnboardingPartReasons.ConnectionMissing,
            OctorateInventoryScenario.OnOtas when !connected => OnboardingPartReasons.ConnectionMissing,
            OctorateInventoryScenario.OnOtas => imports.ReachedMapping(intent) ? null : OnboardingPartReasons.ExistingOtaImportIncomplete,
            OctorateInventoryScenario.NewInventory => null,
            _ => OnboardingPartReasons.InventoryScenarioMissing,
        };
    }

    private async Task<ImportFacts> ImportsAsync(Guid orgId, CancellationToken ct)
    {
        var batches = await db.OctorateInventoryImportBatches
            .AsNoTracking()
            .Where(b => b.OrgId == orgId)
            .Select(b => new { b.Id, b.ScopeKind, b.ScopeGroup, b.ScopePropertyIdsJson })
            .ToListAsync(ct);
        var channels = (await db.OctorateInventoryImportChannels
                .AsNoTracking()
                .Where(c => c.OrgId == orgId)
                .Select(c => new { c.BatchId, Reached = c.AccessCompletedAt != null && c.RoomsImportedAt != null })
                .ToListAsync(ct))
            .ToLookup(c => c.BatchId, c => c.Reached);

        return new ImportFacts(
        [
            .. batches.Select(b => new BatchFacts(
                b.ScopeKind,
                b.ScopeGroup,
                PropertyIdsOf(b.ScopePropertyIdsJson),
                // A batch with no channel has nothing imported: it never counts as reached.
                channels[b.Id].Any() && channels[b.Id].All(reached => reached))),
        ]);
    }

    private static IReadOnlySet<Guid> PropertyIdsOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<Guid>();

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json)?.ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            // A scope that cannot be read covers no property: the import is never presumed reached.
            return new HashSet<Guid>();
        }
    }

    private sealed record IntentRow(Guid? PropertyId, OctorateInventoryScenario Scenario, string? WizardBatch);

    private sealed record BatchFacts(OctorateInventoryScopeKind Kind, string? Group, IReadOnlySet<Guid> PropertyIds, bool Reached);

    private sealed record ImportFacts(IReadOnlyList<BatchFacts> Batches)
    {
        public static ImportFacts None { get; } = new([]);

        /// <summary>True when at least one batch covers the assignment and every batch that covers it reached the mapping.</summary>
        public bool ReachedMapping(IntentRow intent)
        {
            var covering = Batches.Where(batch => Covers(batch, intent)).ToList();
            return covering.Count > 0 && covering.All(batch => batch.Reached);
        }

        private static bool Covers(BatchFacts batch, IntentRow intent) => batch.Kind switch
        {
            OctorateInventoryScopeKind.AllInventory => true,
            OctorateInventoryScopeKind.Properties => intent.PropertyId is { } id && batch.PropertyIds.Contains(id),
            OctorateInventoryScopeKind.Group => intent.WizardBatch is not null && string.Equals(batch.Group, intent.WizardBatch, StringComparison.Ordinal),
            _ => false,
        };
    }
}

/// <summary>
/// The part «Properties» (E10/F01/US01/T03, NF-104): the properties counted by the plan whose listing is confirmed on the channel manager, read
/// through <see cref="IPropertyMappingReader"/> (T02), never from the mapping tables. Not available when nothing provides the mapping.
/// </summary>
public sealed class PropertiesPartEvaluator(IPropertyMappingReader mappingReader, FlowPrerequisiteReader prerequisites) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.Properties;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!mappingReader.IsProvided)
            return PartFacts.NotAvailable(OnboardingPartReasons.MappingUnavailable);
        if (context.PropertyIds.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var mapping = await prerequisites.MappingAsync(context.OrgId, context.PropertyIds, ct);
        var unmet = context.PropertyIds.Select(id => mapping[id]).Where(prerequisite => !prerequisite.Met).ToList();
        return PartFacts.Counted(
            context.PropertyIds.Count - unmet.Count,
            context.PropertyIds.Count,
            unmet.Select(prerequisite => prerequisite.Reason).FirstOrDefault() ?? OnboardingPartReasons.MappingUnconfirmed);
    }
}

/// <summary>The part «Channels» (E10/F01/US01/T03, NF-104): the properties counted by the plan that are on at least one channel (a <see cref="ListingChannelState"/> row).</summary>
public sealed class ChannelsPartEvaluator(AppDbContext db) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.Channels;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ids = context.PropertyIds.ToList();
        if (ids.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var onAChannel = await db.ListingChannelStates
            .AsNoTracking()
            .Where(s => s.OrgId == context.OrgId && ids.Contains(s.PropertyId))
            .Select(s => s.PropertyId)
            .Distinct()
            .CountAsync(ct);
        return PartFacts.Counted(onAChannel, ids.Count, OnboardingPartReasons.ChannelsMissing);
    }
}

/// <summary>
/// The part «Listings» (E10/F01/US01/T03, NF-104): the properties counted by the plan that are on at least one channel and that no channel refused
/// (no row in <see cref="ChannelPublishStatus.ToFix"/>).
/// </summary>
public sealed class ListingsPartEvaluator(AppDbContext db) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.Listings;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ids = context.PropertyIds.ToList();
        if (ids.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var states = (await db.ListingChannelStates
                .AsNoTracking()
                .Where(s => s.OrgId == context.OrgId && ids.Contains(s.PropertyId))
                .Select(s => new { s.PropertyId, s.Status })
                .ToListAsync(ct))
            .ToLookup(s => s.PropertyId, s => s.Status);

        var done = ids.Count(id => states[id].Any() && !states[id].Contains(ChannelPublishStatus.ToFix));
        var reason = ids.Any(id => states[id].Contains(ChannelPublishStatus.ToFix))
            ? OnboardingPartReasons.ChannelContentToFix
            : OnboardingPartReasons.ChannelsMissing;
        return PartFacts.Counted(done, ids.Count, reason);
    }
}

/// <summary>
/// The part «Prices and rules» (E10/F01/US01/T03, NF-104): the properties counted by the plan that have a base nightly price, the base of the one
/// pricing rule of the stays (<see cref="StayPricing"/>). ADR-028 as amended has no <c>RatePlan</c> table: the price lives on the property.
/// </summary>
public sealed class PricesRulesPartEvaluator(AppDbContext db) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.PricesRules;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ids = context.PropertyIds.ToList();
        if (ids.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var priced = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == context.OrgId && ids.Contains(p.Id) && p.NightlyRate > 0)
            .CountAsync(ct);
        return PartFacts.Counted(priced, ids.Count, OnboardingPartReasons.PriceMissing);
    }
}

/// <summary>
/// The part «Compliance» (E10/F01/US01/T03, NF-104): the properties counted by the plan with a stored single sender of the Alloggiati and ISTAT
/// communications, a valid CIN (<c>CinFormat</c>) and a tourist tax rate in force for their comune (<c>ITouristTaxQuoteService</c>). The three
/// are read through the provider of the check-in and compliance flow (T02), so the rules exist once; a property outside Italy has none of them and
/// is not counted. The reason is the first missing among <see cref="OnboardingPartReasons.SenderMissing"/>,
/// <see cref="OnboardingPartReasons.CinMissingOrInvalid"/> and <see cref="OnboardingPartReasons.TaxRateMissing"/>.
/// </summary>
public sealed class CompliancePartEvaluator(IEnumerable<IFlowReadinessProvider> providers) : IOnboardingPartEvaluator
{
    /// <summary>The prerequisites of the flow the part is made of, in the order their reasons are shown.</summary>
    private static readonly string[] Keys =
    [
        OnboardingPrerequisiteKeys.SingleSender,
        OnboardingPrerequisiteKeys.Cin,
        OnboardingPrerequisiteKeys.TaxRate,
    ];

    public OnboardingPartKey Part => OnboardingPartKey.Compliance;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var provider = providers
            .Where(p => p.Flow == OnboardingFlow.CheckInCompliance)
            .OrderByDescending(p => p.Order)
            .FirstOrDefault();
        if (provider is null || provider.Order <= FlowProviderOrders.Placeholder)
            return PartFacts.NotAvailable(OnboardingPartReasons.NotProvided);
        if (context.PropertyIds.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var readiness = await provider.EvaluateAsync(context.OrgId, context.PropertyIds, ct);
        int counted = 0, done = 0, firstMissing = int.MaxValue;
        foreach (var id in context.PropertyIds)
        {
            // Left out of the answer, outside Italy or not applicable: no compliance to complete there.
            if (!readiness.TryGetValue(id, out var property) || !property.Provided || property.NotAvailableReason is not null)
                continue;

            counted++;
            var missing = Array.FindIndex(Keys, key => property.Prerequisites.FirstOrDefault(p => p.Key == key) is not { Met: true });
            if (missing < 0)
                done++;
            else
                firstMissing = Math.Min(firstMissing, missing);
        }

        var reason = firstMissing == int.MaxValue ? null : OnboardingPrerequisiteKeys.DefaultReasonOf(Keys[firstMissing]);
        return PartFacts.Counted(done, counted, reason);
    }
}

/// <summary>
/// The part «Identification» (E10/F01/US01/T03, NF-104): provided only while the identification of the guests is on
/// (<see cref="FeatureFlags.CheckInIdentityVerification"/>); done for the properties counted by the plan that have an access code.
/// </summary>
public sealed class IdentificationPartEvaluator(AppDbContext db, IFeatureFlags featureFlags) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.Identification;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!featureFlags.IsEnabled(FeatureFlags.CheckInIdentityVerification))
            return PartFacts.NotAvailable(OnboardingPartReasons.IdentificationOff);

        var ids = context.PropertyIds.ToList();
        if (ids.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var withCode = await db.PropertyAccessCodes
            .AsNoTracking()
            .Where(c => c.OrgId == context.OrgId && ids.Contains(c.PropertyId))
            .Select(c => c.PropertyId)
            .Distinct()
            .CountAsync(ct);
        return PartFacts.Counted(withCode, ids.Count, OnboardingPartReasons.AccessCodeMissing);
    }
}

/// <summary>
/// The part «Activation» (E10/F01/US01/T03, NF-104): the properties counted by the plan on which every flow that applies is active, read through the
/// flow status of T02 (a flow is active only while its activation stands and every prerequisite still holds).
/// </summary>
public sealed class ActivationPartEvaluator(IOnboardingFlowStatusService flowStatus) : IOnboardingPartEvaluator
{
    private static readonly OnboardingFlow[] AllFlows = [.. Enum.GetValues<OnboardingFlow>().OrderBy(flow => (int)flow)];

    public OnboardingPartKey Part => OnboardingPartKey.Activation;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.PropertyIds.Count == 0)
            return PartFacts.Counted(0, 0, null);

        var statuses = await flowStatus.GetStatusesAsync(context.Scope, context.PropertyIds, AllFlows, ct);
        int counted = 0, done = 0;
        foreach (var property in statuses.GroupBy(status => status.PropertyId))
        {
            var applicable = property.Where(status => status.State != OnboardingFlowStates.NotAvailable).ToList();
            if (applicable.Count == 0)
                continue;

            counted++;
            if (applicable.All(status => status.State == OnboardingFlowStates.Active))
                done++;
        }

        return PartFacts.Counted(done, counted, OnboardingPartReasons.FlowsIncomplete);
    }
}

/// <summary>
/// The placeholder of a part nobody has built yet (messages and AI, fiscal documents, partners until E05, E06 and E09 register their own
/// evaluator with a higher <see cref="IOnboardingPartEvaluator.Order"/>): not available, <see cref="OnboardingPartReasons.NotProvided"/>. A part
/// with no evaluator is never presumed done.
/// </summary>
public sealed class NotProvidedPartEvaluator(OnboardingPartKey part) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part { get; } = part;

    public int Order => OnboardingPartEvaluatorOrders.Placeholder;

    public Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct) =>
        Task.FromResult(PartFacts.NotAvailable(OnboardingPartReasons.NotProvided));
}
