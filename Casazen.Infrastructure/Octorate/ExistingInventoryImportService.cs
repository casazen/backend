using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IExistingInventoryImportService"/> (E02/F01/US03, D6, ADR-023, ADR-027): the <c>onOtas</c> scenario of the connect wizard.
/// It follows the access to each OTA channel (a durable handoff to the Octorate panel), the import of the rooms, the mapping of the listings
/// that <see cref="IChannelMappingService"/> proposes, and, for the listings a person confirmed, the reservations, the calendars and
/// «mark connected».
/// </summary>
/// <remarks>
/// <para><b>Never a creation.</b> The only calls made to Octorate are the read of the connections and the four operations of the D6 sequence
/// (<see cref="OctorateD6Operation"/>), plus what <see cref="IChannelMappingService.RefreshAsync"/> reads. This class does not depend on any
/// operation that creates a property, a listing or a connection, or that publishes or pushes content, so no state, error or retry can reach
/// one; the guard for the creation of a property on Octorate is <see cref="IInventoryScenarioGuard"/>.</para>
/// <para><b>Durable.</b> A step is written before it runs and its result after it, item by item; the stamps of
/// <see cref="OctorateInventoryImportItem"/> say what is done, and nothing with a stamp is asked again. A command that finds another one
/// working on the same channel (the session lock of the channel) leaves it alone.</para>
/// <para><b>The shared context.</b> <see cref="IBookingImportService.ImportAllAsync"/> clears the change tracker of the context it shares with
/// this class, so the work that surrounds it saves first, keeps only ids across the call and loads the rows again after it.</para>
/// <para><b>Fail closed.</b> An operation whose capability flag is off (<see cref="OctorateExistingInventoryOptions"/>) is not called: the
/// channel stops with <c>d6_operation_unverified</c>. The error of an operation is classified once, in <see cref="Classify"/>: the rate limit,
/// the refusal of the token and the wrong environment stop the whole channel for this command; the others stop the item only.</para>
/// </remarks>
public sealed class ExistingInventoryImportService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IChannelMappingService mappings,
    IBookingImportService bookings,
    IChannelAccessHandoff handoff,
    IHostScopeResolver hostScopeResolver,
    IOptions<OctorateOptions> options,
    TimeProvider timeProvider,
    ILogger<ExistingInventoryImportService> logger) : IExistingInventoryImportService
{
    /// <summary>The most channels one batch holds.</summary>
    public const int MaxChannelsPerBatch = 20;

    /// <summary>The most assignments of one request of the intents, and the most properties of one batch.</summary>
    public const int MaxAssignmentsPerRequest = 500;

    private static readonly IReadOnlySet<string> NoTokenRoles = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NoIds = new HashSet<string>(StringComparer.Ordinal);

    private static readonly EventId AuditEvent = new(52_311, "OctorateExistingInventoryAudit");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private OctorateExistingInventoryOptions Settings => options.Value.ExistingInventory;

    // ─── Batches ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ExistingInventoryBatchView> CreateBatchAsync(
        ExistingInventoryCaller caller,
        IReadOnlyCollection<string> channelCodes,
        ExistingInventoryScope scope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(channelCodes);
        ArgumentNullException.ThrowIfNull(scope);

        var codes = await ValidChannelsAsync(channelCodes, ct);
        var reach = await ReachAsync(caller, ct);
        var propertyIds = await ResolveScopeAsync(caller, reach, scope, ct);

        var now = Now();
        var batch = new OctorateInventoryImportBatch
        {
            OrgId = caller.OrgId,
            ScopeKind = scope.Kind,
            ScopeGroup = scope.Kind == OctorateInventoryScopeKind.Group ? Cut(scope.Group, OctorateInventoryImportBatch.GroupMaxLength) : null,
            ScopePropertyIdsJson = JsonSerializer.Serialize(propertyIds, Json),
            CreatedByUserId = Cut(caller.UserId, OctorateInventoryImportBatch.UserIdMaxLength) ?? caller.UserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var code in codes)
        {
            batch.Channels.Add(new OctorateInventoryImportChannel
            {
                OrgId = caller.OrgId,
                ChannelCode = code,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.OctorateInventoryImportBatches.Add(batch);

        // The properties the person named are declared «already on OTAs»: from now on nothing can create them again on Octorate.
        if (scope.Kind == OctorateInventoryScopeKind.Properties)
            await DeclareOnOtasAsync(caller, propertyIds, overwrite: true, now, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OctorateInventoryIntent.PropertyIndexName))
        {
            throw IntentConflict();
        }

        Audit("batch-created", caller.OrgId, batch.Id, null, codes.Count, caller.UserId);
        return await ViewAsync(caller.OrgId, batch.Id, ct);
    }

    public async Task<ExistingInventoryBatchView> GetBatchAsync(ExistingInventoryCaller caller, Guid batchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var batch = await LoadBatchAsync(caller.OrgId, batchId, ct);
        await AuthorizeAsync(caller, batch, ct);

        // The result of the access is read here, for the channels that wait for it; a check of the access runs only when its backoff is due.
        var changed = false;
        foreach (var channel in Ordered(batch))
            changed |= await SyncAccessAsync(batch.OrgId, channel, verifyIfDue: true, ct);

        if (changed)
        {
            ApplyBatchStatus(batch);
            await db.SaveChangesAsync(ct);
        }

        return await ViewAsync(caller.OrgId, batch.Id, ct);
    }

    // ─── Access to the channels ───────────────────────────────────────────────────────────────────────────────

    public async Task<ChannelAccessStartResult> StartChannelAccessAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        string channelCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var batch = await LoadBatchAsync(caller.OrgId, batchId, ct);
        await AuthorizeAsync(caller, batch, ct);
        var channel = ChannelOf(batch, channelCode);

        // The access is already given (by this batch or by an earlier one for the same channel): nothing to open.
        if (channel.AccessCompletedAt is null)
        {
            var snapshot = await handoff.GetAsync(batch.OrgId, channel.ChannelCode, verifyIfDue: false, ct);
            if (snapshot.IsComplete)
            {
                ApplyAccess(channel, snapshot);
                ApplyBatchStatus(batch);
                await db.SaveChangesAsync(ct);
            }
        }

        if (channel.AccessCompletedAt is not null)
            return new ChannelAccessStartResult(channel.ChannelCode, true, null, null);

        // The step and the resume point are saved BEFORE the address is returned, and the channel's own state right after: a person who
        // closes the tab, reloads or changes device finds the exact step.
        var start = await handoff.StartAsync(batch.OrgId, channel.ChannelCode, new ChannelAccessResumePoint(batch.Id, channel.ChannelCode), ct);
        var now = Now();
        channel.PanelStepId = start.StepId;
        channel.AccessStartedAt = now;
        channel.AccessState = ChannelAccessState.Waiting;
        SetStatus(channel, OctorateInventoryImportStatus.AwaitingChannelAccess, null);
        ApplyBatchStatus(batch);
        await db.SaveChangesAsync(ct);

        Audit("access-started", batch.OrgId, batch.Id, channel.ChannelCode, 0, caller.UserId);
        return new ChannelAccessStartResult(channel.ChannelCode, false, start.HandoffMode, start.PanelUrl);
    }

    public async Task<ExistingInventoryBatchView> ConfirmChannelAccessAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        string channelCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var batch = await LoadBatchAsync(caller.OrgId, batchId, ct);
        await AuthorizeAsync(caller, batch, ct);
        var channel = ChannelOf(batch, channelCode);

        // Only after a check stored «could not tell»; refused before it and after a check that found the access not valid.
        await handoff.ConfirmAsync(batch.OrgId, caller.UserId, channel.ChannelCode, ct);
        var snapshot = await handoff.GetAsync(batch.OrgId, channel.ChannelCode, verifyIfDue: false, ct);
        if (snapshot.State == ChannelAccessState.Confirmed)
            channel.AccessConfirmedByUserId = Cut(caller.UserId, OctorateInventoryImportChannel.UserIdMaxLength);

        ApplyAccess(channel, snapshot);
        ApplyBatchStatus(batch);
        await db.SaveChangesAsync(ct);

        Audit("access-confirmed", batch.OrgId, batch.Id, channel.ChannelCode, 0, caller.UserId);
        return await ViewAsync(caller.OrgId, batch.Id, ct);
    }

    // ─── Room import ──────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ExistingInventoryBatchView> RunAsync(ExistingInventoryCaller caller, Guid batchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var batch = await LoadBatchAsync(caller.OrgId, batchId, ct);
        await AuthorizeAsync(caller, batch, ct);

        // What the panel said about the access of each channel that waits for it, before anything else.
        foreach (var channel in Ordered(batch))
            await SyncAccessAsync(batch.OrgId, channel, verifyIfDue: true, ct);

        ApplyBatchStatus(batch);
        await db.SaveChangesAsync(ct);

        var ready = Ordered(batch)
            .Where(c => c.AccessCompletedAt is not null && c.RoomsImportedAt is null && c.Status != OctorateInventoryImportStatus.Completed)
            .Select(c => c.Id)
            .ToList();
        if (ready.Count == 0 && !batch.Channels.Any(c => c.RoomsImportedAt is not null))
            throw new DomainConflictException("existing_inventory_access_pending", "ExistingInventoryAccessPending");

        foreach (var channelId in ready)
        {
            var code = batch.Channels.First(c => c.Id == channelId).ChannelCode;
            await using var channelLock = await TryLockAsync(batch.OrgId, code, ct);
            if (channelLock is null)
            {
                logger.LogInformation("Existing inventory of org {OrgId}, channel {ChannelCode}: another command is working on it", batch.OrgId, code);
                continue;
            }

            await RunChannelRoomsAsync(batch.OrgId, batch.Id, channelId, ct);
        }

        await RefreshBatchStatusAsync(batch.OrgId, batch.Id, ct);
        return await ViewAsync(caller.OrgId, batch.Id, ct);
    }

    /// <summary>
    /// The rooms of one channel: the connections of the channel are read (the items are created from them), <c>IMPORT_ROOMS</c> is asked for each
    /// connection that has no stamp, and, with every room imported, the listings of the org are read again so the mapping can start. Nothing is
    /// created on Octorate. Every step is saved before the next one; any failure leaves the channel at the step that failed.
    /// </summary>
    private async Task RunChannelRoomsAsync(Guid orgId, Guid batchId, Guid channelId, CancellationToken ct)
    {
        var channel = await LoadChannelAsync(orgId, channelId, ct);
        var batch = await db.OctorateInventoryImportBatches.AsNoTracking().FirstAsync(b => b.OrgId == orgId && b.Id == batchId, ct);

        SetStatus(channel, OctorateInventoryImportStatus.ImportingRooms, null);
        await db.SaveChangesAsync(ct);

        // The connections of the channel, read every time while the rooms are not all in (a listing connected meanwhile joins the others).
        OctorateConnectionSummary[] connections;
        try
        {
            using (orgScope.Use(orgId))
                connections = await client.GetOrgConnectionsAsync(ct);
        }
        catch (OctorateException ex)
        {
            await StopChannelAsync(channel, Classify(ex), ct);
            return;
        }

        var found = connections
            .Where(c => string.Equals(c.ChannelId, channel.ChannelCode, StringComparison.Ordinal))
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        await AddItemsAsync(batch, channel, found, ct);
        if (channel.Items.Count == 0)
        {
            // The access is given but Octorate shows no connection of this channel: nothing is created to make one.
            await StopChannelAsync(channel, new Failure(OctorateInventoryErrorCodes.ChannelConnectionNotFound, OctorateInventoryImportStatus.NeedsAttention, false), ct);
            return;
        }

        var failures = new List<Failure>();
        Failure? stop = null;
        foreach (var item in channel.Items.Where(i => i.RoomsImportedAt is null).OrderBy(i => i.OctorateConnectionId, StringComparer.Ordinal).ToList())
        {
            var failure = await DoD6Async(orgId, channel, item, OctorateD6Operation.ImportRooms, ct);
            if (failure is null)
                continue;

            failures.Add(failure);
            if (failure.StopsChannel)
            {
                stop = failure;
                break;
            }
        }

        if (channel.Items.Any(i => i.RoomsImportedAt is null))
        {
            await StopChannelAsync(channel, stop ?? WorstOf(failures), ct);
            return;
        }

        // Every room is in: the listings of the org are read again and the matcher proposes a property for each. Then a person decides.
        try
        {
            await mappings.RefreshAsync(orgId, ct);
        }
        catch (OctorateException ex)
        {
            await StopChannelAsync(channel, Classify(ex), ct);
            return;
        }
        catch (DomainConflictException ex) when (ex.Code == "octorate_mapping_refresh_conflict")
        {
            await StopChannelAsync(channel, new Failure(OctorateInventoryErrorCodes.MappingRefreshConflict, OctorateInventoryImportStatus.NeedsAttention, false), ct);
            return;
        }

        channel.RoomsImportedAt = Now();
        SetStatus(channel, OctorateInventoryImportStatus.AwaitingMapping, null);
        await db.SaveChangesAsync(ct);
        Audit("rooms-imported", orgId, batchId, channel.ChannelCode, channel.Items.Count, null);
    }

    // ─── Reservations, calendars, «mark connected» ────────────────────────────────────────────────────────────

    public async Task<ExistingInventoryBatchView> ContinueAsync(
        ExistingInventoryCaller caller,
        Guid batchId,
        IReadOnlyCollection<string>? leaveForLater,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var orgId = caller.OrgId;
        var batch = await LoadBatchAsync(orgId, batchId, ct);
        await AuthorizeAsync(caller, batch, ct);

        var leaveNow = NormalizeIds(leaveForLater);
        var channels = Ordered(batch).Where(c => c.AccessCompletedAt is not null && c.RoomsImportedAt is not null).ToList();
        if (channels.Count == 0)
            throw new DomainConflictException("existing_inventory_not_ready", "ExistingInventoryNotReady");

        // The decision about every listing, before anything is written or asked of Octorate: every one is confirmed, rejected, out of the
        // batch or left for later, or the command is refused.
        var rows = await RowsAsync(orgId, ct);
        var intents = await IntentsAsync(orgId, ct);
        var scopeIds = ScopeIds(batch);
        var plan = new Dictionary<Guid, ItemPlan>();
        var pending = 0;
        foreach (var channel in channels)
        {
            foreach (var item in channel.Items)
            {
                rows.TryGetValue(item.OctorateAccommodationId, out var row);
                var decision = Decide(item, row, leaveNow, intents, scopeIds);
                plan[item.Id] = new ItemPlan(decision, row?.Property?.Id);
                if (decision == ItemDecision.Pending)
                    pending++;
            }
        }

        if (pending > 0)
        {
            throw new DomainConflictException("existing_inventory_mapping_pending", "ExistingInventoryMappingPending", pending)
            {
                Extensions = new Dictionary<string, object?> { ["pending"] = pending },
            };
        }

        // The decisions are saved: what was left for later is remembered, what was decided since is no longer left.
        var now = Now();
        foreach (var item in channels.SelectMany(c => c.Items))
        {
            var decision = plan[item.Id].Decision;
            if (decision == ItemDecision.LeftForLater && item.LeftForLaterAt is null)
            {
                item.LeftForLaterAt = now;
                item.LeftForLaterByUserId = Cut(caller.UserId, OctorateInventoryImportItem.UserIdMaxLength);
                item.UpdatedAt = now;
            }
            else if (decision != ItemDecision.LeftForLater && item.LeftForLaterAt is not null)
            {
                item.LeftForLaterAt = null;
                item.LeftForLaterByUserId = null;
                item.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);

        // A property the import itself made (confirmed from a listing of this batch) has no declaration yet: it is «already on OTAs».
        var undeclared = plan.Values
            .Where(p => p.Decision == ItemDecision.Eligible && p.PropertyId is { } id && !intents.ContainsKey(id))
            .Select(p => p.PropertyId!.Value)
            .Distinct()
            .ToList();
        await DeclareImportedAsync(caller, undeclared, ct);

        var channelIds = channels.Select(c => (c.Id, c.ChannelCode)).ToList();
        foreach (var (channelId, code) in channelIds)
        {
            await using var channelLock = await TryLockAsync(orgId, code, ct);
            if (channelLock is null)
            {
                logger.LogInformation("Existing inventory of org {OrgId}, channel {ChannelCode}: another command is working on it", orgId, code);
                continue;
            }

            await ContinueChannelAsync(orgId, batchId, channelId, plan, ct);
        }

        await RefreshBatchStatusAsync(orgId, batchId, ct);
        return await ViewAsync(orgId, batchId, ct);
    }

    /// <summary>
    /// The phases of one channel for the listings a person confirmed: the reservations (<c>IMPORT_RESERVATIONS</c>, then the import into
    /// CasaZen), the calendars, «mark connected». Each phase takes the listings that finished the previous one, so one that fails does not
    /// hold the others back; a listing left for later, rejected or out of the batch is not touched. Everything is saved item by item.
    /// </summary>
    private async Task ContinueChannelAsync(Guid orgId, Guid batchId, Guid channelId, IReadOnlyDictionary<Guid, ItemPlan> plan, CancellationToken ct)
    {
        var failures = new List<Failure>();
        Failure? stop = null;

        // 1. The reservations asked of Octorate (once for each connection).
        var channel = await LoadChannelAsync(orgId, channelId, ct);
        var eligible = EligibleItems(channel, plan);
        var reservationItems = eligible.Where(i => i.ReservationsImportedAt is null && i.OctorateReservationsImportedAt is null).ToList();
        if (eligible.Any(i => i.ReservationsImportedAt is null))
        {
            SetStatus(channel, OctorateInventoryImportStatus.ImportingReservations, null);
            await db.SaveChangesAsync(ct);
        }

        foreach (var item in reservationItems)
        {
            var failure = await DoD6Async(orgId, channel, item, OctorateD6Operation.ImportReservations, ct);
            if (failure is null)
                continue;

            failures.Add(failure);
            if (failure.StopsChannel)
            {
                stop = failure;
                break;
            }
        }

        // 2. The reservations read into CasaZen, property by property. The import clears the tracker of the shared context: only ids are kept
        // across it, and the rows are loaded again afterwards.
        if (stop is null)
        {
            var toImport = EligibleItems(channel, plan)
                .Where(i => i.OctorateReservationsImportedAt is not null && i.ReservationsImportedAt is null)
                .Select(i => (ItemId: i.Id, PropertyId: plan[i.Id].PropertyId))
                .Where(x => x.PropertyId is not null)
                .ToList();
            var outcomes = new Dictionary<Guid, BookingOutcome>();
            if (toImport.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                foreach (var propertyId in toImport.Select(x => x.PropertyId!.Value).Distinct().ToList())
                {
                    var outcome = await ImportBookingsAsync(orgId, propertyId, ct);
                    outcomes[propertyId] = outcome;
                    if (outcome.Failure is { StopsChannel: true })
                    {
                        stop = outcome.Failure;
                        break;
                    }
                }

                db.ChangeTracker.Clear();
                channel = await LoadChannelAsync(orgId, channelId, ct);
                var now = Now();
                foreach (var (itemId, propertyId) in toImport)
                {
                    if (!outcomes.TryGetValue(propertyId!.Value, out var outcome))
                        continue;

                    var item = channel.Items.First(i => i.Id == itemId);
                    if (outcome.Failure is null)
                    {
                        item.ReservationsImportedAt = now;
                        item.ErrorCode = null;
                    }
                    else
                    {
                        item.ErrorCode = outcome.Failure.Code;
                        failures.Add(outcome.Failure);
                    }

                    item.UpdatedAt = now;
                }

                await db.SaveChangesAsync(ct);
            }
        }

        // 3. The calendars of the listings whose reservations are in.
        if (stop is null)
        {
            channel = await LoadChannelAsync(orgId, channelId, ct);
            var calendarItems = EligibleItems(channel, plan).Where(i => i.ReservationsImportedAt is not null && i.CalendarImportedAt is null).ToList();
            if (calendarItems.Count > 0)
            {
                SetStatus(channel, OctorateInventoryImportStatus.ImportingCalendar, null);
                await db.SaveChangesAsync(ct);
                foreach (var item in calendarItems)
                {
                    var failure = await DoD6Async(orgId, channel, item, OctorateD6Operation.ImportCalendar, ct);
                    if (failure is null)
                        continue;

                    failures.Add(failure);
                    if (failure.StopsChannel)
                    {
                        stop = failure;
                        break;
                    }
                }
            }
        }

        // 4. «Mark connected», only for a listing a person confirmed whose reservations and calendar are in.
        if (stop is null)
        {
            channel = await LoadChannelAsync(orgId, channelId, ct);
            var connectItems = EligibleItems(channel, plan).Where(i => i.CalendarImportedAt is not null && i.ConnectedAt is null).ToList();
            if (connectItems.Count > 0)
            {
                SetStatus(channel, OctorateInventoryImportStatus.MarkingConnected, null);
                await db.SaveChangesAsync(ct);
                foreach (var item in connectItems)
                {
                    var failure = await DoD6Async(orgId, channel, item, OctorateD6Operation.MarkConnected, ct);
                    if (failure is null)
                        continue;

                    failures.Add(failure);
                    if (failure.StopsChannel)
                    {
                        stop = failure;
                        break;
                    }
                }
            }
        }

        channel = await LoadChannelAsync(orgId, channelId, ct);
        if (stop is not null)
        {
            await StopChannelAsync(channel, stop, ct);
            return;
        }

        var open = EligibleItems(channel, plan).Where(i => i.ConnectedAt is null).ToList();
        if (open.Count > 0)
        {
            await StopChannelAsync(channel, WorstOf(failures), ct);
            return;
        }

        // Every listing is connected, left for later, rejected or out of the batch: nothing is left for the import to do.
        var done = Now();
        channel.CompletedAt ??= done;
        SetStatus(channel, OctorateInventoryImportStatus.Completed, null);
        await db.SaveChangesAsync(ct);
        Audit("channel-completed", orgId, batchId, channel.ChannelCode, channel.Items.Count(i => i.ConnectedAt is not null), null);
    }

    /// <summary>The reservations of one property read into CasaZen, outside the tracked state of this class.</summary>
    private async Task<BookingOutcome> ImportBookingsAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        ImportResult result;
        try
        {
            using (orgScope.Use(orgId))
                result = await bookings.ImportAllAsync(propertyId, ct);
        }
        catch (OctorateException ex)
        {
            return new BookingOutcome(Classify(ex));
        }
        finally
        {
            // The import clears the shared tracker itself; doing it here too keeps the behaviour the same whatever the implementation does.
            db.ChangeTracker.Clear();
        }

        return result.Outcome switch
        {
            ImportOutcome.Completed => new BookingOutcome(null),
            ImportOutcome.RoomNotChosen => new BookingOutcome(new Failure(OctorateInventoryErrorCodes.RoomNotChosen, OctorateInventoryImportStatus.NeedsAttention, false)),
            ImportOutcome.NotMapped => new BookingOutcome(new Failure(OctorateInventoryErrorCodes.MappingNotConfirmed, OctorateInventoryImportStatus.NeedsAttention, false)),
            _ => new BookingOutcome(new Failure(OctorateInventoryErrorCodes.ReservationImportIncomplete, OctorateInventoryImportStatus.NeedsAttention, false)),
        };
    }

    // ─── One operation of the D6 sequence on one item ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asks Octorate for one operation of the D6 sequence on one connection, and writes the stamp: <c>null</c> when it is done (or was done
    /// for the same connection by an earlier batch), otherwise why it is not. An operation not verified for the channel is not called.
    /// </summary>
    private async Task<Failure?> DoD6Async(
        Guid orgId,
        OctorateInventoryImportChannel channel,
        OctorateInventoryImportItem item,
        OctorateD6Operation operation,
        CancellationToken ct)
    {
        if (await InheritAsync(orgId, item, operation, ct))
        {
            item.ErrorCode = null;
            item.UpdatedAt = Now();
            await db.SaveChangesAsync(ct);
            return null;
        }

        if (!Settings.IsVerified(channel.ChannelCode, operation))
        {
            // Never a success that Octorate did not confirm: the step stays guided until the pilot has verified it for this channel.
            item.ErrorCode = OctorateInventoryErrorCodes.D6OperationUnverified;
            item.UpdatedAt = Now();
            await db.SaveChangesAsync(ct);
            return new Failure(OctorateInventoryErrorCodes.D6OperationUnverified, OctorateInventoryImportStatus.NeedsAttention, false);
        }

        OctorateConnectionActionResult result;
        try
        {
            using (orgScope.Use(orgId))
            {
                result = operation switch
                {
                    OctorateD6Operation.ImportRooms => await client.ImportRoomsAsync(item.OctorateAccommodationId, item.OctorateConnectionId, ct),
                    OctorateD6Operation.ImportReservations => await client.ImportReservationsAsync(item.OctorateAccommodationId, item.OctorateConnectionId, ct),
                    OctorateD6Operation.ImportCalendar => await client.ImportCalendarAsync(item.OctorateAccommodationId, item.OctorateConnectionId, ct),
                    OctorateD6Operation.MarkConnected => await client.MarkConnectedAsync(item.OctorateAccommodationId, item.OctorateConnectionId, ct),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not an operation of the D6 sequence."),
                };
            }
        }
        catch (OctorateException ex)
        {
            var failure = Classify(ex);
            item.ErrorCode = failure.Code;
            item.UpdatedAt = Now();
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "Existing inventory of org {OrgId}, channel {ChannelCode}, connection {ConnectionId}: {Operation} did not complete ({Error})",
                orgId, channel.ChannelCode, item.OctorateConnectionId, operation, failure.Code);
            return failure;
        }

        var now = Now();
        switch (operation)
        {
            case OctorateD6Operation.ImportRooms:
                item.RoomsImportedAt = now;
                item.RoomCount = result.Mappings.Count;
                break;
            case OctorateD6Operation.ImportReservations:
                item.OctorateReservationsImportedAt = now;
                break;
            case OctorateD6Operation.ImportCalendar:
                item.CalendarImportedAt = now;
                break;
            case OctorateD6Operation.MarkConnected:
                item.ConnectedAt = now;
                break;
        }

        item.ErrorCode = null;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// True (and the stamp copied) when another item of the org for the same Octorate connection already has the stamp of the operation: the
    /// same external item is never imported or marked twice, also across batches.
    /// </summary>
    private async Task<bool> InheritAsync(Guid orgId, OctorateInventoryImportItem item, OctorateD6Operation operation, CancellationToken ct)
    {
        var siblings = await db.OctorateInventoryImportItems
            .AsNoTracking()
            .Where(i => i.OrgId == orgId && i.OctorateConnectionId == item.OctorateConnectionId && i.Id != item.Id)
            .ToListAsync(ct);
        if (siblings.Count == 0)
            return false;

        switch (operation)
        {
            case OctorateD6Operation.ImportRooms:
                var rooms = siblings.Where(s => s.RoomsImportedAt is not null).OrderBy(s => s.RoomsImportedAt).FirstOrDefault();
                if (rooms is null)
                    return false;

                item.RoomsImportedAt = rooms.RoomsImportedAt;
                item.RoomCount = rooms.RoomCount;
                return true;
            case OctorateD6Operation.ImportReservations:
                var reservations = siblings.Where(s => s.OctorateReservationsImportedAt is not null).Select(s => s.OctorateReservationsImportedAt).Min();
                if (reservations is null)
                    return false;

                item.OctorateReservationsImportedAt = reservations;
                return true;
            case OctorateD6Operation.ImportCalendar:
                var calendar = siblings.Where(s => s.CalendarImportedAt is not null).Select(s => s.CalendarImportedAt).Min();
                if (calendar is null)
                    return false;

                item.CalendarImportedAt = calendar;
                return true;
            case OctorateD6Operation.MarkConnected:
                var connected = siblings.Where(s => s.ConnectedAt is not null).Select(s => s.ConnectedAt).Min();
                if (connected is null)
                    return false;

                item.ConnectedAt = connected;
                return true;
            default:
                return false;
        }
    }

    // ─── Items and decisions ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the items of the connections the channel does not hold yet, except those the person never meant for this import: a listing it
    /// rejected, and one confirmed to a property that is declared in another scenario or is outside the scope of the batch. The stamps an
    /// earlier batch wrote for the same connection are taken just before an operation would be asked (<see cref="InheritAsync"/>).
    /// </summary>
    private async Task AddItemsAsync(
        OctorateInventoryImportBatch batch,
        OctorateInventoryImportChannel channel,
        IReadOnlyList<OctorateConnectionSummary> found,
        CancellationToken ct)
    {
        var known = channel.Items.Select(i => i.OctorateConnectionId).ToHashSet(StringComparer.Ordinal);
        var added = found.Where(c => !known.Contains(c.Id)).ToList();
        if (added.Count == 0)
            return;

        var rows = await RowsAsync(batch.OrgId, ct);
        var intents = await IntentsAsync(batch.OrgId, ct);
        var scopeIds = ScopeIds(batch);
        var now = Now();
        foreach (var connection in added)
        {
            rows.TryGetValue(connection.AccommodationId, out var row);
            var probe = new OctorateInventoryImportItem { OctorateAccommodationId = connection.AccommodationId };
            if (row is not null && Decide(probe, row, NoIds, intents, scopeIds) is ItemDecision.Rejected or ItemDecision.OutOfScope)
                continue;

            channel.Items.Add(new OctorateInventoryImportItem
            {
                OrgId = batch.OrgId,
                ChannelId = channel.Id,
                OctorateAccommodationId = connection.AccommodationId,
                OctorateConnectionId = connection.Id,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        channel.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static ItemDecision Decide(
        OctorateInventoryImportItem item,
        ChannelMappingRow? row,
        IReadOnlySet<string> leaveNow,
        IReadOnlyDictionary<Guid, OctorateInventoryScenario> intents,
        IReadOnlySet<Guid>? scopeIds)
    {
        if (row is null)
            return ItemDecision.Pending;

        if (row.Status == OctorateMappingStatus.Rejected)
            return ItemDecision.Rejected;

        if (row.Status == OctorateMappingStatus.Confirmed && row.Property is { } property)
            return InScope(property.Id, intents, scopeIds) ? ItemDecision.Eligible : ItemDecision.OutOfScope;

        return item.LeftForLaterAt is not null || leaveNow.Contains(item.OctorateAccommodationId)
            ? ItemDecision.LeftForLater
            : ItemDecision.Pending;
    }

    /// <summary>
    /// A confirmed listing belongs to the batch when its property is declared «already on OTAs» and in the scope of the batch, or has no
    /// declaration at all (a property the import itself made from a listing of the batch). One declared in another scenario never does.
    /// </summary>
    private static bool InScope(Guid propertyId, IReadOnlyDictionary<Guid, OctorateInventoryScenario> intents, IReadOnlySet<Guid>? scopeIds)
    {
        if (!intents.TryGetValue(propertyId, out var scenario))
            return true;

        return scenario == OctorateInventoryScenario.OnOtas && (scopeIds is null || scopeIds.Contains(propertyId));
    }

    private static List<OctorateInventoryImportItem> EligibleItems(OctorateInventoryImportChannel channel, IReadOnlyDictionary<Guid, ItemPlan> plan) =>
        channel.Items
            .Where(i => plan.TryGetValue(i.Id, out var p) && p.Decision == ItemDecision.Eligible)
            .OrderBy(i => i.OctorateAccommodationId, StringComparer.Ordinal)
            .ThenBy(i => i.OctorateConnectionId, StringComparer.Ordinal)
            .ToList();

    // ─── Intents ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<InventoryIntentView>> SetIntentsAsync(
        ExistingInventoryCaller caller,
        IReadOnlyCollection<InventoryIntentAssignment> assignments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count is 0 or > MaxAssignmentsPerRequest)
            throw new DomainRuleException("inventory_intent_assignments_invalid", "InventoryIntentAssignmentsInvalid", MaxAssignmentsPerRequest);

        var reach = await ReachAsync(caller, ct);
        var shaped = new List<(Guid? PropertyId, OctorateInventoryScenario Scenario, string? Group)>();
        foreach (var assignment in assignments)
        {
            if (!Enum.IsDefined(assignment.Scenario))
                throw new DomainRuleException("inventory_scenario_invalid", "InventoryScenarioInvalid");

            var group = Cut(assignment.Group, OctorateInventoryIntent.WizardBatchMaxLength);
            if (assignment.PropertyId is null)
            {
                // The intent of a group that has no property yet is an org-level decision.
                if (group is null)
                    throw new DomainRuleException("inventory_group_required", "InventoryGroupRequired");

                if (!reach.IsOrgWide)
                    throw new DomainForbiddenException("existing_inventory_needs_whole_org", "ExistingInventoryNeedsWholeOrg");
            }

            shaped.Add((assignment.PropertyId, assignment.Scenario, group));
        }

        if (shaped.Where(a => a.PropertyId is not null).GroupBy(a => a.PropertyId).Any(g => g.Count() > 1)
            || shaped.Where(a => a.PropertyId is null).GroupBy(a => a.Group, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw new DomainRuleException("inventory_intent_duplicate", "InventoryIntentDuplicate");
        }

        // Only properties of the org within the reach of the caller: another org's property is the same 404 as an unknown one.
        var ids = shaped.Where(a => a.PropertyId is not null).Select(a => a.PropertyId!.Value).ToList();
        if (ids.Count > 0 && !await CanReachPropertiesAsync(caller, ids, ct))
            throw PropertyNotFound();

        var now = Now();
        var existing = await db.OctorateInventoryIntents.Where(i => i.OrgId == caller.OrgId).ToListAsync(ct);
        var touched = new List<OctorateInventoryIntent>();
        foreach (var (propertyId, scenario, group) in shaped)
        {
            var row = propertyId is { } id
                ? existing.FirstOrDefault(i => i.PropertyId == id)
                : existing.FirstOrDefault(i => i.PropertyId is null && string.Equals(i.WizardBatch, group, StringComparison.Ordinal));

            // «New» is a choice that cannot be reached from an import: never while a batch or a mapping exists for the property.
            if (scenario == OctorateInventoryScenario.NewInventory
                && row?.Scenario != OctorateInventoryScenario.NewInventory
                && propertyId is { } changing
                && await HasImportEvidenceAsync(caller.OrgId, changing, ct))
            {
                throw new DomainConflictException("inventory_intent_change_refused", "InventoryIntentChangeRefused");
            }

            if (row is null)
            {
                row = new OctorateInventoryIntent
                {
                    OrgId = caller.OrgId,
                    PropertyId = propertyId,
                    Scenario = scenario,
                    WizardBatch = group,
                    ActorUserId = Cut(caller.UserId, OctorateInventoryIntent.UserIdMaxLength) ?? caller.UserId,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.OctorateInventoryIntents.Add(row);
            }
            else
            {
                row.Scenario = scenario;
                row.WizardBatch = group;
                row.ActorUserId = Cut(caller.UserId, OctorateInventoryIntent.UserIdMaxLength) ?? caller.UserId;
                row.UpdatedAt = now;
            }

            touched.Add(row);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex))
        {
            throw IntentConflict();
        }

        return [.. touched.Select(i => new InventoryIntentView(i.PropertyId, i.Scenario, i.WizardBatch, i.UpdatedAt))];
    }

    public async Task<IReadOnlyList<InventoryIntentView>> ListIntentsAsync(ExistingInventoryCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var reach = await ReachAsync(caller, ct);
        var rows = await db.OctorateInventoryIntents.AsNoTracking().Where(i => i.OrgId == caller.OrgId).ToListAsync(ct);
        if (!reach.IsOrgWide)
        {
            var ids = rows.Where(i => i.PropertyId is not null).Select(i => i.PropertyId!.Value).Distinct().ToList();
            var reachable = new HashSet<Guid>();
            foreach (var id in ids)
            {
                if (await CanReachPropertiesAsync(caller, [id], ct))
                    reachable.Add(id);
            }

            rows = [.. rows.Where(i => i.PropertyId is { } id && reachable.Contains(id))];
        }

        return
        [
            .. rows
                .OrderBy(i => i.WizardBatch, StringComparer.Ordinal)
                .ThenBy(i => i.PropertyId)
                .Select(i => new InventoryIntentView(i.PropertyId, i.Scenario, i.WizardBatch, i.UpdatedAt)),
        ];
    }

    /// <summary>
    /// True when an import already concerns the property: a batch of the org names it (in its properties, or through its group), or a listing
    /// is proposed or confirmed to it. A batch of the whole inventory names no property, so it does not by itself stop a new, unpublished
    /// property from being declared as such: that one has no listing and no mapping.
    /// </summary>
    private async Task<bool> HasImportEvidenceAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        if (await db.OctorateListingMappings.AsNoTracking().AnyAsync(
                m => m.OrgId == orgId
                     && m.PropertyId == propertyId
                     && (m.Status == OctorateMappingStatus.Proposed || m.Status == OctorateMappingStatus.Confirmed),
                ct))
        {
            return true;
        }

        var batches = await db.OctorateInventoryImportBatches.AsNoTracking()
            .Where(b => b.OrgId == orgId && b.ScopeKind != OctorateInventoryScopeKind.AllInventory)
            .Select(b => b.ScopePropertyIdsJson)
            .ToListAsync(ct);
        return batches.Any(json => ParseIds(json).Contains(propertyId));
    }

    /// <summary>Declares the properties «already on OTAs»; with <paramref name="overwrite"/> a different declaration is replaced, else it is kept.</summary>
    private async Task DeclareOnOtasAsync(
        ExistingInventoryCaller caller,
        IReadOnlyCollection<Guid> propertyIds,
        bool overwrite,
        DateTime now,
        CancellationToken ct)
    {
        if (propertyIds.Count == 0)
            return;

        var existing = (await db.OctorateInventoryIntents.Where(i => i.OrgId == caller.OrgId && i.PropertyId != null).ToListAsync(ct))
            .Where(i => propertyIds.Contains(i.PropertyId!.Value))
            .ToDictionary(i => i.PropertyId!.Value);
        foreach (var id in propertyIds)
        {
            if (existing.TryGetValue(id, out var row))
            {
                if (overwrite && row.Scenario != OctorateInventoryScenario.OnOtas)
                {
                    row.Scenario = OctorateInventoryScenario.OnOtas;
                    row.ActorUserId = Cut(caller.UserId, OctorateInventoryIntent.UserIdMaxLength) ?? caller.UserId;
                    row.UpdatedAt = now;
                }

                continue;
            }

            db.OctorateInventoryIntents.Add(new OctorateInventoryIntent
            {
                OrgId = caller.OrgId,
                PropertyId = id,
                Scenario = OctorateInventoryScenario.OnOtas,
                ActorUserId = Cut(caller.UserId, OctorateInventoryIntent.UserIdMaxLength) ?? caller.UserId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    /// <summary>
    /// Declares «already on OTAs» the properties the import made from the listings of the batch. Another command that declared one first wins
    /// on the unique index of the intents, and its row is the one that stays.
    /// </summary>
    private async Task DeclareImportedAsync(ExistingInventoryCaller caller, IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        if (propertyIds.Count == 0)
            return;

        await DeclareOnOtasAsync(caller, propertyIds, overwrite: false, Now(), ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (OrgTeamAccess.IsUniqueViolation(ex, OctorateInventoryIntent.PropertyIndexName))
        {
            foreach (var entry in db.ChangeTracker.Entries<OctorateInventoryIntent>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }
    }

    // ─── Reach, loading, views ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The caller's scope, or 403 when it has none in the org.</summary>
    private async Task<HostScope> ReachAsync(ExistingInventoryCaller caller, CancellationToken ct) =>
        await hostScopeResolver.ResolveAsync(caller.UserId, caller.TokenRoles ?? NoTokenRoles, caller.OrgId, ct)
        ?? throw new DomainForbiddenException("no_org_context", "NoOrganizationContext");

    /// <summary>
    /// The batch is within the reach of the caller: the whole inventory needs the whole org, and the properties of the batch must be the ones
    /// the caller reaches. Otherwise it is the same 404 as a batch that does not exist.
    /// </summary>
    private async Task AuthorizeAsync(ExistingInventoryCaller caller, OctorateInventoryImportBatch batch, CancellationToken ct)
    {
        var reach = await ReachAsync(caller, ct);
        if (reach.IsOrgWide)
            return;

        var ids = PropertyIdsOf(batch);
        if (batch.ScopeKind == OctorateInventoryScopeKind.AllInventory || ids.Count == 0 || !await CanReachPropertiesAsync(caller, ids, ct))
            throw BatchNotFound();
    }

    /// <summary>The properties of a batch scope, each of the org and within the reach of the caller, or 404 / 403.</summary>
    private Task<List<Guid>> ResolveScopeAsync(ExistingInventoryCaller caller, HostScope reach, ExistingInventoryScope scope, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case OctorateInventoryScopeKind.AllInventory:
                if (!reach.IsOrgWide)
                    throw new DomainForbiddenException("existing_inventory_needs_whole_org", "ExistingInventoryNeedsWholeOrg");

                return Task.FromResult(new List<Guid>());

            case OctorateInventoryScopeKind.Properties:
                return ResolvePropertiesScopeAsync(caller, scope, ct);

            case OctorateInventoryScopeKind.Group:
                return ResolveGroupScopeAsync(caller, reach, scope, ct);

            default:
                throw new DomainRuleException("existing_inventory_scope_invalid", "ExistingInventoryScopeInvalid");
        }
    }

    /// <summary>The properties the caller named: each one of its org and within its reach (404 otherwise).</summary>
    private async Task<List<Guid>> ResolvePropertiesScopeAsync(ExistingInventoryCaller caller, ExistingInventoryScope scope, CancellationToken ct)
    {
        var ids = (scope.PropertyIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxAssignmentsPerRequest)
            throw new DomainRuleException("existing_inventory_scope_invalid", "ExistingInventoryScopeInvalid");

        if (!await CanReachPropertiesAsync(caller, ids, ct))
            throw PropertyNotFound();

        return ids;
    }

    /// <summary>The properties of a named group of the caller's org, declared «already on OTAs» and within the caller's reach.</summary>
    private async Task<List<Guid>> ResolveGroupScopeAsync(ExistingInventoryCaller caller, HostScope reach, ExistingInventoryScope scope, CancellationToken ct)
    {
        var group = Cut(scope.Group, OctorateInventoryIntent.WizardBatchMaxLength)
                    ?? throw new DomainRuleException("existing_inventory_scope_invalid", "ExistingInventoryScopeInvalid");
        var members = await db.OctorateInventoryIntents.AsNoTracking()
            .Where(i => i.OrgId == caller.OrgId && i.WizardBatch == group)
            .ToListAsync(ct);
        if (members.Count == 0)
        {
            throw new NotFoundException("Inventory group not found.")
            {
                Code = "existing_inventory_group_not_found",
                MessageKey = "ExistingInventoryGroupNotFound",
            };
        }

        if (members.Any(m => m.Scenario != OctorateInventoryScenario.OnOtas))
            throw new DomainConflictException("existing_inventory_group_scenario_mismatch", "ExistingInventoryGroupScenarioMismatch");

        // A group with a member that has no property yet is an org-level decision.
        if (members.Any(m => m.PropertyId is null) && !reach.IsOrgWide)
            throw new DomainForbiddenException("existing_inventory_needs_whole_org", "ExistingInventoryNeedsWholeOrg");

        var ids = members.Where(m => m.PropertyId is not null).Select(m => m.PropertyId!.Value).Distinct().ToList();
        if (ids.Count > 0 && !await CanReachPropertiesAsync(caller, ids, ct))
            throw PropertyNotFound();

        return ids;
    }

    /// <summary>True when every property is one of the caller's org, not deleted, and within the reach of the caller (a property of another org is none of them).</summary>
    private async Task<bool> CanReachPropertiesAsync(ExistingInventoryCaller caller, IReadOnlyCollection<Guid> propertyIds, CancellationToken ct)
    {
        var ids = propertyIds.Distinct().ToList();
        var rows = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == caller.OrgId && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.OwnerId })
            .ToListAsync(ct);
        if (rows.Count != ids.Count)
            return false;

        foreach (var row in rows)
        {
            if (!await hostScopeResolver.CanReachPropertyAsync(caller.UserId, caller.TokenRoles ?? NoTokenRoles, caller.OrgId, row.Id, row.OwnerId, ct))
                return false;
        }

        return true;
    }

    /// <summary>The channel codes of a request: trimmed, distinct, and each one a listed, enabled OTA of the catalog of the channel manager.</summary>
    private async Task<List<string>> ValidChannelsAsync(IReadOnlyCollection<string> channelCodes, CancellationToken ct)
    {
        var codes = channelCodes
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            throw new DomainRuleException("existing_inventory_channels_required", "ExistingInventoryChannelsRequired");

        if (codes.Count > MaxChannelsPerBatch)
            throw new DomainRuleException("existing_inventory_too_many_channels", "ExistingInventoryTooManyChannels", MaxChannelsPerBatch);

        var listed = await db.MarketplacePortals
            .AsNoTracking()
            .Where(p => codes.Contains(p.PortalCode) && p.IsListed && p.Enabled && p.Category == MarketplacePortalCategory.Ota)
            .Select(p => p.PortalCode)
            .ToListAsync(ct);
        var unsupported = codes
            .Where(c => c.Length > OctorateInventoryImportChannel.ChannelCodeMaxLength || !listed.Contains(c, StringComparer.Ordinal))
            .ToList();
        if (unsupported.Count > 0)
        {
            throw new DomainRuleException("existing_inventory_channel_not_supported", "ExistingInventoryChannelNotSupported")
            {
                Extensions = new Dictionary<string, object?> { ["channels"] = unsupported },
            };
        }

        return codes;
    }

    private async Task<OctorateInventoryImportBatch> LoadBatchAsync(Guid orgId, Guid batchId, CancellationToken ct) =>
        await db.OctorateInventoryImportBatches
            .Include(b => b.Channels)
            .ThenInclude(c => c.Items)
            .FirstOrDefaultAsync(b => b.OrgId == orgId && b.Id == batchId, ct)
        ?? throw BatchNotFound();

    private async Task<OctorateInventoryImportChannel> LoadChannelAsync(Guid orgId, Guid channelId, CancellationToken ct) =>
        await db.OctorateInventoryImportChannels
            .Include(c => c.Items)
            .FirstAsync(c => c.OrgId == orgId && c.Id == channelId, ct);

    /// <summary>The listings of the org as the mapping shows them, by accommodation id (the first of an id wins).</summary>
    private async Task<IReadOnlyDictionary<string, ChannelMappingRow>> RowsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await mappings.ListAsync(orgId, ct);
        var byId = new Dictionary<string, ChannelMappingRow>(StringComparer.Ordinal);
        foreach (var row in rows)
            byId.TryAdd(row.OctorateAccommodationId, row);

        return byId;
    }

    /// <summary>The scenario declared for each property of the org that has a declaration.</summary>
    private async Task<IReadOnlyDictionary<Guid, OctorateInventoryScenario>> IntentsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await db.OctorateInventoryIntents.AsNoTracking()
            .Where(i => i.OrgId == orgId && i.PropertyId != null)
            .Select(i => new { i.PropertyId, i.Scenario })
            .ToListAsync(ct);
        return rows.ToDictionary(i => i.PropertyId!.Value, i => i.Scenario);
    }

    /// <summary>The batch as the API shows it, read from the database as it was saved.</summary>
    private async Task<ExistingInventoryBatchView> ViewAsync(Guid orgId, Guid batchId, CancellationToken ct)
    {
        var batch = await db.OctorateInventoryImportBatches
            .AsNoTracking()
            .Include(b => b.Channels)
            .ThenInclude(c => c.Items)
            .FirstOrDefaultAsync(b => b.OrgId == orgId && b.Id == batchId, ct)
            ?? throw BatchNotFound();

        var hasItems = batch.Channels.Any(c => c.Items.Count > 0);
        var rows = hasItems ? await RowsAsync(orgId, ct) : new Dictionary<string, ChannelMappingRow>(StringComparer.Ordinal);
        var intents = hasItems ? await IntentsAsync(orgId, ct) : new Dictionary<Guid, OctorateInventoryScenario>();
        var scopeIds = ScopeIds(batch);
        var channels = batch.Channels
            .OrderBy(c => c.ChannelCode, StringComparer.Ordinal)
            .Select(c => ProjectChannel(c, rows, intents, scopeIds))
            .ToList();
        return new ExistingInventoryBatchView(
            batch.Id,
            batch.ScopeKind,
            batch.ScopeGroup,
            PropertyIdsOf(batch),
            batch.Status,
            batch.ErrorCode,
            batch.CreatedAt,
            batch.UpdatedAt,
            batch.CompletedAt,
            channels);
    }

    private ExistingInventoryChannelView ProjectChannel(
        OctorateInventoryImportChannel channel,
        IReadOnlyDictionary<string, ChannelMappingRow> rows,
        IReadOnlyDictionary<Guid, OctorateInventoryScenario> intents,
        IReadOnlySet<Guid>? scopeIds)
    {
        var items = channel.Items
            .OrderBy(i => i.OctorateAccommodationId, StringComparer.Ordinal)
            .ThenBy(i => i.OctorateConnectionId, StringComparer.Ordinal)
            .ToList();
        var decisions = items.Select(i => Decide(i, rows.GetValueOrDefault(i.OctorateAccommodationId), NoIds, intents, scopeIds)).ToList();
        var counts = new ExistingInventoryCounts(
            items.Count,
            items.Count(i => i.RoomsImportedAt is not null),
            decisions.Count(d => d == ItemDecision.Eligible),
            decisions.Count(d => d == ItemDecision.Pending),
            decisions.Count(d => d == ItemDecision.LeftForLater),
            decisions.Count(d => d == ItemDecision.Rejected),
            decisions.Count(d => d == ItemDecision.OutOfScope),
            items.Count(i => i.ReservationsImportedAt is not null),
            items.Count(i => i.CalendarImportedAt is not null),
            items.Count(i => i.ConnectedAt is not null),
            items.Count(i => i.ErrorCode is not null));
        var views = items
            .Select(i => new ExistingInventoryItemView(
                i.OctorateAccommodationId,
                i.OctorateConnectionId,
                rows.TryGetValue(i.OctorateAccommodationId, out var row) ? row.Status : null,
                i.LeftForLaterAt is not null,
                i.RoomsImportedAt is not null,
                i.RoomCount,
                i.ReservationsImportedAt is not null,
                i.CalendarImportedAt is not null,
                i.ConnectedAt is not null,
                i.ErrorCode))
            .ToList();
        var code = channel.ChannelCode;
        return new ExistingInventoryChannelView(
            code,
            channel.Status,
            channel.ErrorCode,
            channel.AccessState,
            channel.AccessCompletedAt,
            channel.RoomsImportedAt,
            channel.CompletedAt,
            counts,
            new ExistingInventoryCapabilities(
                Settings.IsVerified(code, OctorateD6Operation.ImportRooms),
                Settings.IsVerified(code, OctorateD6Operation.ImportReservations),
                Settings.IsVerified(code, OctorateD6Operation.ImportCalendar),
                Settings.IsVerified(code, OctorateD6Operation.MarkConnected)),
            views);
    }

    // ─── Access state, status of the channel and of the batch ─────────────────────────────────────────────────

    /// <summary>Reads the result of the access of a channel that waits for it; <c>true</c> when the channel row changed.</summary>
    private async Task<bool> SyncAccessAsync(Guid orgId, OctorateInventoryImportChannel channel, bool verifyIfDue, CancellationToken ct)
    {
        if (channel.AccessCompletedAt is not null || channel.Status == OctorateInventoryImportStatus.Completed)
            return false;

        // A channel the person was never sent to the panel for has nothing to read.
        if (channel.PanelStepId is null && channel.Status == OctorateInventoryImportStatus.Draft)
            return false;

        var snapshot = await handoff.GetAsync(orgId, channel.ChannelCode, verifyIfDue, ct);
        return ApplyAccess(channel, snapshot);
    }

    /// <summary>
    /// Takes the state of the panel step into the channel: a given access (seen or confirmed) moves it on to the room import, one found not
    /// valid stops it with a stable code, and the state the wizard shows follows the step.
    /// </summary>
    private bool ApplyAccess(OctorateInventoryImportChannel channel, ChannelAccessSnapshot snapshot)
    {
        var changed = false;
        if (channel.AccessState != snapshot.State)
        {
            channel.AccessState = snapshot.State;
            changed = true;
        }

        if (snapshot.StepId is { } stepId && channel.PanelStepId != stepId)
        {
            channel.PanelStepId = stepId;
            changed = true;
        }

        var accessError = channel.Status == OctorateInventoryImportStatus.NeedsAttention
                          && channel.ErrorCode == OctorateInventoryErrorCodes.ChannelAccessNotValid;
        if (snapshot.IsComplete)
        {
            channel.AccessCompletedAt ??= Now();
            if (channel.Status is OctorateInventoryImportStatus.Draft or OctorateInventoryImportStatus.AwaitingChannelAccess || accessError)
                SetStatus(channel, OctorateInventoryImportStatus.ImportingRooms, null);

            changed = true;
        }
        else if (snapshot.State == ChannelAccessState.NotValid)
        {
            if (!accessError)
                SetStatus(channel, OctorateInventoryImportStatus.NeedsAttention, OctorateInventoryErrorCodes.ChannelAccessNotValid);

            changed = true;
        }
        else if (accessError)
        {
            SetStatus(channel, OctorateInventoryImportStatus.AwaitingChannelAccess, null);
            changed = true;
        }

        if (changed)
            channel.UpdatedAt = Now();

        return changed;
    }

    private void SetStatus(OctorateInventoryImportChannel channel, OctorateInventoryImportStatus status, string? errorCode)
    {
        channel.Status = status;
        channel.ErrorCode = errorCode;
        channel.UpdatedAt = Now();
    }

    /// <summary>Stops the channel at the step that failed, with the stable code, and saves it.</summary>
    private async Task StopChannelAsync(OctorateInventoryImportChannel channel, Failure failure, CancellationToken ct)
    {
        SetStatus(channel, failure.Status, failure.Code);
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "Existing inventory of org {OrgId}, channel {ChannelCode} stopped: {Status} ({Error})",
            channel.OrgId, channel.ChannelCode, failure.Status, failure.Code);
    }

    /// <summary>
    /// The failure that decides the state of a channel when some listings did not get through: one that needs a person before one that failed,
    /// and, with none recorded (which a pass that stopped without trying cannot do), an unknown result.
    /// </summary>
    private static Failure WorstOf(IReadOnlyList<Failure> failures) =>
        failures.FirstOrDefault(f => f.Status == OctorateInventoryImportStatus.NeedsAttention)
        ?? failures.FirstOrDefault()
        ?? new Failure(OctorateInventoryErrorCodes.D6ResultUnknown, OctorateInventoryImportStatus.NeedsAttention, false);

    /// <summary>The error of an operation as a stable code and the state it leaves the channel in (see the remarks of the class).</summary>
    private static Failure Classify(OctorateException exception) => exception switch
    {
        OctorateRateLimitException => new Failure(exception.Code, OctorateInventoryImportStatus.NeedsAttention, true),
        OctorateAuthException => new Failure(exception.Code, OctorateInventoryImportStatus.NeedsAttention, true),
        OctorateWrongEnvironmentException => new Failure(exception.Code, OctorateInventoryImportStatus.NeedsAttention, true),
        OctorateServerException { OctorateStatusCode: 550 } => new Failure(OctorateInventoryErrorCodes.D6ChannelFailed, OctorateInventoryImportStatus.Failed, false),
        OctorateServerException => new Failure(OctorateInventoryErrorCodes.D6ResultUnknown, OctorateInventoryImportStatus.NeedsAttention, false),
        OctorateNotFoundException => new Failure(exception.Code, OctorateInventoryImportStatus.Failed, false),
        OctorateConflictException => new Failure(exception.Code, OctorateInventoryImportStatus.Failed, false),
        OctorateRequestRejectedException => new Failure(exception.Code, OctorateInventoryImportStatus.Failed, false),
        _ => new Failure(exception.Code, OctorateInventoryImportStatus.NeedsAttention, false),
    };

    /// <summary>The summary of the channels: done when all are, a person is needed before a failure, otherwise the earliest step still to do.</summary>
    internal static OctorateInventoryImportStatus Aggregate(IReadOnlyCollection<OctorateInventoryImportChannel> channels)
    {
        if (channels.Count == 0)
            return OctorateInventoryImportStatus.Draft;

        if (channels.All(c => c.Status == OctorateInventoryImportStatus.Completed))
            return OctorateInventoryImportStatus.Completed;

        if (channels.Any(c => c.Status == OctorateInventoryImportStatus.NeedsAttention))
            return OctorateInventoryImportStatus.NeedsAttention;

        if (channels.Any(c => c.Status == OctorateInventoryImportStatus.Failed))
            return OctorateInventoryImportStatus.Failed;

        return channels.Where(c => c.Status != OctorateInventoryImportStatus.Completed).Min(c => c.Status);
    }

    private void ApplyBatchStatus(OctorateInventoryImportBatch batch)
    {
        var status = Aggregate(batch.Channels);
        var now = Now();
        batch.Status = status;
        batch.ErrorCode = status switch
        {
            OctorateInventoryImportStatus.NeedsAttention => batch.Channels.FirstOrDefault(c => c.Status == status)?.ErrorCode,
            OctorateInventoryImportStatus.Failed => batch.Channels.FirstOrDefault(c => c.Status == status)?.ErrorCode,
            _ => null,
        };
        batch.CompletedAt = status == OctorateInventoryImportStatus.Completed ? batch.CompletedAt ?? now : null;
        batch.UpdatedAt = now;
    }

    /// <summary>The summary of the batch from its channels as saved (the rows are loaded again: the import may have cleared the tracker).</summary>
    private async Task RefreshBatchStatusAsync(Guid orgId, Guid batchId, CancellationToken ct)
    {
        var batch = await db.OctorateInventoryImportBatches
            .Include(b => b.Channels)
            .FirstOrDefaultAsync(b => b.OrgId == orgId && b.Id == batchId, ct);
        if (batch is null)
            return;

        ApplyBatchStatus(batch);
        await db.SaveChangesAsync(ct);
    }

    // ─── Small things ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Session lock of one channel of one org for the whole step; <c>null</c> when another command holds it.</summary>
    private Task<IAsyncDisposable?> TryLockAsync(Guid orgId, string channelCode, CancellationToken ct) =>
        PostgresAdvisoryLocks.TryAcquireSessionLockAsync(db, PostgresAdvisoryLocks.Scope.OctorateInventoryImport, $"{orgId:N}:{channelCode}", ct);

    private static List<OctorateInventoryImportChannel> Ordered(OctorateInventoryImportBatch batch) =>
        [.. batch.Channels.OrderBy(c => c.ChannelCode, StringComparer.Ordinal)];

    private static OctorateInventoryImportChannel ChannelOf(OctorateInventoryImportBatch batch, string channelCode)
    {
        var code = channelCode?.Trim();
        return batch.Channels.FirstOrDefault(c => string.Equals(c.ChannelCode, code, StringComparison.Ordinal))
               ?? throw new NotFoundException("Channel not in the import.")
               {
                   Code = "existing_inventory_channel_not_found",
                   MessageKey = "ExistingInventoryChannelNotFound",
               };
    }

    /// <summary>The properties named by a batch; the whole inventory names none.</summary>
    private static List<Guid> PropertyIdsOf(OctorateInventoryImportBatch batch) => ParseIds(batch.ScopePropertyIdsJson);

    /// <summary>The properties a batch is restricted to, or <c>null</c> for the whole inventory.</summary>
    private static HashSet<Guid>? ScopeIds(OctorateInventoryImportBatch batch) =>
        batch.ScopeKind == OctorateInventoryScopeKind.AllInventory ? null : [.. PropertyIdsOf(batch)];

    private static List<Guid> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The accommodation ids of a request, trimmed and distinct; one that cannot be an Octorate id is ignored.</summary>
    private static IReadOnlySet<string> NormalizeIds(IReadOnlyCollection<string>? ids)
    {
        if (ids is null || ids.Count == 0)
            return NoIds;

        if (ids.Count > MaxAssignmentsPerRequest)
            throw new DomainRuleException("existing_inventory_scope_invalid", "ExistingInventoryScopeInvalid");

        return ids
            .Select(id => id?.Trim())
            .Where(id => !string.IsNullOrEmpty(id) && id.Length <= OctorateInventoryImportItem.IdMaxLength)
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string? Cut(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private void Audit(string action, Guid orgId, Guid batchId, string? channelCode, int count, string? userId) =>
        logger.LogInformation(
            AuditEvent,
            "Octorate existing inventory {Action}: batch {BatchId} of org {OrgId}, channel {ChannelCode}, count {Count}, by user {UserId}",
            action, batchId, orgId, channelCode, count, userId);

    private static NotFoundException BatchNotFound() =>
        new("Existing inventory import not found.")
        {
            Code = "existing_inventory_batch_not_found",
            MessageKey = "ExistingInventoryBatchNotFound",
        };

    private static NotFoundException PropertyNotFound() =>
        new("Property not found for the Octorate import.")
        {
            Code = "octorate_property_not_found",
            MessageKey = "OctoratePropertyNotFound",
        };

    private static DomainConflictException IntentConflict() => new("inventory_intent_conflict", "InventoryIntentConflict");

    // ─── Types ────────────────────────────────────────────────────────────────────────────────────────────────

    private enum ItemDecision
    {
        /// <summary>Confirmed to a property of the batch: the import goes on for it.</summary>
        Eligible,

        /// <summary>The person said it is not one of the org's properties.</summary>
        Rejected,

        /// <summary>Confirmed to a property declared in another scenario, or outside the scope of the batch: left alone.</summary>
        OutOfScope,

        /// <summary>The person left it for later: it stays write-blocked and is not marked connected.</summary>
        LeftForLater,

        /// <summary>Nobody decided yet: the import does not go on until somebody does.</summary>
        Pending,
    }

    private sealed record ItemPlan(ItemDecision Decision, Guid? PropertyId);

    private sealed record Failure(string Code, OctorateInventoryImportStatus Status, bool StopsChannel);

    private sealed record BookingOutcome(Failure? Failure);
}
