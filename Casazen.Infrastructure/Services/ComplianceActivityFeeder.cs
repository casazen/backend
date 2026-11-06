using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceActivityFeeder" />
/// <remarks>
/// <para><b>One "To do" per item.</b> The list is reconciled in steps against the To do already open for the stays, properties and months
/// the evaluation looked at (<see cref="ComplianceEvaluatedUniverse"/>): first the open ones whose item is green or gone, or whose type, urgency or reason
/// no longer say what the item says, are closed by the system (<see cref="IInternalActivityService.CloseAsync"/>); then the items that need attention
/// and have no open To do get one (<see cref="IInternalActivityService.CreateAsync"/>, idempotent: a parallel run returns the first). Closing comes
/// first, so an item that changes what it asks for (a stay that stops being entrusted and has to be sent by hand, a receipt awaited that turns late)
/// replaces its To do in the same run. The match is on the action, the kind of entity and the id
/// (<see cref="IInternalActivityService.FindOpenAsync(Guid, string, string, IReadOnlyCollection{Guid}, CancellationToken)"/>), whoever raised the To do:
/// the alert stages and this feeder share the item of a stay, but only a To do this feeder raised (its reason is a code of
/// <see cref="ComplianceReasonCodes"/>) is replaced when the item changes.</para>
/// <para><b>Stays that owe nothing any more.</b> A stay that was cancelled, became a no-show, is gone or arrived before the lookback is not evaluated
/// any more, so it has no item to turn green: its open To do (of the actions of a stay) would stay on the list for ever, and a red one in the
/// count of the urgent. The second step finds the open To do of the caller's reach about stays the evaluation did not look at and closes those
/// whose stay no longer owes a communication (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/> and no no-show, the one definition), or is
/// older than the lookback. A stay that is active and simply not in this evaluation (arriving later) keeps its To do.</para>
/// <para><b>Same action and id as the cockpit.</b> The action of the To do is the name of the cockpit action wherever the list already has an
/// equivalent (<see cref="ActivityAction"/>); an item of a month is about the property and carries the month in its entity type
/// (<c>PropertyPeriod:yyyy-MM</c>), so the key is (action, property, month).</para>
/// <para><b>Cheap, and bounded.</b> A reconcile is one indexed read per kind of item and no write when nothing changed. The caller that starts a run
/// holds the throttle from the start (<see cref="Throttle"/>), so a parallel read of the same caller (the Home and the cockpit together) does not
/// repeat the work; a run that fails gives the throttle back, and a run does at most <see cref="MaxChangesPerRun"/> changes (the most urgent first),
/// the rest waits for the next read (<see cref="ContinueAfter"/>). No personal data is written: ids and reason codes.</para>
/// </remarks>
public sealed class ComplianceActivityFeeder(
    IInternalActivityService activities,
    AppDbContext db,
    IMemoryCache cache,
    ILogger<ComplianceActivityFeeder> logger) : IComplianceActivityFeeder
{
    /// <summary>The kind of entity of a To do about a property.</summary>
    internal const string PropertyEntityType = "Property";

    /// <summary>The start of the kind of entity of a To do about a property and a month: <c>PropertyPeriod:yyyy-MM</c>.</summary>
    internal const string PeriodEntityTypePrefix = "PropertyPeriod:";

    /// <summary>How long an org and scope that started a run are left alone.</summary>
    internal static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);

    /// <summary>How long they are left alone when a run stopped at its limit: the next read goes on with what is left.</summary>
    internal static readonly TimeSpan ContinueAfter = TimeSpan.FromSeconds(5);

    /// <summary>The most To do a run raises and closes together; the rest is left for the next run.</summary>
    internal const int MaxChangesPerRun = 50;

    /// <summary>Makes the check and the start of the throttle one step: two requests that arrive together do not both run.</summary>
    private static readonly object Gate = new();

    private enum Level
    {
        Booking,
        Property,
        PropertyPeriod,
    }

    private sealed record Mapping(ActivityType Type, ActivityAction Action, Level Level);

    private readonly record struct TodoKey(ActivityAction Action, string EntityType, Guid EntityId);

    private sealed record Desired(Mapping Mapping, ComplianceStatusItem Item, string EntityType, Guid EntityId);

    /// <summary>
    /// The cockpit actions that feed the To do list and what each becomes. The Alloggiati ones share the action of the page of the proofs
    /// except the one the host sends by hand; the type of the receipt to upload is the one the alert stages use. The identity item has the
    /// type of the operators' alert (<see cref="ActivityType.IdentityVerification"/>, <c>VerificationOperatorAlerts</c>): the unique index of the
    /// open items then keeps ONE To do for the verification of a booking whichever of the two raises it first.
    /// </summary>
    private static readonly IReadOnlyDictionary<ComplianceCockpitAction, Mapping> Fed = new Dictionary<ComplianceCockpitAction, Mapping>
    {
        [ComplianceCockpitAction.SendAlloggiati] = new(ActivityType.AlloggiatiDeadline, ActivityAction.UploadAlloggiatiProof, Level.Booking),
        [ComplianceCockpitAction.ResolveAlloggiatiFailure] = new(ActivityType.AlloggiatiDeadline, ActivityAction.UploadAlloggiatiProof, Level.Booking),
        [ComplianceCockpitAction.UploadAlloggiatiReceipt] = new(ActivityType.ReceiptToRetrieve, ActivityAction.UploadAlloggiatiProof, Level.Booking),
        [ComplianceCockpitAction.SendAlloggiatiManually] = new(ActivityType.AlloggiatiDeadline, ActivityAction.SendAlloggiatiManually, Level.Booking),
        [ComplianceCockpitAction.VerifyIdentity] = new(ActivityType.IdentityVerification, ActivityAction.VerifyIdentity, Level.Booking),
        [ComplianceCockpitAction.ActivateOctorateSend] = new(ActivityType.ReconnectChannelManager, ActivityAction.ActivateOctorateSend, Level.Property),
        [ComplianceCockpitAction.RenewPoliceCredentials] = new(ActivityType.CredentialsToRenew, ActivityAction.RenewPoliceCredentials, Level.Property),
        [ComplianceCockpitAction.EnterMunicipalityTaxRate] = new(ActivityType.MunicipalityTaxRateMissing, ActivityAction.EnterMunicipalityTaxRate, Level.Property),
        [ComplianceCockpitAction.UploadIstatProof] = new(ActivityType.IstatDeadline, ActivityAction.UploadIstatProof, Level.PropertyPeriod),
        [ComplianceCockpitAction.DeclareTouristTax] = new(ActivityType.TouristTaxDeadline, ActivityAction.DeclareTouristTax, Level.PropertyPeriod),
    };

    /// <summary>The cockpit actions that feed the To do list (every other action has its own producer).</summary>
    internal static IReadOnlyCollection<ComplianceCockpitAction> FedActions => Fed.Keys.ToList();

    /// <summary>The cockpit actions that do not feed the To do list here: the operational items, whose To do their own producers keep.</summary>
    internal static IReadOnlyCollection<ComplianceCockpitAction> NotFedActions { get; } =
    [
        ComplianceCockpitAction.ActivateProperty,
        ComplianceCockpitAction.CompleteGuestCheckIn,
        ComplianceCockpitAction.CheckOut,
        ComplianceCockpitAction.ConfirmPropertyReady,
    ];

    public async Task FeedAsync(HostScope scope, ComplianceStatusResult status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(status);

        // The scope is a record: two reads of the same caller are the same key, and another caller of the same org is another one.
        var throttleKey = ("compliance-activity-feed", scope);
        if (!TryBegin(throttleKey))
            return;

        var finished = false;
        try
        {
            var desired = DesiredTodos(status);
            var open = await ReadOpenAsync(scope.OrgId, status.Evaluated, ct);
            var ofStaysThatOweNothing = await ReadOfStaysThatOweNothingAsync(scope, status.Evaluated, ct);

            // To close: the open ones whose item is green or gone, the ones that no longer say what the item says, and the ones of stays that owe nothing.
            var closing = new Dictionary<Guid, InternalActivity>();
            var replaced = new HashSet<TodoKey>();
            foreach (var (key, activity) in open)
            {
                if (!desired.TryGetValue(key, out var wanted))
                {
                    // Green, or the item no longer applies: the condition that raised it is gone.
                    closing[activity.Id] = activity;
                }
                else if (IsStale(activity, wanted))
                {
                    closing[activity.Id] = activity;
                    replaced.Add(key);
                }
            }

            foreach (var activity in ofStaysThatOweNothing)
                closing[activity.Id] = activity;

            var changes = 0;
            var capped = false;
            var closedIds = new HashSet<Guid>();
            foreach (var activity in closing.Values.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id))
            {
                if (changes >= MaxChangesPerRun)
                {
                    capped = true;
                    break;
                }

                await activities.CloseAsync(activity.Id, InternalActivity.ClosedBySystem, ct);
                closedIds.Add(activity.Id);
                changes++;
            }

            var closed = closedIds.Count;
            var created = 0;
            foreach (var (key, todo) in desired
                         .OrderByDescending(pair => pair.Value.Item.Verdict.Light)
                         .ThenBy(pair => pair.Value.Item.DueAt ?? DateTime.MaxValue))
            {
                if (open.TryGetValue(key, out var existing) && !closedIds.Contains(existing.Id))
                    continue;

                if (changes >= MaxChangesPerRun)
                {
                    capped = true;
                    break;
                }

                await activities.CreateAsync(Request(todo), ct);
                created++;
                changes++;
            }

            finished = true;
            if (capped)
                cache.Set(throttleKey, true, ContinueAfter);

            if (created + closed > 0)
            {
                logger.LogInformation(
                    "Compliance To do of org {OrgId} in step with the cockpit: {Created} raised, {Closed} closed{More}",
                    scope.OrgId, created, closed, capped ? ", more to do on the next read" : string.Empty);
            }
        }
        finally
        {
            // A run that failed is tried again by the next read, not left alone for a minute.
            if (!finished)
                cache.Remove(throttleKey);
        }
    }

    /// <summary>Starts the throttle of a caller, or says it is already running or was run a moment ago.</summary>
    private bool TryBegin(object key)
    {
        lock (Gate)
        {
            if (cache.TryGetValue(key, out _))
                return false;

            cache.Set(key, true, Throttle);
            return true;
        }
    }

    /// <summary>The To do the evaluation asks for: one per (action, entity), the most urgent item if two meet.</summary>
    private static Dictionary<TodoKey, Desired> DesiredTodos(ComplianceStatusResult status)
    {
        var desired = new Dictionary<TodoKey, Desired>();
        foreach (var item in status.Items)
        {
            if (!item.Verdict.NeedsAttention || !Fed.TryGetValue(item.Action, out var mapping))
                continue;

            string entityType;
            Guid entityId;
            switch (mapping.Level)
            {
                case Level.Booking when item.BookingId is { } bookingId:
                    entityType = InternalActivity.BookingEntityType;
                    entityId = bookingId;
                    break;
                case Level.Property:
                    entityType = PropertyEntityType;
                    entityId = item.PropertyId;
                    break;
                case Level.PropertyPeriod when !string.IsNullOrEmpty(item.Period):
                    entityType = PeriodEntityTypePrefix + item.Period;
                    entityId = item.PropertyId;
                    break;
                default:
                    continue;
            }

            var key = new TodoKey(mapping.Action, entityType, entityId);
            if (!desired.TryGetValue(key, out var existing) || existing.Item.Verdict.Light < item.Verdict.Light)
                desired[key] = new Desired(mapping, item, entityType, entityId);
        }

        return desired;
    }

    /// <summary>
    /// Whether an open To do of this feed no longer says what its item says: another type (the receipt to upload instead of the communication to
    /// send), another urgency (a yellow that turned red, a red that turned yellow) or another reason. A To do another producer raised (its reason is a
    /// text, not a code of the compliance status) is never rewritten here.
    /// </summary>
    private static bool IsStale(InternalActivity activity, Desired wanted) =>
        activity.Reason is { } reason
        && ComplianceReasonCodes.All.Contains(reason)
        && (!string.Equals(activity.ActivityType, wanted.Mapping.Type.ToString(), StringComparison.Ordinal)
            || activity.Urgency != UrgencyOf(wanted.Item)
            || !string.Equals(reason, wanted.Item.Verdict.Reason, StringComparison.Ordinal));

    private static ActivityUrgency UrgencyOf(ComplianceStatusItem item) =>
        item.Verdict.Light == ComplianceLight.Red ? ActivityUrgency.High : ActivityUrgency.Medium;

    /// <summary>The open To do of the stays, properties and months the evaluation looked at, whoever raised them.</summary>
    private async Task<Dictionary<TodoKey, InternalActivity>> ReadOpenAsync(
        Guid orgId,
        ComplianceEvaluatedUniverse universe,
        CancellationToken ct)
    {
        var open = new Dictionary<TodoKey, InternalActivity>();
        var notRead = universe.ConnectionNotReadPropertyIds ?? new HashSet<Guid>();
        foreach (var mapping in Fed.Values.DistinctBy(m => (m.Action, m.Level)))
        {
            switch (mapping.Level)
            {
                case Level.Booking:
                    // The identity of a stay is looked at for the stays arriving soon, the Alloggiati status for the ones whose arrival came.
                    await AddOpenAsync(
                        mapping.Action,
                        InternalActivity.BookingEntityType,
                        mapping.Action == ActivityAction.VerifyIdentity ? universe.IdentityBookingIds : universe.BookingIds);
                    break;
                case Level.Property:
                    // The police send of a property that was not read in this evaluation was not looked at: its To do stays as it is.
                    await AddOpenAsync(
                        mapping.Action,
                        PropertyEntityType,
                        mapping.Action == ActivityAction.ActivateOctorateSend ? universe.PropertyIds.Where(id => !notRead.Contains(id)).ToList() : universe.PropertyIds);
                    break;
                default:
                    foreach (var period in universe.Periods)
                        await AddOpenAsync(mapping.Action, PeriodEntityTypePrefix + period, universe.PropertyIds);
                    break;
            }
        }

        return open;

        async Task AddOpenAsync(ActivityAction action, string entityType, IReadOnlyCollection<Guid> ids)
        {
            if (ids.Count == 0)
                return;

            var found = await activities.FindOpenAsync(orgId, action.ToString(), entityType, ids, ct);
            foreach (var (entityId, activity) in found)
                open[new TodoKey(action, entityType, entityId)] = activity;
        }
    }

    /// <summary>
    /// The open To do of the actions of a stay, in the caller's reach, about stays the evaluation did NOT look at and that owe nothing any more: a stay
    /// that was cancelled (or is a request or a hold), became a no-show, is not there any more, or (for the Alloggiati communication) arrived before
    /// the lookback. A stay that is active and arrives later than the evaluation looks keeps its To do.
    /// </summary>
    private async Task<List<InternalActivity>> ReadOfStaysThatOweNothingAsync(
        HostScope scope,
        ComplianceEvaluatedUniverse universe,
        CancellationToken ct)
    {
        var looked = universe.BookingIds.Concat(universe.IdentityBookingIds).Distinct().ToList();
        var candidates = new List<InternalActivity>();
        foreach (var action in Fed.Values.Where(m => m.Level == Level.Booking).Select(m => m.Action).Distinct())
        {
            candidates.AddRange(await activities.FindOpenInScopeAsync(scope, action.ToString(), InternalActivity.BookingEntityType, looked, ct));
        }

        if (candidates.Count == 0)
            return candidates;

        var ids = candidates.Select(a => a.EntityId!.Value).Distinct().ToList();
        var stays = (await db.Bookings
                .AsNoTracking()
                .Where(b => ids.Contains(b.Id))
                .Select(b => new { b.Id, b.Status, b.NoShowAt, b.CheckInDate })
                .ToListAsync(ct))
            .ToDictionary(b => b.Id);

        return candidates
            .Where(activity =>
            {
                if (!stays.TryGetValue(activity.EntityId!.Value, out var stay))
                    return true;

                var owes = AlloggiatiStatusRules.ActiveBookingStatuses.Contains(stay.Status) && stay.NoShowAt is null;
                if (!owes)
                    return true;

                // The identity is looked at for the stays arriving soon only, whatever their age; the Alloggiati status only within the lookback.
                return activity.Action != ActivityAction.VerifyIdentity
                       && universe.BookingWindowStartUtc is { } from
                       && stay.CheckInDate < from;
            })
            .ToList();
    }

    private static CreateTodoRequest Request(Desired todo) =>
        new()
        {
            Type = todo.Mapping.Type,
            Action = todo.Mapping.Action,
            EntityType = todo.EntityType,
            EntityId = todo.EntityId,
            Urgency = UrgencyOf(todo.Item),
            PropertyId = todo.Item.PropertyId,
            Deadline = todo.Item.DueAt,
            Reason = todo.Item.Verdict.Reason,
        };
}
