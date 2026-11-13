using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The messages flow (E10/F01/US04/T01, NF-106, E05): the conversations of the guests with the channels, copied from the channel manager. Needs the
/// connection, the confirmed mapping and a working message sync of the listing of the property (<see cref="OtaMessageSyncState"/>). Everything is read
/// from the stored facts and nothing is written: the flow is never active from a flag.
/// </summary>
/// <remarks>
/// <para>A listing whose license has no <c>chat</c> module is kept by the sync as the state <see cref="OtaMessageSyncErrorCodes.ChatNotLicensed"/>: the
/// flow is then «not available» with <see cref="OnboardingFlowReasons.ChatNotLicensed"/>, never an empty inbox. It stays so until a later pass reads the
/// listing and clears the code (the sync probes it again every hour).</para>
/// <para>The sync works for the property when the last pass of its listing did not fail and a pass has read it (the initial import is complete, or the
/// recent pass ran): until then <see cref="OnboardingPrerequisiteKeys.MessageSync"/> is not met (<see cref="OnboardingFlowReasons.MessageSyncNotReady"/>),
/// a thing the host cannot hurry, so it has no action.</para>
/// <para><b>Reach.</b> Only the properties asked about are read (the flow status service gives the ones the caller reaches, AM-03), every query names
/// the org, and an empty list is an empty answer, never «every property of the org».</para>
/// </remarks>
public sealed class MessagesFlowProvider(AppDbContext db, FlowPrerequisiteReader prerequisites) : IFlowReadinessProvider
{
    public OnboardingFlow Flow => OnboardingFlow.Messages;

    public int Order => FlowProviderOrders.Provider;

    public async Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var readiness = new Dictionary<Guid, FlowReadiness>(ids.Count);
        if (ids.Count == 0)
            return readiness;

        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var mapping = await prerequisites.MappingAsync(orgId, ids, ct);

        // A row has a property only while its listing is confirmed. A property has one confirmed listing, so one row; the newest wins if a stale one is left.
        var states = (await db.OtaMessageSyncStates
                .AsNoTracking()
                .Where(s => s.OrgId == orgId && s.PropertyId != null && ids.Contains(s.PropertyId.Value))
                .Select(s => new SyncFacts(s.PropertyId!.Value, s.LastError, s.InitialImportCompletedAt, s.LastRecentPassAt, s.UpdatedAt))
                .ToListAsync(ct))
            .ToLookup(s => s.PropertyId);

        foreach (var id in ids)
        {
            var state = states[id].OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
            if (state?.LastError == OtaMessageSyncErrorCodes.ChatNotLicensed)
            {
                readiness[id] = FlowReadiness.NotAvailable(OnboardingFlowReasons.ChatNotLicensed);
                continue;
            }

            var worked = state is { LastError: null } && (state.InitialImportCompletedAt is not null || state.LastRecentPassAt is not null);
            var sync = worked
                ? FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.MessageSync)
                : FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.MessageSync, OnboardingFlowReasons.MessageSyncNotReady);
            readiness[id] = FlowReadiness.Needing(connection, mapping[id], sync);
        }

        return readiness;
    }

    private sealed record SyncFacts(Guid PropertyId, string? LastError, DateTime? InitialImportCompletedAt, DateTime? LastRecentPassAt, DateTime UpdatedAt);
}

/// <summary>
/// The documents flow (E10/F01/US04/T01, NF-106, E06): the fiscal documents of the stays. Needs the organization's choice of who issues them
/// (<see cref="OrgFiscalIssuerProfile"/>: a row means the host chose, the safe default is nobody) and, when the channel manager is the issuer, the
/// connection and the confirmed mapping too.
/// </summary>
/// <remarks>
/// <para>No profile is <see cref="OnboardingFlowReasons.IssuerMissing"/> with the action <see cref="OnboardingFlowActions.ChooseIssuer"/>; the choice
/// «none, my accountant issues» (<see cref="DocumentIssuerMode.Accountant"/>) is «not available» with
/// <see cref="OnboardingFlowReasons.AccountantHandles"/>: CasaZen has no document to run. Octorate as the issuer needs the connection and the mapping
/// like the other flows; CasaZen as the issuer (the direct bookings) needs neither. A mode this version does not know is taken as no choice.</para>
/// <para><b>Reach.</b> The choice is the organization's: it is the same for every property asked about. Only those are answered, an empty list is an
/// empty answer, and the row is read by org.</para>
/// </remarks>
public sealed class DocumentsFlowProvider(AppDbContext db, FlowPrerequisiteReader prerequisites) : IFlowReadinessProvider
{
    public OnboardingFlow Flow => OnboardingFlow.Documents;

    public int Order => FlowProviderOrders.Provider;

    public async Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var readiness = new Dictionary<Guid, FlowReadiness>(ids.Count);
        if (ids.Count == 0)
            return readiness;

        var mode = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => (DocumentIssuerMode?)p.IssuerMode)
            .FirstOrDefaultAsync(ct);

        if (mode == DocumentIssuerMode.Accountant)
        {
            foreach (var id in ids)
                readiness[id] = FlowReadiness.NotAvailable(OnboardingFlowReasons.AccountantHandles);
            return readiness;
        }

        if (mode is not (DocumentIssuerMode.Octorate or DocumentIssuerMode.CasaZenDirectOnly))
        {
            // No profile, or a mode this version does not know: nobody chose who issues, and the host is led to the wizard that asks it.
            var missing = FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Issuer, OnboardingFlowReasons.IssuerMissing);
            foreach (var id in ids)
                readiness[id] = FlowReadiness.Needing(missing);
            return readiness;
        }

        var issuer = FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Issuer);
        if (mode == DocumentIssuerMode.CasaZenDirectOnly)
        {
            foreach (var id in ids)
                readiness[id] = FlowReadiness.Needing(issuer);
            return readiness;
        }

        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var mapping = await prerequisites.MappingAsync(orgId, ids, ct);
        foreach (var id in ids)
            readiness[id] = FlowReadiness.Needing(issuer, connection, mapping[id]);

        return readiness;
    }
}

/// <summary>
/// The reports flow (E10/F01/US04/T01, NF-106, E07): the reports and the figures made of the bookings of the property. Needs the connection, the
/// confirmed mapping and at least one booking imported from the channel manager (<see cref="Booking.OctorateReservationId"/> set) that is still a
/// booking: not a request or a hold, not cancelled and not a no-show (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> and no no-show mark, the
/// one definition of an active booking). Without one the property is «to complete» with <see cref="OnboardingFlowReasons.NoBookingsYet"/>, never
/// active.
/// </summary>
/// <remarks>
/// <para><b>Reach.</b> Only the properties asked about are read (the flow status service gives the ones the caller reaches, AM-03), the bookings of
/// other properties of the org never count for them, and an empty list is an empty answer, never «every property of the org». No guest, name or amount
/// is read: only which properties have a booking.</para>
/// </remarks>
public sealed class ReportsFlowProvider(AppDbContext db, FlowPrerequisiteReader prerequisites) : IFlowReadinessProvider
{
    public OnboardingFlow Flow => OnboardingFlow.Reports;

    public int Order => FlowProviderOrders.Provider;

    public async Task<IReadOnlyDictionary<Guid, FlowReadiness>> EvaluateAsync(
        Guid orgId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var ids = propertyIds.Distinct().ToList();
        var readiness = new Dictionary<Guid, FlowReadiness>(ids.Count);
        if (ids.Count == 0)
            return readiness;

        var connection = await prerequisites.ConnectionAsync(orgId, ct);
        var mapping = await prerequisites.MappingAsync(orgId, ids, ct);
        var withBookings = (await db.Bookings
                .AsNoTracking()
                .Where(b => b.OrgId == orgId
                            && ids.Contains(b.PropertyId)
                            && !string.IsNullOrWhiteSpace(b.OctorateReservationId)
                            && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                            && b.NoShowAt == null)
                .Select(b => b.PropertyId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var id in ids)
        {
            var bookings = withBookings.Contains(id)
                ? FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Bookings)
                : FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Bookings, OnboardingFlowReasons.NoBookingsYet);
            readiness[id] = FlowReadiness.Needing(connection, mapping[id], bookings);
        }

        return readiness;
    }
}

/// <summary>
/// The part «Messages and AI» (E10/F01/US04/T01, NF-104, E05): up to two checks, each read from what the host stored, never from a flag that says
/// «done». The notification preferences were saved by an administrator of the organization (its owner or an Admin: a row of
/// <see cref="UserNotificationChannelSetting"/>, which only a save writes, or quiet hours that differ from the defaults, because the alert router
/// also creates a row with the defaults by itself and that proves nothing), and, only while <see cref="FeatureFlags.MessageAssistant"/> is on, at
/// least one assistant situation is configured (<see cref="AssistantSituationSetting"/>). <c>Done</c> counts the checks that hold, <c>Total</c> is 1 or 2.
/// </summary>
/// <remarks>
/// <para><b>Reach.</b> The preferences are the administrators' of the org, whoever asks. An assistant situation of the organization counts for every
/// caller (the list of the organization is readable with <c>booking.read</c>); the exception of a property counts only for a caller who reaches that
/// property (<see cref="OnboardingPartContext.PropertyIds"/>, AM-03): an org with no property, or none reached, counts the rows of the organization
/// only, never «every row».</para>
/// <para>Nothing is written, no preference, template or text is read: only whether a row exists.</para>
/// </remarks>
public sealed class MessagesAiPartEvaluator(AppDbContext db, IFeatureFlags featureFlags) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.MessagesAi;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var checks = new List<(bool Done, string Reason)>(2)
        {
            (await NotificationPreferencesSavedAsync(context.OrgId, ct), OnboardingPartReasons.NotificationPreferencesMissing),
        };
        if (featureFlags.IsEnabled(FeatureFlags.MessageAssistant))
            checks.Add((await AssistantSituationConfiguredAsync(context, ct), OnboardingPartReasons.AssistantSituationMissing));

        return PartFacts.Counted(checks.Count(check => check.Done), checks.Count, checks.FirstOrDefault(check => !check.Done).Reason);
    }

    private async Task<bool> NotificationPreferencesSavedAsync(Guid orgId, CancellationToken ct)
    {
        // The administrators of the org, as the team of the org defines them: the owner and the Admins, active members (never a deactivated one). The
        // read names the org and ignores the filter of the request's tenant, as the other readers of the team do, because a job runs with none. Their
        // ids are read first (a handful of people), so the two reads below are plain lists of ids and an org with none counts no row.
        var administrators = await db.OrgMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.Status == OrgMemberStatus.Active && (m.Role == OrgRole.Owner || m.Role == OrgRole.Admin))
            .Select(m => m.UserId)
            .ToListAsync(ct);
        if (administrators.Count == 0)
            return false;

        if (await db.UserNotificationChannelSettings
                .AsNoTracking()
                .AnyAsync(s => s.OrgId == orgId && administrators.Contains(s.UserId), ct))
        {
            return true;
        }

        return await db.UserNotificationPreferences
            .AsNoTracking()
            .AnyAsync(
                p => p.OrgId == orgId
                     && administrators.Contains(p.UserId)
                     && (p.QuietHoursStart != UserNotificationPreferences.DefaultQuietHoursStart
                         || p.QuietHoursEnd != UserNotificationPreferences.DefaultQuietHoursEnd
                         || !p.LegalAlertsOverrideQuietHours),
                ct);
    }

    private async Task<bool> AssistantSituationConfiguredAsync(OnboardingPartContext context, CancellationToken ct)
    {
        var orgId = context.OrgId;
        var reached = context.PropertyIds.ToList();
        return await db.AssistantSituationSettings
            .AsNoTracking()
            .AnyAsync(s => s.OrgId == orgId && (s.PropertyId == null || reached.Contains(s.PropertyId.Value)), ct);
    }
}

/// <summary>
/// The part «Fiscal documents» (E10/F01/US04/T01, NF-104, E06): done when the organization chose who issues its fiscal documents, that is when its
/// <see cref="OrgFiscalIssuerProfile"/> exists (Octorate, CasaZen for the direct bookings, or «none, my accountant issues»). The choice «none» counts as
/// done and the facts say so with <see cref="OnboardingPartReasons.AccountantHandles"/>; no profile is <see cref="OnboardingPartReasons.IssuerMissing"/>.
/// </summary>
/// <remarks>
/// The choice belongs to the organization, not to some of its properties: it is the same for every caller, as <c>GET api/fiscal/documents/settings</c>
/// is readable by a caller who reaches only some of the properties. One row is read, by org; the data of the issuer is never read.
/// </remarks>
public sealed class FiscalDocumentsPartEvaluator(AppDbContext db) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.FiscalDocuments;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var mode = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .Where(p => p.OrgId == context.OrgId)
            .Select(p => (DocumentIssuerMode?)p.IssuerMode)
            .FirstOrDefaultAsync(ct);

        return mode switch
        {
            // Nobody issues here: a choice like the others. The reason says so; the plan shows none for a part that is done.
            DocumentIssuerMode.Accountant => new PartFacts(true, 1, 1, OnboardingPartReasons.AccountantHandles),
            DocumentIssuerMode.Octorate or DocumentIssuerMode.CasaZenDirectOnly => PartFacts.Counted(1, 1, null),
            _ => PartFacts.Counted(0, 1, OnboardingPartReasons.IssuerMissing),
        };
    }
}

/// <summary>
/// The part «Partners» (E10/F01/US04/T01, NF-104, E09): provided only while <see cref="FeatureFlags.OctorateConnector"/> is on, the flag of the
/// marketplace endpoints that depend on Octorate (otherwise «not available»); done when at least one integration of the organization is active or
/// declared, read through the catalog of the marketplace (<see cref="IMarketplaceCatalogService.GetActiveAsync"/>, the Active tab), never from its
/// tables. The part is optional: no other part requires it (<see cref="OnboardingPartCatalog"/>), so it never blocks the activation.
/// </summary>
/// <remarks>
/// <b>Reach.</b> The catalog reads for the caller's scope: the integrations of the properties the caller reaches, and the organization-level ones only
/// for a caller who reaches the whole org. The part says nothing more than the Active tab does. Nothing calls Octorate and nothing is written.
/// </remarks>
public sealed class PartnersPartEvaluator(IFeatureFlags featureFlags, IMarketplaceCatalogService catalog) : IOnboardingPartEvaluator
{
    public OnboardingPartKey Part => OnboardingPartKey.Partners;

    public int Order => OnboardingPartEvaluatorOrders.Evaluator;

    public async Task<PartFacts> EvaluateAsync(OnboardingPartContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return PartFacts.NotAvailable(OnboardingPartReasons.ConnectorOff);

        var active = await catalog.GetActiveAsync(context.OrgId, context.Scope, null, ct);
        var any = active.Integrations.Any(integration => integration.Status == MarketplaceCardStatus.Active || integration.IsDeclared);
        return PartFacts.Counted(any ? 1 : 0, 1, OnboardingPartReasons.PartnerIntegrationMissing);
    }
}
