using System.Runtime.ExceptionServices;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Photos;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IContentPushService"/> (E02/F03/US01/T02, NF-27, NF-29, NF-30, NF-32, NF-34, NF-37, ADR-022, ADR-027, ADR-034, ADR-036): publishes a
/// property to Octorate and to the channels that receive content, and records what became of each channel.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Nothing is written before the guards.</b> The endpoint only queues a job; the guards (reach, content channel, confirmed mapping, the
/// declared scenario of a property with no mapping, the Italian and English texts, no unresolved change made outside CasaZen) run first and answer 4xx.
/// A list of channels that is empty is a 422, never «all». A publication refused for a change made outside CasaZen is the only outcome until the
/// person chooses to keep CasaZen's version (<c>overwrite</c>) or to take Octorate's descriptions (E02/F03/US01/T07): nothing is overwritten silently.
/// The content is shared by the channels of the property, so the change flagged on any of them counts, requested or not.</item>
/// <item><b>One run for each property.</b> <see cref="ListingPublishJob"/> runs under <see cref="IOctorateOrgScope.Use"/> of the property's org. A
/// property with no confirmed mapping is created on Octorate first (<see cref="IOctoratePropertyCreator"/>, which refuses existing inventory); a
/// mapping with no room gets its unit created once (found by name when an earlier run already made it) and set.</item>
/// <item><b>A channel is judged alone.</b> One whose rule is not met (<c>RequiresCin</c> and no valid CIN, fewer photos than <c>MinPhotos</c>) is left
/// <c>ToFix</c> with an error code and a «To do»; one the property is not connected to likewise; a failure of the push of one channel never cancels the
/// others (AC-NF29-3). A failure of a group of the content (the content is shared by the channels) fails the channels still waiting, with the code of
/// the error: nothing is marked accepted that Octorate did not take.</item>
/// <item><b>Accepted is not published.</b> A row goes <c>Sending</c> then <c>Accepted</c> (the channel manager took it); the move to <c>Published</c> or
/// <c>ToFix</c> is made only by the status of the process (E02/F03/US01/T07). The groups are read back and their SHA-256 stored as the baseline of the
/// changes made outside CasaZen, and moved to the other pushed channels of the property that are not flagged (the content is shared by the channels);
/// <c>ListingPublished</c> is recorded once for the property, in the same save as the first accepted state.</item>
/// <item><b>What is sent.</b> The accommodation, the unit, the descriptions in every language, the rooms with their beds, the structured rules, the
/// photos by their public address. The amenities and the cancellation policy only behind their options (not proven on the sandbox). The CIN never,
/// until D2 is answered. A failed read-back of the baseline does not undo the accepted state: it is recorded and the error rethrown.</item>
/// </list>
/// </remarks>
public sealed class ContentPushService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IChannelMappingService mappings,
    IInventoryScenarioGuard scenarios,
    IOctoratePropertyCreator creator,
    IHostScopeResolver hostScopeResolver,
    IFeatureFlags featureFlags,
    IDomainEventRecorder recorder,
    IBackgroundJobClient jobs,
    OctorateContentReader reader,
    ListingPhotoSync photoSync,
    PublicPhotoUrlService publicPhotos,
    ListingTodos todos,
    IOptions<ListingChannelOptions> options,
    TimeProvider timeProvider,
    ILogger<ContentPushService> logger) : IContentPushService
{
    /// <summary>Name of the unique index on the property and the channel of a state row.</summary>
    internal const string ChannelRowIndexName = "UIX_ListingChannelStates_PropertyId_ChannelCode";

    private static readonly IReadOnlySet<string> NoTokenRoles = new HashSet<string>(StringComparer.Ordinal);

    private static readonly EventId AuditEvent = new(52_261, "ListingPublishAudit");

    public async Task<IReadOnlyList<ListingChannelResult>> PublishAsync(
        Guid propertyId,
        IReadOnlyList<string> channels,
        bool overwrite,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(channels);

        // The tenant filter hides another org's property in a request; the reach of the person decides for the properties of the org.
        var roles = tokenRoles ?? NoTokenRoles;
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, ct);
        if (property is null || !await hostScopeResolver.CanReachPropertyAsync(userId, roles, property.OrgId, property.Id, property.OwnerId, ct))
            throw PropertyNotFound();

        var codes = ValidChannels(channels);

        // Nothing is written for a property that may already exist on Octorate and was not confirmed or rejected yet (OCT-04).
        if (await mappings.IsWriteBlockedAsync(propertyId, ct))
            throw new DomainConflictException("octorate_mapping_unconfirmed", "OctorateMappingUnconfirmed");

        // A property with no mapping is created on Octorate only when a person declared it never published (D6): a missing mapping proves nothing.
        if (await ConfirmedMappingAsync(property.OrgId, propertyId, ct) is null)
            await scenarios.EnsureNewInventoryAsync(property.OrgId, propertyId, ct);

        var missing = LanguageMapper.ValidateForPublish(property);
        if (missing.Count > 0)
        {
            throw new DomainRuleException("listing_content_incomplete", "ListingContentIncomplete")
            {
                Extensions = new Dictionary<string, object?> { ["missing"] = missing },
            };
        }

        // A change made on Octorate after the last push is overwritten only by the person's choice (NF-27). The groups of the content (the
        // accommodation, the descriptions, the rooms) are shared by the channels of the property and a publication writes them for all of them, so a
        // change flagged on any channel of the property stops it, whichever channels were asked for.
        var outside = await db.ListingChannelStates
            .AsNoTracking()
            .Where(s => s.PropertyId == propertyId && s.ExternalChangeAt != null)
            .Select(s => s.ChannelCode)
            .OrderBy(code => code)
            .ToListAsync(ct);
        if (!overwrite && outside.Count > 0)
        {
            throw new DomainConflictException("listing_modified_outside", "ListingModifiedOutside")
            {
                Extensions = new Dictionary<string, object?> { ["channels"] = outside },
            };
        }

        var results = await MarkSendingAsync(property, codes, ct);

        var channelArray = codes.ToArray();
        var roleArray = roles.ToArray();
        var jobId = jobs.Enqueue<ListingPublishJob>(
            job => job.RunAsync(propertyId, channelArray, overwrite, userId, roleArray, CancellationToken.None));
        logger.LogInformation(
            AuditEvent,
            "Listing publication queued: property {PropertyId} of org {OrgId}, {Channels} channel(s), overwrite {Overwrite}, by user {UserId} (job {JobId})",
            propertyId, property.OrgId, codes.Count, overwrite, userId, jobId);
        return results;
    }

    public async Task RunAsync(
        Guid propertyId,
        IReadOnlyList<string> channels,
        bool overwrite,
        string userId,
        IReadOnlySet<string>? tokenRoles,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Listing publication of property {PropertyId}: the connector is off, nothing done", propertyId);
            return;
        }

        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, ct);
        if (property is null)
        {
            logger.LogWarning("Listing publication of property {PropertyId}: the property is gone, nothing done", propertyId);
            return;
        }

        var codes = channels
            .Select(c => c?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c) && options.Value.RuleOf(c) is not null)
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rows = await RowsForRunAsync(property, codes, ct);
        if (rows.Count == 0)
            return;

        using (orgScope.Use(property.OrgId))
        {
            try
            {
                await PushAsync(property, rows, userId, tokenRoles, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await FailAsync(property, rows.Where(r => r.Status == ChannelPublishStatus.Sending).ToList(), ex, ct);

                // What the person can fix is on the rows and in their «To do»; anything else is a failure of the job too.
                if (ex is not (DomainException or OctoratePropertyRejectedException or OctorateRequestRejectedException))
                    throw;
            }
        }
    }

    /// <summary>The run itself, with the rows of the requested channels put in <c>Sending</c> by <see cref="RowsForRunAsync"/>.</summary>
    private async Task PushAsync(Property property, List<ListingChannelState> rows, string userId, IReadOnlySet<string>? roles, CancellationToken ct)
    {
        var orgId = property.OrgId;
        var document = SitePropertyProfileJson.Parse(property.SiteProfileJson);
        var photoUrls = publicPhotos.ListingPhotoUrls(property);
        var cinValid = CinFormat.IsValid(property.CinCode);

        // The rule of each channel, before anything is sent: a channel that cannot be published is left to fix, the others go on.
        var ready = new List<(ListingChannelState Row, ListingChannelRule Rule)>();
        var blocked = new List<ListingChannelState>();
        foreach (var row in rows.Where(r => r.Status == ChannelPublishStatus.Sending))
        {
            var rule = options.Value.RuleOf(row.ChannelCode)!;
            var code = rule.RequiresCin && !cinValid
                ? ListingChannelErrors.CinMissing
                : photoUrls.Count < rule.MinPhotos ? ListingChannelErrors.PhotosBelowMinimum : null;
            if (code is null)
            {
                ready.Add((row, rule));
                continue;
            }

            MarkToFix(row, code);
            blocked.Add(row);
        }

        if (blocked.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await RaiseErrorsAsync(blocked, ct);
        }

        if (ready.Count == 0)
            return;

        // The state may have changed since the publication was queued.
        if (await mappings.IsWriteBlockedAsync(property.Id, ct))
            throw new DomainConflictException("octorate_mapping_unconfirmed", "OctorateMappingUnconfirmed");

        var (accommodation, product) = await EnsureListingAsync(property, document, userId, roles, ct);

        // The channels the property is connected to: a content channel with no connection cannot be pushed to, one with no rule is managed on the channel.
        var connections = await client.GetConnectionsAsync(accommodation, ct);
        var disconnected = new List<ListingChannelState>();
        var targets = new List<(ListingChannelState Row, ListingChannelRule Rule, string? ConnectionId)>();
        foreach (var (row, rule) in ready)
        {
            var connection = connections.FirstOrDefault(c => options.Value.PortalCodeOfChannelId(c.ChannelId) == row.ChannelCode);
            if (connection is null)
            {
                MarkToFix(row, ListingChannelErrors.ChannelNotConnected);
                disconnected.Add(row);
                continue;
            }

            targets.Add((row, rule, connection.Id));
        }

        await UpsertManagedRowsAsync(property, connections, ct);
        await db.SaveChangesAsync(ct);
        if (disconnected.Count > 0)
            await RaiseErrorsAsync(disconnected, ct);

        if (targets.Count == 0)
            return;

        await PushGroupsAsync(property, document, accommodation, product, ct);
        await photoSync.SendAsync(property, accommodation, ct);

        // Each channel on its own: a failure stores its code and the others go on (AC-NF29-3).
        var accepted = new List<(ListingChannelState Row, string? ProcessId)>();
        var failed = new List<ListingChannelState>();
        foreach (var (row, rule, connectionId) in targets)
        {
            try
            {
                var push = await client.PushChannelAsync(
                    accommodation, product, new OctorateChannelTarget(row.ChannelCode, rule.UsesPushQueue, connectionId), ct);
                accepted.Add((row, push.ProcessId));
            }
            catch (Exception ex) when (ex is OctorateException or ArgumentException)
            {
                logger.LogWarning(
                    "Listing publication of property {PropertyId}, channel {ChannelCode}: the push failed ({Error})",
                    property.Id, row.ChannelCode, ex.GetType().Name);
                MarkToFix(row, ex is OctorateException octorate ? ListingChannelErrors.CodeOfOctorateError(octorate.Code) : ListingChannelErrors.ChannelPushFailed);
                failed.Add(row);
            }
        }

        if (failed.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await RaiseErrorsAsync(failed, ct);
        }

        if (accepted.Count == 0)
            return;

        // The groups as the channel manager holds them now are the baseline of the changes made outside CasaZen.
        IReadOnlyDictionary<string, string> hashes = new Dictionary<string, string>();
        Exception? readFailure = null;
        try
        {
            hashes = await reader.ReadAsync(accommodation, product, ct);
        }
        catch (OctorateException ex)
        {
            readFailure = ex;
            logger.LogWarning(
                "Listing publication of property {PropertyId}: the content could not be read back ({Error}), no baseline is stored",
                property.Id, ex.GetType().Name);
        }

        var firstPublication = !await db.ListingChannelStates.AnyAsync(s => s.PropertyId == property.Id && s.ContentVersion > 0, ct);
        var now = Now();
        var resolved = new List<(ListingChannelState Row, bool WasChangedOutside)>();
        foreach (var (row, processId) in accepted)
        {
            resolved.Add((row, row.ExternalChangeAt is not null));
            row.Status = ChannelPublishStatus.Accepted;
            row.ErrorCode = null;
            row.ErrorMessage = null;
            row.LastSyncedAt = now;
            row.ContentVersion++;
            row.ExternalChangeAt = null;
            row.ContentHashesJson = new ListingChannelBaseline(hashes, processId, []).ToJson();
        }

        // The groups are shared by the channels of the property: what was just written and read back is what every other pushed channel has on Octorate
        // now. Their baseline is moved to it, or a change CasaZen made itself would be found «outside» on a channel it was not pushed to this time.
        if (hashes.Count > 0)
            await AlignOtherBaselinesAsync(property.Id, accepted.Select(a => a.Row.Id).ToList(), hashes, ct);

        // The event in the same save as the state it describes (the outbox, ADR-022): once for the property, the first time the structure is accepted.
        var dedupKey = $"listing-published:{property.Id:N}";
        if (firstPublication && !await db.OutboxEvents.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AnyAsync(e => e.OrgId == orgId && e.DedupKey == dedupKey, ct))
        {
            recorder.Record(
                DomainEventType.ListingPublished,
                property.Id,
                new ListingPublishedPayload(property.Id, accommodation),
                dedupKey,
                orgId);
        }

        await db.SaveChangesAsync(ct);

        foreach (var (row, wasChangedOutside) in resolved)
        {
            await todos.CloseAsync(ActivityType.PublicationError, row, InternalActivity.ClosedBySystem, ct);
            if (wasChangedOutside)
                await todos.CloseAsync(ActivityType.ListingChangedOutside, row, InternalActivity.ClosedBySystem, ct);
        }

        if (readFailure is not null)
            ExceptionDispatchInfo.Capture(readFailure).Throw();
    }

    /// <summary>
    /// Moves the baseline of the other pushed channels of the property (<c>Accepted</c> or <c>Published</c>, with a baseline, not flagged) to the
    /// groups just read back: they were written for every channel, so the content CasaZen put there is not a change made outside CasaZen. A channel
    /// already flagged keeps its baseline, since its change is still the person's to decide; the process each one waits on is kept, and so are its
    /// time of confirmation and its version (the channel itself was not pushed).
    /// </summary>
    private async Task AlignOtherBaselinesAsync(Guid propertyId, List<Guid> pushed, IReadOnlyDictionary<string, string> hashes, CancellationToken ct)
    {
        var others = await db.ListingChannelStates
            .Where(s => s.PropertyId == propertyId
                        && !pushed.Contains(s.Id)
                        && (s.Status == ChannelPublishStatus.Accepted || s.Status == ChannelPublishStatus.Published)
                        && s.ExternalChangeAt == null)
            .ToListAsync(ct);
        foreach (var row in others)
        {
            var baseline = ListingChannelBaseline.Parse(row.ContentHashesJson);
            if (baseline.HasBaseline)
                row.ContentHashesJson = new ListingChannelBaseline(hashes, baseline.ProcessId, []).ToJson();
        }
    }

    /// <summary>
    /// The accommodation and the unit of the property on Octorate. A property with no confirmed mapping is created (the creator refuses existing
    /// inventory); a mapping with no room is given one: the unit an earlier run made (found by its name), else a new one for a property declared new,
    /// else the only room of the listing. A listing with several rooms and no choice is left to the person (<c>octorate_room_required</c>).
    /// </summary>
    private async Task<(string Accommodation, string Product)> EnsureListingAsync(
        Property property,
        SitePropertyProfileDocument document,
        string userId,
        IReadOnlySet<string>? roles,
        CancellationToken ct)
    {
        var orgId = property.OrgId;
        var mapping = await ConfirmedMappingAsync(orgId, property.Id, ct);
        if (mapping is null)
        {
            await creator.CreateAsync(property.Id, userId, ct, roles);
            mapping = await ConfirmedMappingAsync(orgId, property.Id, ct)
                      ?? throw new InvalidOperationException("The property was created on Octorate but has no confirmed mapping.");
        }

        if (mapping.Value.Product is { } existing)
            return (mapping.Value.Accommodation, existing);

        var accommodation = mapping.Value.Accommodation;
        var units = (await client.GetRoomsAsync(accommodation, ct)).Where(room => room.IsRoom).ToList();
        var name = OctorateContentMapper.UnitName(property, document);
        string product;
        if (units.FirstOrDefault(unit => string.Equals(unit.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) is { } made)
        {
            product = made.Id;
        }
        else if (await scenarios.GetScenarioAsync(orgId, property.Id, ct) == OctorateInventoryScenario.NewInventory)
        {
            var rooms = await db.PropertyRooms.AsNoTracking().Where(r => r.PropertyId == property.Id).ToListAsync(ct);
            product = await client.CreateRoomAsync(accommodation, OctorateContentMapper.RoomCreate(property, document, rooms), ct);
        }
        else if (units.Count == 1)
        {
            product = units[0].Id;
        }
        else
        {
            throw new DomainRuleException(ListingChannelErrors.RoomRequired, "ListingChannelErrorRoomRequired");
        }

        await mappings.SetRoomAsync(orgId, accommodation, product, userId, ct, roles);
        return (accommodation, product);
    }

    /// <summary>The groups of the content of the unit, one operation each. The first refusal stops the run: the content is shared by the channels.</summary>
    private async Task PushGroupsAsync(Property property, SitePropertyProfileDocument document, string accommodation, string product, CancellationToken ct)
    {
        await client.UpdatePropertyAsync(accommodation, OctorateContentMapper.PropertyUpdate(property), ct);

        var descriptions = OctorateContentMapper.Descriptions(property, document);
        if (!descriptions.IsEmpty)
            await client.UpdateDescriptionsAsync(accommodation, product, descriptions, ct);

        var storedRooms = await db.PropertyRooms.AsNoTracking().Where(r => r.PropertyId == property.Id).ToListAsync(ct);
        var rooms = OctorateContentMapper.Rooms(storedRooms);
        if (rooms.Count > 0)
        {
            // POST adds a room: rooms already on the unit as they should be are not sent again.
            var existing = await client.GetContentRoomsAsync(accommodation, product, ct);
            if (OctorateContentReader.RoomsForm(existing) != OctorateContentReader.RoomsForm(rooms))
                await client.UpdateRoomsAsync(accommodation, product, rooms, ct);
        }

        if (options.Value.SendAmenities)
        {
            var amenities = AmenityCatalogMapper.OctorateIds(property.Amenities);
            if (amenities.Count > 0)
                await client.UpdateAmenitiesAsync(accommodation, product, amenities, ct);
        }

        var rules = OctorateContentMapper.ReservationRules(property);
        if (!rules.IsEmpty)
            await client.UpdateReservationRulesAsync(accommodation, product, rules, ct);

        if (options.Value.SendCancellationPolicy && OctorateContentMapper.CancellationPolicy(property) is { } policy)
            await client.UpdateCancellationPolicyAsync(accommodation, product, policy, ct);
    }

    /// <summary>The channels the property is connected to that no content rule covers are managed on the channel itself: their row says so and nothing is sent.</summary>
    private async Task UpsertManagedRowsAsync(Property property, IReadOnlyList<OctorateChannelConnection> connections, CancellationToken ct)
    {
        foreach (var connection in connections)
        {
            if (options.Value.PortalCodeOfChannelId(connection.ChannelId) is not null)
                continue;

            var code = ManagedChannelCode(connection);
            if (code is null || options.Value.RuleOf(code) is not null)
                continue;

            var row = await db.ListingChannelStates.FirstOrDefaultAsync(s => s.PropertyId == property.Id && s.ChannelCode == code, ct);
            if (row is null)
            {
                db.ListingChannelStates.Add(new ListingChannelState
                {
                    OrgId = property.OrgId,
                    PropertyId = property.Id,
                    ChannelCode = code,
                    Status = ChannelPublishStatus.ManagedOnChannel,
                });
            }
            else if (row.Status != ChannelPublishStatus.ManagedOnChannel)
            {
                row.Status = ChannelPublishStatus.ManagedOnChannel;
                row.ErrorCode = null;
                row.ErrorMessage = null;
            }
        }
    }

    /// <summary>The code of a managed channel: its name in lower case with letters and digits only (<c>expedia</c>); its id when it has no name.</summary>
    internal static string? ManagedChannelCode(OctorateChannelConnection connection)
    {
        var builder = new StringBuilder();
        foreach (var letter in (connection.ChannelName ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(letter))
                builder.Append(letter);
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }

        var code = builder.ToString().Trim('_');
        if (code.Length == 0 && !string.IsNullOrWhiteSpace(connection.ChannelId))
            code = $"channel_{connection.ChannelId.Trim()}";

        if (code.Length == 0)
            return null;

        return code.Length <= ListingChannelState.ChannelCodeMaxLength ? code : code[..ListingChannelState.ChannelCodeMaxLength];
    }

    /// <summary>A failure that stopped the run: the rows still waiting are left to fix with the code of the error, and the person is told.</summary>
    private async Task FailAsync(Property property, List<ListingChannelState> waiting, Exception error, CancellationToken ct)
    {
        if (waiting.Count == 0)
            return;

        var code = error switch
        {
            DomainException domain when ListingChannelErrors.MessageKey(domain.Code) is not null => domain.Code,
            OctorateException octorate => ListingChannelErrors.CodeOfOctorateError(octorate.Code),
            _ => ListingChannelErrors.ChannelPushFailed,
        };
        logger.LogWarning(
            "Listing publication of property {PropertyId} failed ({Error}): {Channels} channel(s) left to fix with {ErrorCode}",
            property.Id, error.GetType().Name, waiting.Count, code);

        foreach (var row in waiting)
            MarkToFix(row, code);

        await db.SaveChangesAsync(CancellationToken.None);
        await RaiseErrorsAsync(waiting, CancellationToken.None);
    }

    private async Task RaiseErrorsAsync(IEnumerable<ListingChannelState> rows, CancellationToken ct)
    {
        foreach (var row in rows)
            await todos.RaiseAsync(ActivityType.PublicationError, row, ActivityUrgency.Medium, row.ErrorCode ?? ListingChannelErrors.ChannelPushFailed, ct);
    }

    private static void MarkToFix(ListingChannelState row, string errorCode)
    {
        row.Status = ChannelPublishStatus.ToFix;
        row.ErrorCode = errorCode;
        row.ErrorMessage = ListingChannelErrors.ItalianMessage(errorCode);
    }

    /// <summary>
    /// The rows of the requested channels, put in <c>Sending</c> now: the run decides from the channels it was queued for, not from the state it finds
    /// the rows in. A publication queued while an earlier run of the same property was going on finds its rows back in <c>Accepted</c> or <c>ToFix</c>
    /// (the earlier run writes the state it ends with over the <c>Sending</c> the newer request put), and would otherwise do nothing: what the host
    /// changed meanwhile would never be pushed. The row exists already when the endpoint made it; a run started without its endpoint makes it.
    /// </summary>
    private async Task<List<ListingChannelState>> RowsForRunAsync(Property property, IReadOnlyList<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0)
            return [];

        var rows = await db.ListingChannelStates.Where(s => s.PropertyId == property.Id && codes.Contains(s.ChannelCode)).ToListAsync(ct);
        var changed = false;
        foreach (var code in codes)
        {
            var row = rows.FirstOrDefault(r => r.ChannelCode == code);
            if (row is null)
            {
                row = new ListingChannelState { OrgId = property.OrgId, PropertyId = property.Id, ChannelCode = code, Status = ChannelPublishStatus.Sending };
                db.ListingChannelStates.Add(row);
                rows.Add(row);
                changed = true;
            }
            else if (row.Status != ChannelPublishStatus.Sending)
            {
                row.Status = ChannelPublishStatus.Sending;
                row.ErrorCode = null;
                row.ErrorMessage = null;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);

        return rows;
    }

    /// <summary>Puts the requested channels in <c>Sending</c> in one save; two publications of the same property end on one row each, whichever writes first.</summary>
    private async Task<IReadOnlyList<ListingChannelResult>> MarkSendingAsync(Property property, IReadOnlyList<string> codes, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var rows = await db.ListingChannelStates.Where(s => s.PropertyId == property.Id && codes.Contains(s.ChannelCode)).ToListAsync(ct);
            foreach (var code in codes)
            {
                var row = rows.FirstOrDefault(r => r.ChannelCode == code);
                if (row is null)
                {
                    row = new ListingChannelState { OrgId = property.OrgId, PropertyId = property.Id, ChannelCode = code };
                    db.ListingChannelStates.Add(row);
                    rows.Add(row);
                }

                row.Status = ChannelPublishStatus.Sending;
                row.ErrorCode = null;
                row.ErrorMessage = null;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return [.. rows.OrderBy(r => r.ChannelCode, StringComparer.Ordinal).Select(r => new ListingChannelResult(r.ChannelCode, r.Status, r.ErrorCode))];
            }
            catch (DbUpdateException ex) when (attempt == 0 && OrgTeamAccess.IsUniqueViolation(ex, ChannelRowIndexName))
            {
                // Another publication made the same row first: forget the rows this one added and update the existing ones.
                foreach (var entry in db.ChangeTracker.Entries<ListingChannelState>().Where(e => e.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>The accommodation and the room of the confirmed mapping of the property (in any environment of the org), if there is one.</summary>
    private async Task<(string Accommodation, string? Product)?> ConfirmedMappingAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var row = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new { m.OctorateAccommodationId, m.OctorateProductId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.OctorateAccommodationId, row.OctorateProductId);
    }

    /// <summary>The channels of a request: trimmed, lower case, distinct, each a content channel of <see cref="ListingChannelOptions"/>; none is a 422, never «all».</summary>
    private List<string> ValidChannels(IReadOnlyList<string> channels)
    {
        var codes = channels
            .Select(c => c?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            throw new DomainRuleException("listing_channels_required", "ListingChannelsRequired");

        var unknown = codes.Where(c => c.Length > ListingChannelState.ChannelCodeMaxLength || options.Value.RuleOf(c) is null).ToList();
        if (unknown.Count > 0)
        {
            throw new DomainRuleException("listing_channel_unknown", "ListingChannelUnknown")
            {
                Extensions = new Dictionary<string, object?> { ["channels"] = unknown },
            };
        }

        return codes;
    }

    private static NotFoundException PropertyNotFound() =>
        new("Property not found for the Octorate publication.") { Code = "octorate_property_not_found", MessageKey = "OctoratePropertyNotFound" };

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
