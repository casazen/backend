using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IComplianceActivityFeeder" />
/// <remarks>
/// <para><b>One "To do" per item.</b> The list is reconciled in three steps against the To do already open for the stays, properties and months
/// the evaluation looked at (<see cref="ComplianceEvaluatedUniverse"/>): first the open ones whose item is green or gone are closed by the system
/// (<see cref="IInternalActivityService.CloseAsync"/>), then the items that need attention and have no open To do get one
/// (<see cref="IInternalActivityService.CreateAsync"/>, idempotent: a parallel run returns the first). Closing comes first, so an item that
/// changes what it asks for (a stay that stops being entrusted and has to be sent by hand) replaces its To do in the same run. The match is on the
/// action, the kind of entity and the id (<see cref="IInternalActivityService.FindOpenAsync(Guid, string, string, IReadOnlyCollection{Guid}, CancellationToken)"/>),
/// whoever raised the To do: the alert stages and this feeder share the item of a stay.</para>
/// <para><b>Same action and id as the cockpit.</b> The action of the To do is the name of the cockpit action wherever the list already has an
/// equivalent (<see cref="ActivityAction"/>); an item of a month is about the property and carries the month in its entity type
/// (<c>PropertyPeriod:yyyy-MM</c>), so the key is (action, property, month).</para>
/// <para><b>Cheap when nothing changed.</b> A reconcile is one indexed read per kind of item and no write; an org and scope that were fed less than
/// <see cref="Throttle"/> ago are skipped, so the page that reloads does not repeat it. No personal data is written: ids and reason codes.</para>
/// </remarks>
public sealed class ComplianceActivityFeeder(
    IInternalActivityService activities,
    IMemoryCache cache,
    ILogger<ComplianceActivityFeeder> logger) : IComplianceActivityFeeder
{
    /// <summary>The kind of entity of a To do about a property.</summary>
    internal const string PropertyEntityType = "Property";

    /// <summary>The start of the kind of entity of a To do about a property and a month: <c>PropertyPeriod:yyyy-MM</c>.</summary>
    internal const string PeriodEntityTypePrefix = "PropertyPeriod:";

    /// <summary>How long an org and scope that were fed are left alone.</summary>
    internal static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);

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
        if (cache.TryGetValue(throttleKey, out _))
            return;

        var desired = DesiredTodos(status);
        var open = await ReadOpenAsync(scope.OrgId, status.Evaluated, ct);

        var closed = 0;
        foreach (var (key, activity) in open)
        {
            if (desired.ContainsKey(key))
                continue;

            // Green, or the item no longer applies: the condition that raised it is gone.
            await activities.CloseAsync(activity.Id, InternalActivity.ClosedBySystem, ct);
            closed++;
        }

        var created = 0;
        foreach (var (key, todo) in desired)
        {
            if (open.ContainsKey(key))
                continue;

            await activities.CreateAsync(Request(todo), ct);
            created++;
        }

        cache.Set(throttleKey, true, Throttle);
        if (created + closed > 0)
        {
            logger.LogInformation(
                "Compliance To do of org {OrgId} in step with the cockpit: {Created} raised, {Closed} closed",
                scope.OrgId, created, closed);
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

    /// <summary>The open To do of the stays, properties and months the evaluation looked at, whoever raised them.</summary>
    private async Task<Dictionary<TodoKey, InternalActivity>> ReadOpenAsync(
        Guid orgId,
        ComplianceEvaluatedUniverse universe,
        CancellationToken ct)
    {
        var open = new Dictionary<TodoKey, InternalActivity>();
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
                    await AddOpenAsync(mapping.Action, PropertyEntityType, universe.PropertyIds);
                    break;
                default:
                    foreach (var period in universe.Periods)
                        await AddOpenAsync(mapping.Action, PeriodEntityTypePrefix + period, universe.PropertyIds);
                    break;
            }
        }

        return open;

        async Task AddOpenAsync(ActivityAction action, string entityType, IReadOnlySet<Guid> ids)
        {
            if (ids.Count == 0)
                return;

            var found = await activities.FindOpenAsync(orgId, action.ToString(), entityType, ids, ct);
            foreach (var (entityId, activity) in found)
                open[new TodoKey(action, entityType, entityId)] = activity;
        }
    }

    private static CreateTodoRequest Request(Desired todo) =>
        new()
        {
            Type = todo.Mapping.Type,
            Action = todo.Mapping.Action,
            EntityType = todo.EntityType,
            EntityId = todo.EntityId,
            Urgency = todo.Item.Verdict.Light == ComplianceLight.Red ? ActivityUrgency.High : ActivityUrgency.Medium,
            PropertyId = todo.Item.PropertyId,
            Deadline = todo.Item.DueAt,
            Reason = todo.Item.Verdict.Reason,
        };
}
