using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IListingStatusService"/> (E02/F03/US01/T07, NF-09, NF-27, NF-30, NF-38, ADR-027, ADR-034): what state the listing of a property is in on
/// each channel, and the follow-up of a publication.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Read.</b> The completeness and the missing steps are worked out when read, from the rule of each channel and the data of the property
/// (the Italian and English texts, the photos, the rooms and beds, the address and comune, a valid CIN where the channel asks for it): they are never
/// stored, so they cannot go stale. The reason is the code of the row (a message in simple words), never a generic «error». A channel not confirmed
/// for more than 24 hours raises <c>syncAlert</c>. The CIN is not sent to any content channel until D2 is answered, and every content channel says so.</item>
/// <item><b>Follow-up.</b> An accepted row is not a published one: the process of the push (<c>GET .../process/{id}</c>) moves it to <c>Published</c>
/// or <c>ToFix</c> with Octorate's reason; a state CasaZen does not understand changes nothing. Every successful read sets <c>LastSyncedAt</c>. A photo
/// still waiting is read in the photo queue, and a photo a run left unsent is sent once it has waited <see cref="ListingPhotoSync.PendingGrace"/>.
/// Nothing here writes to Octorate but the sending of those photos and the removal of a photo that left the gallery.</item>
/// <item>The web layer decides who may read a property (<c>property.read</c> and the row check); the lists narrow by <c>InScope</c>, so a
/// collaborator «Solo alcuni» sees only the properties it reaches.</item>
/// </list>
/// </remarks>
public sealed class ListingStatusService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    ListingPhotoSync photoSync,
    PublicPhotoUrlService publicPhotos,
    ListingTodos todos,
    IOptions<ListingChannelOptions> options,
    TimeProvider timeProvider,
    ILogger<ListingStatusService> logger) : IListingStatusService
{
    /// <summary>How long a pushed channel may go without a confirmation before the status raises the alert (ADR-034-R3).</summary>
    public static readonly TimeSpan SyncAlertAfter = TimeSpan.FromHours(24);

    public async Task<ListingStatusView?> GetAsync(Guid propertyId, CancellationToken ct)
    {
        var property = await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, ct);
        if (property is null)
            return null;

        var rows = await db.ListingChannelStates
            .AsNoTracking()
            .Where(s => s.PropertyId == propertyId)
            .OrderBy(s => s.ChannelCode)
            .ToListAsync(ct);
        var rooms = await db.PropertyRooms.AsNoTracking().Where(r => r.PropertyId == propertyId).ToListAsync(ct);

        var facts = FactsOf(property, rooms);
        var now = Now();
        var channels = rows.Select(row => StatusOf(row, facts, now)).ToList();
        return new ListingStatusView(
            propertyId,
            channels,
            [.. rows.Where(row => row.Status != ChannelPublishStatus.ManagedOnChannel).Select(row => row.ChannelCode)]);
    }

    public async Task<IReadOnlyList<ListingPropertySummary>> GetSummaryAsync(HostScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var rows = await (from state in db.ListingChannelStates.AsNoTracking()
                          join property in db.Properties.AsNoTracking().InScope(scope) on state.PropertyId equals property.Id
                          where state.OrgId == scope.OrgId
                          select new { state.PropertyId, state.Status, state.LastSyncedAt })
            .ToListAsync(ct);

        var now = Now();
        return
        [
            .. rows
                .GroupBy(row => row.PropertyId)
                .Select(group => new ListingPropertySummary(
                    group.Key,
                    group.Select(row => row.Status).MinBy(Severity),
                    group.Any(row => IsStale(row.Status, row.LastSyncedAt, now))))
                .OrderBy(summary => summary.PropertyId),
        ];
    }

    public async Task<int> SyncPropertyAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var accommodation = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => m.OctorateAccommodationId)
            .FirstOrDefaultAsync(ct);
        if (accommodation is null)
            return 0;

        var rows = await db.ListingChannelStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(s => s.OrgId == orgId
                        && s.PropertyId == propertyId
                        && (s.Status == ChannelPublishStatus.Sending || s.Status == ChannelPublishStatus.Accepted))
            .ToListAsync(ct);
        // The photos waiting in the queue or to be removed, and the ones a run that stopped left unsent (those a publication is not still sending).
        var unsentBefore = Now() - ListingPhotoSync.PendingGrace;
        var tasks = await db.PhotoUploadTasks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(t => t.OrgId == orgId
                        && t.PropertyId == propertyId
                        && (t.Status == PhotoUploadStatus.Uploading
                            || t.Status == PhotoUploadStatus.RemovePending
                            || (t.Status == PhotoUploadStatus.Pending && t.CreatedAt < unsentBefore)))
            .ToListAsync(ct);

        var changed = 0;
        using (orgScope.Use(orgId))
        {
            foreach (var row in rows)
                changed += await FollowProcessAsync(row, accommodation, ct);

            if (tasks.Count > 0)
            {
                changed += await photoSync.ReadQueueAsync(tasks, accommodation, ct);

                // A photo a run left unsent is sent now: the publication the host asked for is finished. A property that was deleted is left alone.
                if (tasks.Any(t => t.Status == PhotoUploadStatus.Pending)
                    && await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, ct) is { } property)
                {
                    changed += await photoSync.ResumeAsync(property, tasks, accommodation, ct);
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Reads the process the row waits on and moves it: done is <c>Published</c>, failed is <c>ToFix</c> with the reason, still going on or not
    /// understood changes nothing but the time of the read. A row with no process to follow is left as it is.
    /// </summary>
    private async Task<int> FollowProcessAsync(ListingChannelState row, string accommodation, CancellationToken ct)
    {
        var baseline = ListingChannelBaseline.Parse(row.ContentHashesJson);
        if (baseline.ProcessId is not { } processId)
            return 0;

        OctorateContentProcess process;
        try
        {
            process = await client.GetContentProcessAsync(accommodation, processId, ct);
        }
        catch (OctorateNotFoundException)
        {
            logger.LogWarning("Listing status of property {PropertyId}, channel {ChannelCode}: Octorate does not know the process", row.PropertyId, row.ChannelCode);
            return 0;
        }

        var now = Now();
        switch (process.State)
        {
            case OctorateProcessState.Done:
                row.Status = ChannelPublishStatus.Published;
                row.ErrorCode = null;
                row.ErrorMessage = null;
                row.LastSyncedAt = now;
                row.ContentHashesJson = baseline.WithProcess(null).ToJson();
                await db.SaveChangesAsync(ct);
                await todos.CloseAsync(ActivityType.PublicationError, row, InternalActivity.ClosedBySystem, ct);
                return 1;

            case OctorateProcessState.Failed:
                row.Status = ChannelPublishStatus.ToFix;
                row.ErrorCode = ListingChannelErrors.ChannelRefused;
                row.ErrorMessage = Cut(process.Reason ?? ListingChannelErrors.ItalianMessage(ListingChannelErrors.ChannelRefused), ListingChannelState.ErrorMessageMaxLength);
                row.LastSyncedAt = now;
                row.ContentHashesJson = baseline.WithProcess(null).ToJson();
                await db.SaveChangesAsync(ct);
                await todos.RaiseAsync(ActivityType.PublicationError, row, ActivityUrgency.Medium, ListingChannelErrors.ChannelRefused, ct);
                return 1;

            case OctorateProcessState.Pending:
                // The read worked: the channel manager answered, the channel has not decided yet.
                row.LastSyncedAt = now;
                await db.SaveChangesAsync(ct);
                return 0;

            default:
                // An answer CasaZen does not understand is never read as a success, and it confirms nothing.
                logger.LogWarning("Listing status of property {PropertyId}, channel {ChannelCode}: the state of the process is not understood", row.PropertyId, row.ChannelCode);
                return 0;
        }
    }

    private ListingChannelStatus StatusOf(ListingChannelState row, ListingFacts facts, DateTime now)
    {
        var rule = options.Value.RuleOf(row.ChannelCode);
        var managed = row.Status == ChannelPublishStatus.ManagedOnChannel || rule is null;
        var baseline = ListingChannelBaseline.Parse(row.ContentHashesJson);

        // What the rule of the channel asks for, each with the step that owns it.
        var checks = new List<(bool Ok, string Step)>();
        if (!managed)
        {
            checks.Add((facts.ItalianTexts, ListingStatusSteps.Descriptions));
            checks.Add((facts.EnglishTexts, ListingStatusSteps.Descriptions));
            checks.Add((facts.PublicPhotos >= rule!.MinPhotos, ListingStatusSteps.Photos));
            checks.Add((facts.RoomsWithBeds, ListingStatusSteps.Base));
            checks.Add((facts.AddressAndComune, ListingStatusSteps.Base));
            if (rule.RequiresCin)
                checks.Add((facts.ValidCin, ListingStatusSteps.Base));
        }

        var completeness = checks.Count == 0 ? 100 : (int)Math.Round(100.0 * checks.Count(c => c.Ok) / checks.Count, MidpointRounding.AwayFromZero);
        var missingSteps = checks.Where(c => !c.Ok).Select(c => c.Step).ToHashSet(StringComparer.Ordinal);
        if (!managed)
        {
            if (!facts.Amenities)
                missingSteps.Add(ListingStatusSteps.Amenities);
            if (!facts.Rules)
                missingSteps.Add(ListingStatusSteps.Rules);
            if (!facts.Price)
                missingSteps.Add(ListingStatusSteps.Prices);
        }

        string[] order =
        [
            ListingStatusSteps.Base, ListingStatusSteps.Photos, ListingStatusSteps.Descriptions,
            ListingStatusSteps.Amenities, ListingStatusSteps.Rules, ListingStatusSteps.Prices,
        ];

        return new ListingChannelStatus(
            row.ChannelCode,
            row.Status,
            completeness,
            [.. order.Where(missingSteps.Contains)],
            row.LastSyncedAt,
            row.LastSyncedAt is { } at ? Math.Max(0, (long)(now - at).TotalSeconds) : null,
            IsStale(row.Status, row.LastSyncedAt, now),
            row.ErrorCode,
            row.ErrorCode == ListingChannelErrors.ChannelRefused ? row.ErrorMessage : null,
            row.ErrorCode == ListingChannelErrors.PhotosBelowMinimum ? Math.Max(rule?.MinPhotos ?? 1, 1) : null,
            row.ExternalChangeAt is not null ? baseline.ChangedGroups : []);
    }

    private ListingFacts FactsOf(Property property, IReadOnlyList<PropertyRoom> rooms)
    {
        var document = SitePropertyProfileJson.Parse(property.SiteProfileJson);
        var missingTexts = LanguageMapper.ValidateForPublish(property, document);
        return new ListingFacts(
            ItalianTexts: !missingTexts.Any(key => key.StartsWith(ListingLanguages.Italian + ".", StringComparison.Ordinal)),
            EnglishTexts: !missingTexts.Any(key => key.StartsWith(ListingLanguages.English + ".", StringComparison.Ordinal)),
            PublicPhotos: publicPhotos.ListingPhotoUrls(property).Count,
            RoomsWithBeds: rooms.Any(room => room.Beds.Count > 0),
            AddressAndComune: !string.IsNullOrWhiteSpace(property.Address) && !string.IsNullOrWhiteSpace(property.City),
            ValidCin: CinFormat.IsValid(property.CinCode),
            Amenities: property.Amenities.Count > 0,
            Rules: !string.IsNullOrWhiteSpace(property.HouseRules) || property.ChildrenPolicy is not null || property.PetsPolicy is not null || property.SmokingPolicy is not null,
            Price: property.NightlyRate > 0m);
    }

    /// <summary>A channel that was pushed and that the channel manager has not confirmed for more than 24 hours (or never).</summary>
    private static bool IsStale(ChannelPublishStatus status, DateTime? lastSyncedAt, DateTime now) =>
        status is ChannelPublishStatus.Accepted or ChannelPublishStatus.Published
        && (lastSyncedAt is null || now - lastSyncedAt.Value > SyncAlertAfter);

    /// <summary>How much a state needs the attention of the host: the lower the number, the worse the state.</summary>
    internal static int Severity(ChannelPublishStatus status) => status switch
    {
        ChannelPublishStatus.ToFix => 0,
        ChannelPublishStatus.Sending => 1,
        ChannelPublishStatus.Accepted => 2,
        ChannelPublishStatus.Draft => 3,
        ChannelPublishStatus.Published => 4,
        _ => 5,
    };

    private static string? Cut(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private sealed record ListingFacts(
        bool ItalianTexts,
        bool EnglishTexts,
        int PublicPhotos,
        bool RoomsWithBeds,
        bool AddressAndComune,
        bool ValidCin,
        bool Amenities,
        bool Rules,
        bool Price);
}
