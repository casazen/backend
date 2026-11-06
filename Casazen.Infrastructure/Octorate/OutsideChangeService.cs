using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOutsideChangeService"/> (E02/F03/US01/T07, NF-27, ADR-027-R1, R2, R4): finds the changes made on Octorate after CasaZen's last push and
/// applies the person's choice about them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Detection never writes to Octorate.</b> It re-reads the groups of the content (<see cref="OctorateContentReader"/>) and compares their hashes
/// with the baseline stored after the last accepted push. A difference sets <c>ExternalChangeAt</c> (when it is empty), keeps the groups that changed
/// and raises one «To do» of the kind <see cref="ActivityType.ListingChangedOutside"/> for the property and channel. A channel being published
/// (<c>Sending</c>) is not compared: its content is moving. Only the groups with a baseline can be compared: the rules of the stay, the prices and the
/// discounts can be written but no read returns them, so a change made on them outside CasaZen is not seen. The same read confirms the <c>Published</c>
/// channels of the property (<c>LastSyncedAt</c>, the clock of the 24-hour alert): it is the only recurring read of a channel that has no process to
/// follow any more.</item>
/// <item><b>Nothing is overwritten without a choice.</b> A publication over a flagged channel is refused until <c>keep</c> or <c>take</c> (the guard of
/// <see cref="IContentPushService.PublishAsync"/>). <c>keep</c> publishes the channel again with <c>overwrite</c>: the push stores the new baseline,
/// clears the change and closes the «To do». <c>take</c> adopts the descriptions the channel manager holds: the Italian description and house rules
/// into the columns of the property (the note on the rules, which the house rules sent to the channel already include, is dropped from the profile so it
/// is not said twice), the other languages and the directions into the site profile through <see cref="IPropertySiteProfileService"/> as the person's
/// own texts; then the baseline is read again, the change cleared and the «To do» closed. Only the descriptions can be taken: for any other group
/// <c>take</c> answers 422 <c>outside_change_take_not_supported</c> with the groups, and CasaZen keeps its version of them (<c>keep</c>). A text longer
/// than the column of the property is refused (422), never cut.</item>
/// </list>
/// </remarks>
public sealed class OutsideChangeService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    OctorateContentReader reader,
    IContentPushService push,
    IPropertySiteProfileService profiles,
    IHostScopeResolver hostScopeResolver,
    ListingTodos todos,
    TimeProvider timeProvider,
    ILogger<OutsideChangeService> logger) : IOutsideChangeService
{
    private static readonly IReadOnlySet<string> NoTokenRoles = new HashSet<string>(StringComparer.Ordinal);

    private static readonly string[] ItalianKeys = ["IT"];

    public async Task<IReadOnlyList<string>> DetectAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var rows = await db.ListingChannelStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(s => s.OrgId == orgId
                        && s.PropertyId == propertyId
                        && (s.Status == ChannelPublishStatus.Accepted || s.Status == ChannelPublishStatus.Published))
            .ToListAsync(ct);

        // What the read can tell about a row: one with a baseline is compared with it, and a published one is confirmed by it (below).
        var compared = rows.Where(row => ListingChannelBaseline.Parse(row.ContentHashesJson).HasBaseline).ToList();
        var published = rows.Where(row => row.Status == ChannelPublishStatus.Published).ToList();
        if (compared.Count == 0 && published.Count == 0)
            return [];

        var listing = await MappingAsync(orgId, propertyId, ct);
        if (listing?.Product is not { } product)
            return [];

        IReadOnlyDictionary<string, string> current;
        using (orgScope.Use(orgId))
            current = await reader.ReadAsync(listing.Value.Accommodation, product, ct);

        // The channel manager answered for the listing: that is the confirmation of a published channel (a published row has no process left to
        // read, so nothing else ever refreshes it; without this every healthy channel would go «silent» after 24 hours). An accepted row is
        // confirmed only by the process of its push (ListingStatusService), so an answer CasaZen does not understand still confirms nothing.
        var now = Now();
        foreach (var row in published)
            row.LastSyncedAt = now;

        var flagged = new List<ListingChannelState>();
        var groups = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in compared)
        {
            var baseline = ListingChannelBaseline.Parse(row.ContentHashesJson);
            var differences = baseline.Differences(current);
            if (differences.Count == 0)
                continue;

            row.ExternalChangeAt ??= now;
            row.ContentHashesJson = baseline.WithChanged(differences).ToJson();
            flagged.Add(row);
            groups.UnionWith(differences);
        }

        await db.SaveChangesAsync(ct);
        if (flagged.Count == 0)
            return [];

        foreach (var row in flagged)
            await todos.RaiseAsync(ActivityType.ListingChangedOutside, row, ActivityUrgency.Medium, "listing_changed_outside", ct);

        logger.LogInformation(
            "Listing of property {PropertyId}: a change made outside CasaZen was found on {Channels} channel(s), groups {Groups}",
            propertyId, flagged.Count, string.Join(',', groups));
        return [.. groups];
    }

    public async Task ResolveAsync(
        Guid propertyId,
        string channelCode,
        OutsideChangeChoice choice,
        string userId,
        CancellationToken ct,
        IReadOnlySet<string>? tokenRoles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var roles = tokenRoles ?? NoTokenRoles;
        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, ct);
        if (property is null || !await hostScopeResolver.CanReachPropertyAsync(userId, roles, property.OrgId, property.Id, property.OwnerId, ct))
            throw PropertyNotFound();

        var code = channelCode?.Trim().ToLowerInvariant();
        var row = string.IsNullOrEmpty(code)
            ? null
            : await db.ListingChannelStates.FirstOrDefaultAsync(s => s.PropertyId == propertyId && s.ChannelCode == code, ct);
        if (row is null)
        {
            throw new NotFoundException("Channel of the listing not found.")
            {
                Code = "listing_channel_not_found",
                MessageKey = "ListingChannelNotFound",
            };
        }

        if (row.ExternalChangeAt is null)
            throw new DomainConflictException("outside_change_not_found", "OutsideChangeNotFound");

        if (choice == OutsideChangeChoice.Keep)
        {
            // CasaZen's version goes over the change; the push stores the new baseline, clears the change and closes the «To do».
            await push.PublishAsync(propertyId, [row.ChannelCode], overwrite: true, userId, ct, roles);
            return;
        }

        await TakeAsync(property, row, ct);
    }

    private async Task TakeAsync(Property property, ListingChannelState row, CancellationToken ct)
    {
        var baseline = ListingChannelBaseline.Parse(row.ContentHashesJson);
        var unsupported = baseline.ChangedGroups.Where(group => group != ListingContentGroups.Descriptions).ToList();
        if (unsupported.Count > 0)
        {
            throw new DomainRuleException("outside_change_take_not_supported", "OutsideChangeTakeNotSupported")
            {
                Extensions = new Dictionary<string, object?> { ["groups"] = unsupported },
            };
        }

        var listing = await MappingAsync(property.OrgId, property.Id, ct);
        if (listing?.Product is not { } product)
            throw new DomainRuleException("octorate_mapping_not_confirmed", "OctorateMappingNotConfirmed");

        OctorateDescriptions? held;
        IReadOnlyDictionary<string, string> current;
        using (orgScope.Use(property.OrgId))
        {
            held = await client.GetDescriptionsAsync(listing.Value.Accommodation, product, ct);
            current = await reader.ReadAsync(listing.Value.Accommodation, product, ct);
        }

        if (held is not null)
            await AdoptAsync(property, held, ct);

        row.ExternalChangeAt = null;
        row.ContentHashesJson = new ListingChannelBaseline(current, baseline.ProcessId, []).ToJson();
        await db.SaveChangesAsync(ct);
        await todos.CloseAsync(ActivityType.ListingChangedOutside, row, InternalActivity.ClosedBySystem, ct);

        logger.LogInformation("Listing of property {PropertyId}, channel {ChannelCode}: the descriptions of the channel manager were taken", property.Id, row.ChannelCode);
    }

    /// <summary>
    /// The descriptions of the channel manager become the texts of the listing: Italian into the columns, the other languages and the directions into
    /// the site profile. Everything is checked before anything is written, so a text that does not fit changes nothing.
    /// </summary>
    private async Task AdoptAsync(Property property, OctorateDescriptions held, CancellationToken ct)
    {
        var italianDescription = held.Description.GetValueOrDefault("IT");
        var italianRules = held.HouseRules.GetValueOrDefault("IT");
        if (italianDescription is { } description && description.Length > 2000)
            throw TextTooLong("description");
        if (italianRules is { } rules && rules.Length > 1000)
            throw TextTooLong("houseRules");

        var view = await profiles.GetAsync(property.Id, ct) ?? throw PropertyNotFound();
        var translations = view.Translations.ToDictionary(
            entry => entry.Key,
            entry => new ListingTextsInput(
                entry.Value.Title, entry.Value.Description, entry.Value.Spaces, entry.Value.Access,
                entry.Value.Neighborhood, entry.Value.Transport, entry.Value.Rules),
            StringComparer.Ordinal);

        foreach (var (octorateLanguage, text) in held.Description.Where(entry => !ItalianKeys.Contains(entry.Key)))
        {
            if (LanguageMapper.FromOctorate(octorateLanguage) is { } language)
                translations[language] = translations.GetValueOrDefault(language) is { } known ? known with { Description = text } : new ListingTextsInput(Description: text);
        }

        foreach (var (octorateLanguage, text) in held.HouseRules.Where(entry => !ItalianKeys.Contains(entry.Key)))
        {
            if (LanguageMapper.FromOctorate(octorateLanguage) is { } language)
                translations[language] = translations.GetValueOrDefault(language) is { } known ? known with { Rules = text } : new ListingTextsInput(Rules: text);
        }

        if (!string.IsNullOrWhiteSpace(held.Directions))
        {
            var italian = ListingLanguages.Italian;
            translations[italian] = translations.GetValueOrDefault(italian) is { } known ? known with { Transport = held.Directions } : new ListingTextsInput(Transport: held.Directions);
        }

        // The house rules sent to the channel already hold the note on the rules; adopted back, the note would be said twice.
        await profiles.SaveAsync(property.Id, ToInput(view, translations, dropRulesNote: italianRules is not null), ct);

        if (italianDescription is not null)
            property.Description = italianDescription;
        if (italianRules is not null)
            property.HouseRules = italianRules;

        property.UpdatedAt = Now();
        await db.SaveChangesAsync(ct);
    }

    private static SitePropertyProfileInput ToInput(SitePropertyProfileView view, IReadOnlyDictionary<string, ListingTextsInput> translations, bool dropRulesNote) =>
        new(
            view.FloorLevel,
            view.RenovatedYear,
            view.EnergyClass,
            view.EnergyIndex,
            view.AreaKind,
            view.ChildrenWelcome,
            view.CheckInTo,
            view.VideoUrl,
            view.TourUrl,
            Text(view.Zone),
            Text(view.Near),
            Text(view.FloorNote),
            Text(view.OutdoorNote),
            Text(view.CheckInMethod),
            dropRulesNote ? null : Text(view.RulesNote),
            [.. view.Highlights.Select(h => new SiteHighlightInput(h.Icon, Text(h.Title), Text(h.Text)))],
            [.. view.SafetyNotes.Select(note => Text(note)!)],
            view.Tags,
            translations);

    private static LocalizedTextInput? Text(LocalizedText? text) =>
        text is null
            ? null
            : new LocalizedTextInput(text.It, text.En, text.Others?.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal));

    /// <summary>The accommodation and the room of the confirmed mapping of the property.</summary>
    private async Task<(string Accommodation, string? Product)?> MappingAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        var row = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && m.PropertyId == propertyId && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new { m.OctorateAccommodationId, m.OctorateProductId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.OctorateAccommodationId, row.OctorateProductId);
    }

    private static DomainRuleException TextTooLong(string field) =>
        new("outside_change_text_too_long", "OutsideChangeTextTooLong")
        {
            Extensions = new Dictionary<string, object?> { ["field"] = field },
        };

    private static NotFoundException PropertyNotFound() =>
        new("Property not found for the change made outside CasaZen.") { Code = "octorate_property_not_found", MessageKey = "OctoratePropertyNotFound" };

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
