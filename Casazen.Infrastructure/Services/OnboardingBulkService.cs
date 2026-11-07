using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOnboardingBulkService" />
/// <remarks>
/// <para><b>Reach.</b> Every read and every write starts from <see cref="Universe"/>: the org of the scope bound in SQL, then what the caller
/// reaches (<c>InScope</c>, AM-03), then the active short-rent properties. The tenant filter of the context applies underneath. An id that is
/// not in it is reported as <c>property_not_found</c>, the same answer as an id that does not exist.</para>
/// <para><b>Writes.</b> Only the pilot group is written here (one save, the unique index on the property keeps a property in it once). The sender
/// goes through <see cref="IPropertySenderService"/> and the activation through <see cref="IOnboardingFlowActivationService"/>, one property or
/// one chunk at a time, so the rules of those services (confirmation, delegation, audit, the prerequisites verified again, the outbox event)
/// apply untouched. A refusal of those services for a property is a typed, expected failure and becomes that property's outcome; anything else
/// (a database error, a cancelled request) is not caught: the properties done before it keep their change and the call can be repeated, because
/// a property that already has what was asked is <c>unchanged</c>.</para>
/// <para><b>Logs.</b> Ids, counts and codes; never a name or an address.</para>
/// </remarks>
public sealed class OnboardingBulkService(
    AppDbContext db,
    IOnboardingFlowStatusService flowStatus,
    IOnboardingFlowActivationService activation,
    IPropertySenderService senders,
    IPropertySenderStore senderStore,
    IOptions<OnboardingOptions> options,
    TimeProvider timeProvider,
    ILogger<OnboardingBulkService> logger) : IOnboardingBulkService
{
    /// <summary>The most values a list of a filter may hold (a code of a comune, a region or a channel).</summary>
    public const int MaxFilterValues = 500;

    private static readonly OnboardingFlow[] AllFlows = [.. Enum.GetValues<OnboardingFlow>().OrderBy(flow => (int)flow)];

    public async Task<BulkPreview> PreviewAsync(HostScope scope, BulkSelection selection, CancellationToken ct)
    {
        var selected = await ResolveAsync(scope, selection, ct);
        var cap = Cap();
        return new BulkPreview(
            selected.Properties.Count,
            [.. selected.Properties.Take(OnboardingBulkLimits.SampleSize).Select(p => new BulkPropertySample(p.Id, p.Name, p.City))],
            selected.NotFound.Count,
            selected.Properties.Count > cap,
            cap);
    }

    public async Task<BulkApplyResult> ApplyAsync(
        HostScope scope,
        string actorUserId,
        BulkSelection selection,
        BulkChoice choice,
        bool dryRun,
        bool confirmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        // The one choice of this version is the sender; a missing or unknown one never means «the default».
        if (choice?.ComplianceSender is not { } sender || !Enum.IsDefined(sender))
            throw new DomainRuleException(OnboardingBulkCodes.ChoiceInvalid, "OnboardingChoiceInvalid");

        var selected = await ResolveAsync(scope, selection, ct);
        var cap = Cap();
        if (selected.Properties.Count > cap)
            throw new DomainRuleException(OnboardingBulkCodes.SelectionTooLarge, "OnboardingSelectionTooLarge", cap);

        // Without the confirmation nothing is written, whatever else was asked.
        var writeNothing = dryRun || !confirmed;
        var before = selected.Properties.Count == 0
            ? new Dictionary<Guid, PropertySenderInfo>()
            : (await senderStore.GetManyAsync([.. selected.Properties.Select(p => p.Id)], ct)).ToDictionary(pair => pair.Key, pair => pair.Value);

        var items = new List<BulkApplyItem>(selected.Properties.Count + selected.NotFound.Count);
        foreach (var property in selected.Properties)
        {
            before.TryGetValue(property.Id, out var current);
            items.Add(await ApplyOneAsync(property, current?.AlloggiatiSender, sender, actorUserId, writeNothing, ct));
        }

        items.AddRange(selected.NotFound.Select(id => new BulkApplyItem(id, null, null, BulkApplyOutcomes.Refused, OnboardingBulkCodes.PropertyNotFound)));

        var summary = new BulkApplySummary(
            Count(items, BulkApplyOutcomes.Applied),
            Count(items, BulkApplyOutcomes.Unchanged),
            Count(items, BulkApplyOutcomes.Refused),
            Count(items, BulkApplyOutcomes.WouldChange));
        logger.LogInformation(
            "Bulk sender {Sender} for org {OrgId}, nothing written {WriteNothing}: applied {Applied}, unchanged {Unchanged}, refused {Refused}, would change {WouldChange}",
            sender,
            scope.OrgId,
            writeNothing,
            summary.Applied,
            summary.Unchanged,
            summary.Refused,
            summary.WouldChange);
        return new BulkApplyResult(writeNothing, sender, summary, items);
    }

    public async Task<BulkProgress> GetProgressAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var propertyIds = await Universe(scope).OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);
        var statuses = propertyIds.Count == 0
            ? []
            : await flowStatus.GetStatusesAsync(scope, propertyIds, AllFlows, ct);
        var byProperty = statuses.ToLookup(status => status.PropertyId);

        var ready = propertyIds.Count(id => IsReady(byProperty[id]));
        var flows = AllFlows
            .Select(flow =>
            {
                var ofFlow = statuses.Where(status => status.Flow == flow).ToList();
                return new BulkFlowProgress(
                    flow,
                    ofFlow.Count(IsActive),
                    ofFlow.Count(IsReadyToActivate),
                    ofFlow.Count(status => status.State == OnboardingFlowStates.ToComplete && !IsReadyToActivate(status)),
                    ofFlow.Count(status => status.State == OnboardingFlowStates.NotAvailable));
            })
            .ToList();

        var pilot = BuildPilot(await PilotMembersAsync(scope, ct), byProperty);
        return new BulkProgress(propertyIds.Count, ready, propertyIds.Count - ready, flows, pilot);
    }

    public async Task<BulkPilot> GetPilotAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var members = await PilotMembersAsync(scope, ct);
        var statuses = members.Count == 0
            ? []
            : await flowStatus.GetStatusesAsync(scope, [.. members.Select(m => m.PropertyId)], AllFlows, ct);
        return BuildPilot(members, statuses.ToLookup(status => status.PropertyId));
    }

    public async Task<BulkPilot> SetPilotAsync(HostScope scope, string actorUserId, IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (actorUserId.Length > OnboardingPilotMember.UserIdMaxLength)
            throw new ArgumentException("Not a user id.", nameof(actorUserId));

        // An empty list never means «no pilot group»: the pilot is replaced by a group or kept.
        var wanted = (propertyIds ?? []).Distinct().ToList();
        if (wanted.Count == 0)
            throw new DomainRuleException(OnboardingActivationCodes.PropertyIdsEmpty, "OnboardingPropertyIdsEmpty");

        var maxSize = options.Value.EffectivePilotGroupMaxSize;
        if (wanted.Count > maxSize)
            throw new DomainRuleException(OnboardingBulkCodes.PilotGroupTooLarge, "OnboardingPilotGroupTooLarge", maxSize);

        // Every member is a property of the scope the flows apply to; one that is not (another org, deleted, outside the reach, long-term or
        // switched off) is the 404 of an id that does not exist, and nothing is written.
        var found = await Universe(scope).Where(p => wanted.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        if (found.Count != wanted.Count)
            throw OnboardingReach.PropertyNotFound();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ReplacePilotAsync(scope, actorUserId, wanted, ct);
                break;
            }
            catch (DbUpdateException ex) when (attempt == 1 && OrgTeamAccess.IsUniqueViolation(ex, OnboardingPilotMember.PropertyIndexName))
            {
                // A parallel call put the same property in the group first: nothing of this one was saved, and it decides again from what is stored.
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation("Pilot group of org {OrgId} set to {Count} properties", scope.OrgId, wanted.Count);
        return await GetPilotAsync(scope, ct);
    }

    public async Task<BulkActivationResult> ActivateAsync(
        HostScope scope,
        string actorUserId,
        string cohort,
        IReadOnlyCollection<OnboardingFlow> flows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);

        var kind = BulkCohorts.Parse(cohort) ?? throw new DomainRuleException(OnboardingBulkCodes.CohortInvalid, "OnboardingCohortInvalid");
        var wantedFlows = (flows ?? []).Distinct().ToList();
        if (wantedFlows.Count == 0)
            throw new DomainRuleException(OnboardingBulkCodes.FlowsEmpty, "OnboardingFlowsEmpty");
        if (wantedFlows.Any(flow => !Enum.IsDefined(flow)))
            throw new ArgumentOutOfRangeException(nameof(flows), "Not an onboarding flow.");

        var members = await PilotMembersAsync(scope, ct);
        var memberIds = members.Select(m => m.PropertyId).ToList();
        var result = new List<BulkActivationFlow>(wantedFlows.Count);

        if (kind == BulkCohorts.Pilot)
        {
            // The pilot group is never «all»: with none set there is nothing to activate.
            if (memberIds.Count == 0)
                throw new DomainRuleException(OnboardingBulkCodes.PilotGroupEmpty, "OnboardingPilotGroupEmpty");

            foreach (var flow in wantedFlows)
                result.Add(new BulkActivationFlow(flow, await activation.ActivateAsync(scope, actorUserId, flow, memberIds, ct)));
        }
        else
        {
            var universe = await Universe(scope).OrderBy(p => p.Name).ThenBy(p => p.Id).Select(p => p.Id).ToListAsync(ct);
            if (universe.Count > options.Value.EffectivePilotGroupMaxSize)
                await RequirePilotActiveAsync(scope, memberIds, wantedFlows, ct);

            var inPilot = memberIds.ToHashSet();
            var others = universe.Where(id => !inPilot.Contains(id)).ToList();
            foreach (var flow in wantedFlows)
            {
                // The activation service takes at most the cap in one call: a large portfolio goes through it in chunks, in the same order.
                var outcomes = new List<FlowActivationOutcome>(others.Count);
                foreach (var chunk in others.Chunk(Cap()))
                    outcomes.AddRange(await activation.ActivateAsync(scope, actorUserId, flow, chunk, ct));

                result.Add(new BulkActivationFlow(flow, outcomes));
            }
        }

        logger.LogInformation(
            "Bulk activation of org {OrgId}, cohort {Cohort}: {Outcomes}",
            scope.OrgId,
            kind,
            string.Join(
                ", ",
                result.SelectMany(r => r.Outcomes.Select(o => (r.Flow, o.Outcome)))
                    .GroupBy(x => x)
                    .OrderBy(g => g.Key.Flow)
                    .ThenBy(g => g.Key.Outcome, StringComparer.Ordinal)
                    .Select(g => $"{g.Key.Flow} {g.Key.Outcome} {g.Count()}")));
        return new BulkActivationResult(kind, result);
    }

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// The properties a bulk call may touch: the org of the scope, then what the caller reaches, short-rent and active. The one place the
    /// reach of this feature is decided.
    /// </summary>
    private IQueryable<Property> Universe(HostScope scope) =>
        db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId)
            .InScope(scope)
            .Where(PropertyRentalModeRules.IsShortRent)
            .Where(p => p.IsActive);

    private static Task<List<SelectedProperty>> ToListAsync(IQueryable<Property> query, CancellationToken ct) =>
        query
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Select(p => new SelectedProperty(p.Id, p.Name, p.City))
            .ToListAsync(ct);

    private async Task<Selected> ResolveAsync(HostScope scope, BulkSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var kind = BulkSelectionKinds.Parse(selection?.Kind) ?? throw SelectionInvalid();

        // What the kind does not use is not silently ignored: a client that names a filter and says «all» gets an error, not the whole portfolio.
        var hasIds = selection!.PropertyIds is not null;
        var hasFilter = selection.Filter is not null;
        if ((kind == BulkSelectionKinds.All && (hasIds || hasFilter))
            || (kind == BulkSelectionKinds.Ids && hasFilter)
            || (kind == BulkSelectionKinds.Filter && hasIds))
        {
            throw SelectionInvalid();
        }

        var universe = Universe(scope);
        switch (kind)
        {
            case BulkSelectionKinds.All:
                return new Selected(await ToListAsync(universe, ct), []);

            case BulkSelectionKinds.Ids:
                return await ResolveIdsAsync(universe, selection.PropertyIds, ct);

            default:
                return await ResolveFilterAsync(scope, universe, selection.Filter, ct);
        }
    }

    private async Task<Selected> ResolveIdsAsync(IQueryable<Property> universe, IReadOnlyList<Guid>? propertyIds, CancellationToken ct)
    {
        // An empty list never means «all».
        var ids = (propertyIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
            throw new DomainRuleException(OnboardingActivationCodes.PropertyIdsEmpty, "OnboardingPropertyIdsEmpty");

        var cap = Cap();
        if (ids.Count > cap)
            throw new DomainRuleException(OnboardingActivationCodes.PropertyIdsTooMany, "OnboardingPropertyIdsTooMany", cap);

        var found = await ToListAsync(universe.Where(p => ids.Contains(p.Id)), ct);
        var foundIds = found.Select(p => p.Id).ToHashSet();
        return new Selected(found, [.. ids.Where(id => !foundIds.Contains(id))]);
    }

    private async Task<Selected> ResolveFilterAsync(HostScope scope, IQueryable<Property> universe, BulkFilter? filter, CancellationToken ct)
    {
        var criteria = ValidateFilter(filter);
        var orgId = scope.OrgId;

        var query = universe;
        if (criteria.Comunes is { } comunes)
            query = query.Where(p => p.ComuneIstatCode != null && comunes.Contains(p.ComuneIstatCode));
        if (criteria.Regions is { } regions)
            query = query.Where(p => p.RegionCode != null && regions.Contains(p.RegionCode));
        if (criteria.Channels is { } channels)
        {
            query = query.Where(p => db.ListingChannelStates.Any(
                s => s.OrgId == orgId && s.PropertyId == p.Id && channels.Contains(s.ChannelCode)));
        }

        var candidates = await ToListAsync(query, ct);
        if (criteria.MissingFlow is null && criteria.FlowState is null)
            return new Selected(candidates, []);

        // The state of a flow is derived, never stored: the flow status judges the properties that are left.
        var flows = new[] { criteria.MissingFlow, criteria.FlowState?.Flow }.OfType<OnboardingFlow>().Distinct().ToList();
        var statuses = candidates.Count == 0
            ? []
            : await flowStatus.GetStatusesAsync(scope, [.. candidates.Select(c => c.Id)], flows, ct);
        var byKey = statuses.ToDictionary(status => (status.PropertyId, status.Flow));

        bool Matches(SelectedProperty property)
        {
            if (criteria.MissingFlow is { } missing
                && !(byKey.TryGetValue((property.Id, missing), out var status) && status.State == OnboardingFlowStates.ToComplete))
            {
                return false;
            }

            return criteria.FlowState is not { } wanted
                   || (byKey.TryGetValue((property.Id, wanted.Flow), out var found) && found.State == wanted.State);
        }

        return new Selected([.. candidates.Where(Matches)], []);
    }

    /// <summary>
    /// The filter in the form the query uses, or 422: at least one criterion, and a list that is given is not empty (an empty list never means
    /// «everything»), holds at most <see cref="MaxFilterValues"/> values and only well-formed codes.
    /// </summary>
    private static NormalizedFilter ValidateFilter(BulkFilter? filter)
    {
        if (filter is null)
            throw SelectionInvalid();

        IReadOnlyList<string>? comunes = null;
        if (filter.ComuneIstatCodes is { } rawComunes)
        {
            var values = rawComunes.Select(code => code?.Trim()).ToList();
            if (values.Count is 0 or > MaxFilterValues || values.Any(code => !ComuneRules.IsIstatCode(code)))
                throw SelectionInvalid();

            comunes = [.. values.Select(code => code!).Distinct(StringComparer.Ordinal)];
        }

        IReadOnlyList<string>? regions = null;
        if (filter.RegionCodes is { } rawRegions)
        {
            var values = rawRegions.Select(code => ItalianRegions.FindByCode(code)?.Code).ToList();
            if (values.Count is 0 or > MaxFilterValues || values.Any(code => code is null))
                throw SelectionInvalid();

            regions = [.. values.Select(code => code!).Distinct(StringComparer.Ordinal)];
        }

        IReadOnlyList<string>? channels = null;
        if (filter.ChannelCodes is { } rawChannels)
        {
            var values = rawChannels.Select(code => code?.Trim()).ToList();
            if (values.Count is 0 or > MaxFilterValues
                || values.Any(code => string.IsNullOrEmpty(code) || code.Length > ListingChannelState.ChannelCodeMaxLength))
            {
                throw SelectionInvalid();
            }

            channels = [.. values.Select(code => code!).Distinct(StringComparer.Ordinal)];
        }

        if (filter.MissingFlow is { } missing && !Enum.IsDefined(missing))
            throw SelectionInvalid();

        BulkFlowStateFilter? flowState = null;
        if (filter.FlowState is { } rawState)
        {
            if (!Enum.IsDefined(rawState.Flow) || !OnboardingFlowStates.TryParse(rawState.State, out var state))
                throw SelectionInvalid();

            flowState = new BulkFlowStateFilter(rawState.Flow, state);
        }

        // «No criterion» would be everything: that is the kind «all», asked for by name.
        if (comunes is null && regions is null && channels is null && filter.MissingFlow is null && flowState is null)
            throw SelectionInvalid();

        return new NormalizedFilter(comunes, regions, channels, filter.MissingFlow, flowState);
    }

    private static DomainRuleException SelectionInvalid() => new(OnboardingBulkCodes.SelectionInvalid, "OnboardingSelectionInvalid");

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// One property: judged by the sender service without writing anything when <paramref name="writeNothing"/>, else switched through it. A rule
    /// of the service that refuses is the outcome; the sender in effect before decides between <c>applied</c> and <c>unchanged</c>.
    /// </summary>
    private async Task<BulkApplyItem> ApplyOneAsync(
        SelectedProperty property,
        ComplianceSenderType? current,
        ComplianceSenderType sender,
        string actorUserId,
        bool writeNothing,
        CancellationToken ct)
    {
        try
        {
            if (writeNothing)
            {
                var check = await senders.CheckSwitchAsync(property.Id, sender, ct);
                return Item(property, check.WouldChange ? BulkApplyOutcomes.WouldChange : BulkApplyOutcomes.Unchanged, null);
            }

            // The confirmation was asked once for the whole call; the consent to the delegation is never given in bulk.
            await senders.SwitchAsync(property.Id, sender, confirmed: true, delegationConsent: false, actorUserId, ct);
            return Item(property, current == sender ? BulkApplyOutcomes.Unchanged : BulkApplyOutcomes.Applied, null);
        }
        catch (DomainException ex)
        {
            return Item(property, BulkApplyOutcomes.Refused, ex.Code);
        }
        catch (NotFoundException ex)
        {
            return Item(property, BulkApplyOutcomes.Refused, ex.Code ?? OnboardingBulkCodes.PropertyNotFound);
        }
    }

    private static BulkApplyItem Item(SelectedProperty property, string outcome, string? reason) =>
        new(property.Id, property.Name, property.City, outcome, reason);

    private static int Count(IReadOnlyList<BulkApplyItem> items, string outcome) => items.Count(item => item.Outcome == outcome);

    // ---------------------------------------------------------------- progress

    private static bool IsActive(FlowPropertyStatus status) => status.State == OnboardingFlowStates.Active;

    /// <summary>
    /// The flow waits only for the activation: every prerequisite holds (the sole reason it is not active is
    /// <see cref="OnboardingFlowReasons.FlowNotActivated"/>, the rule the activation service applies).
    /// </summary>
    private static bool IsReadyToActivate(FlowPropertyStatus status) =>
        status.State == OnboardingFlowStates.ToComplete && status.Reason == OnboardingFlowReasons.FlowNotActivated;

    /// <summary>
    /// Ready: at least one flow applies to the property, and every flow that does is active or has all its prerequisites met. A property no flow
    /// applies to is not ready: there is nothing to run for it.
    /// </summary>
    private static bool IsReady(IEnumerable<FlowPropertyStatus> statuses)
    {
        var available = statuses.Where(status => status.State != OnboardingFlowStates.NotAvailable).ToList();
        return available.Count > 0 && available.All(status => IsActive(status) || IsReadyToActivate(status));
    }

    // ---------------------------------------------------------------- pilot group

    /// <summary>The members of the pilot group the scope reaches (any state of the property), by name.</summary>
    private async Task<List<BulkPilotMember>> PilotMembersAsync(HostScope scope, CancellationToken ct)
    {
        var reachable = db.Properties.AsNoTracking().Where(p => p.OrgId == scope.OrgId).InScope(scope);
        var rows = await (
                from member in db.OnboardingPilotMembers.AsNoTracking()
                join property in reachable on member.PropertyId equals property.Id
                where member.OrgId == scope.OrgId
                orderby property.Name, property.Id
                select new { property.Id, property.Name, property.City, member.AddedAt })
            .ToListAsync(ct);
        return [.. rows.Select(row => new BulkPilotMember(row.Id, row.Name, row.City, row.AddedAt))];
    }

    private BulkPilot BuildPilot(IReadOnlyList<BulkPilotMember> members, ILookup<Guid, FlowPropertyStatus> statuses)
    {
        var flows = AllFlows
            .Select(flow =>
            {
                var ofMembers = members
                    .Select(member => statuses[member.PropertyId].FirstOrDefault(status => status.Flow == flow))
                    .Where(status => status is not null && status.State != OnboardingFlowStates.NotAvailable)
                    .ToList();
                var active = ofMembers.Count(status => IsActive(status!));
                return new BulkPilotFlow(flow, ofMembers.Count, active, ofMembers.Count > 0 && active == ofMembers.Count);
            })
            .ToList();

        return new BulkPilot(
            members,
            members.Count,
            OnboardingBulkLimits.PilotRecommendedMin,
            options.Value.EffectivePilotGroupMaxSize,
            members.Count < OnboardingBulkLimits.PilotRecommendedMin,
            flows);
    }

    private async Task ReplacePilotAsync(HostScope scope, string actorUserId, IReadOnlyList<Guid> wanted, CancellationToken ct)
    {
        // The members the caller reaches are the ones it replaces; a member it does not reach is neither seen nor touched. The reach is the one of
        // every read here (the org, then InScope); it is not AsNoTracking, which inside this tracked query would make the whole query untracked.
        var reachableIds = db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);
        var existing = await db.OnboardingPilotMembers
            .Where(m => m.OrgId == scope.OrgId && reachableIds.Contains(m.PropertyId))
            .ToListAsync(ct);

        var keep = wanted.ToHashSet();
        db.OnboardingPilotMembers.RemoveRange(existing.Where(m => !keep.Contains(m.PropertyId)));

        var present = existing.Select(m => m.PropertyId).ToHashSet();
        var now = UtcDateTime.TruncateToMicroseconds(timeProvider.GetUtcNow().UtcDateTime);
        foreach (var propertyId in wanted.Where(id => !present.Contains(id)))
        {
            // Added through the DbSet: the Guid of the row is preset, so adding it to a collection would track it as modified.
            db.OnboardingPilotMembers.Add(new OnboardingPilotMember
            {
                OrgId = scope.OrgId,
                PropertyId = propertyId,
                AddedAt = now,
                AddedByUserId = actorUserId,
            });
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The pilot group is set and active in every flow asked, or 409: nothing is written. A flow is active on the pilot when it has at least one
    /// member it is available for and every such member has it active.
    /// </summary>
    private async Task RequirePilotActiveAsync(
        HostScope scope,
        IReadOnlyList<Guid> memberIds,
        IReadOnlyList<OnboardingFlow> flows,
        CancellationToken ct)
    {
        if (memberIds.Count == 0)
            throw new DomainConflictException(OnboardingBulkCodes.PilotGroupNotActive, "OnboardingPilotGroupNotActive");

        var statuses = (await flowStatus.GetStatusesAsync(scope, memberIds, flows, ct)).ToLookup(status => status.Flow);
        foreach (var flow in flows)
        {
            var available = statuses[flow].Where(status => status.State != OnboardingFlowStates.NotAvailable).ToList();
            if (available.Count == 0 || !available.All(IsActive))
                throw new DomainConflictException(OnboardingBulkCodes.PilotGroupNotActive, "OnboardingPilotGroupNotActive");
        }
    }

    private int Cap() => options.Value.EffectiveMaxPropertiesPerCall;

    private sealed record SelectedProperty(Guid Id, string Name, string City);

    private sealed record Selected(IReadOnlyList<SelectedProperty> Properties, IReadOnlyList<Guid> NotFound);

    private sealed record NormalizedFilter(
        IReadOnlyList<string>? Comunes,
        IReadOnlyList<string>? Regions,
        IReadOnlyList<string>? Channels,
        OnboardingFlow? MissingFlow,
        BulkFlowStateFilter? FlowState);
}
