using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed record MarketingCategoryEdit(string[] Interests, string[] DismissedInterests, string[]? TripType);
public sealed record MarketingCategories(string[] TripType, string Loyalty, decimal Spend, string? SpendBand, int? DaysSinceLastStay,
    string[] Period, string[] Channels, double? AverageStayNights, double? AverageLeadDays, string[] Interests,
    string[] SuggestedInterests, string[] DismissedInterests, string[]? TripTypeOverride);

public sealed class MarketingGuestCategoryService(AppDbContext db, ICrmProfileReadService profiles, IAiProvider ai,
    IFeatureFlags flags, MarketingAiQuota quota, TimeProvider clock)
{
    private static readonly string[] TripTypes = ["single", "family", "couple", "group", "business", "withPets"];

    // Only the keys of this dictionary and their counts may leave CasaZen. The raw text stays in the request's memory.
    private static readonly Dictionary<string, string[]> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["music"] = ["music", "musica", "concerto", "concert", "jazz", "musik"],
        ["cinema"] = ["cinema", "film", "kino"],
        ["sport"] = ["sport", "tennis", "cycling", "ciclismo", "sci", "ski", "marathon", "maratona"],
        ["food-and-wine"] = ["gastronomia", "enogastronomia", "degustazione", "wine", "vino", "cucina"],
        ["nature"] = ["nature", "natura", "wandern", "hiking", "trekking", "escursione"],
        ["art"] = ["art", "arte", "museum", "museo", "kunst"],
    };

    private async Task<GuestCrmProfile> ProfileAsync(HostScope scope, Guid id, CancellationToken ct)
    {
        var profile = await db.GuestCrmProfiles.SingleOrDefaultAsync(p => p.OrgId == scope.OrgId && p.Id == id && p.MergedIntoProfileId == null, ct);
        if (profile != null && (scope.IsOrgWide || await profiles.ProfilesWhollyInScope(scope).AnyAsync(p => p.Id == id, ct)))
            return profile;
        if (profile != null && await db.GuestContacts.AnyAsync(c => c.OrgId == scope.OrgId && c.GuestCrmProfileId == id && c.Origin == GuestContactOrigin.Imported, ct)
            && !await db.Guests.AnyAsync(g => g.OrgId == scope.OrgId && g.GuestCrmProfileId == id, ct))
        {
            var ids = JsonSerializer.Deserialize<Guid[]>(profile.PropertyIdsJson) ?? [];
            if (ids.Length > 0 && await db.Properties.Where(p => p.OrgId == scope.OrgId && ids.Contains(p.Id)).InScope(scope).CountAsync(ct) == ids.Length)
                return profile;
        }
        throw new NotFoundException("CRM profile not found") { Code = "crm_profile_not_found", MessageKey = "CrmProfileNotFound" };
    }

    public async Task<MarketingCategories> ReadAsync(HostScope scope, Guid id, CancellationToken ct)
    {
        var profile = await ProfileAsync(scope, id, ct);
        var settings = await db.MarketingStudioSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrgId == scope.OrgId, ct);
        var choices = MarketingGuestCategories.Choices(profile);
        var today = clock.GetUtcNow().UtcDateTime.Date;
        var bookings = await profiles.BookingsInScope(scope).Where(b => b.Guest.GuestCrmProfileId == id)
            .Select(b => new { b.Status, b.CheckInDate, b.CheckOutDate, b.TotalPrice, b.Source, b.CreatedAt, b.NumberOfAdults, b.NumberOfChildren, b.NumberOfGuests })
            .ToListAsync(ct);
        var stays = bookings.Where(b => GuestMatchRules.IsStay(b.Status, b.CheckOutDate, today)).ToList();
        var stayCount = bookings.Count == 0 ? profile.TotalStays : stays.Count;
        var last = stays.Count > 0 ? stays.Max(s => s.CheckOutDate) : stayCount > 0 ? profile.LastStayAt : (DateTime?)null;
        var spend = stays.Count > 0 ? stays.Sum(s => s.TotalPrice) : bookings.Count == 0 ? profile.TotalSpend : 0m;
        var period = stays.Select(s => MarketingGuestCategories.Season(s.CheckInDate.Month)).ToList();
        if (period.Count == 0 && last.HasValue) period.Add(MarketingGuestCategories.Season(last.Value.Month));
        var ranked = period.GroupBy(p => p).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var preferred = ranked.TakeWhile(g => g.Count() == ranked[0].Count()).Select(g => g.Key).ToArray();
        var trip = stays.Select(s => s.NumberOfChildren > 0 ? "family" : s.NumberOfAdults == 2 ? "couple"
            : s.NumberOfAdults >= 3 || s.NumberOfGuests >= 3 ? "group" : "single").Distinct().ToArray();
        var interests = choices.Interests;
        var peers = profiles.ProfilesWhollyInScope(scope).Where(p => p.TotalSpend > 0);
        string? spendBand = null;
        var peerCount = spend > 0 ? await peers.CountAsync(ct) : 0;
        if (peerCount >= 3)
        {
            var lower = await peers.CountAsync(p => p.TotalSpend < spend, ct);
            spendBand = lower * 3 < peerCount ? "lower" : lower * 3 < peerCount * 2 ? "middle" : "upper";
        }
        return new(choices.TripType ?? trip, MarketingGuestCategories.Loyalty(stayCount, last, today, settings?.LoyalStays ?? 3, settings?.LostMonths ?? 24),
            spend, spendBand, last is { } date ? Math.Max(0, (today - date.Date).Days) : null, preferred,
            stays.Select(s => s.Source.ToString()).Distinct().ToArray(),
            stays.Count == 0 ? null : Math.Round(stays.Average(s => (s.CheckOutDate - s.CheckInDate).TotalDays), 1),
            stays.Select(s => (double?)CrmProfileRules.AdvanceDays(s.CreatedAt, s.CheckInDate)).Average(), interests,
            choices.SuggestedInterests.Except(choices.DismissedInterests, StringComparer.OrdinalIgnoreCase).Except(interests, StringComparer.OrdinalIgnoreCase).ToArray(),
            choices.DismissedInterests, choices.TripType);
    }

    public async Task EditAsync(HostScope scope, Guid id, MarketingCategoryEdit edit, CancellationToken ct)
    {
        var profile = await ProfileAsync(scope, id, ct);
        if (!CrmProfileRules.TryNormalizeTags(edit.Interests, out var interests)
            || !CrmProfileRules.TryNormalizeTags(edit.DismissedInterests, out var dismissed)
            || edit.TripType?.Any(t => !TripTypes.Contains(t)) == true)
            throw new DomainRuleException("marketing_invalid_categories", "CrmTagsInvalid");
        var current = MarketingGuestCategories.Choices(profile);
        Save(profile, current with { Interests = interests.ToArray(), DismissedInterests = dismissed.ToArray(), TripType = edit.TripType?.Distinct().ToArray() });
        await CommitAsync(ct);
    }

    public async Task<string[]> SuggestAsync(HostScope scope, Guid id, CancellationToken ct)
    {
        var profile = await ProfileAsync(scope, id, ct);
        if (!flags.IsEnabled(FeatureFlags.MarketingGuestInterestAi))
            throw new DomainForbiddenException("marketing_interest_ai_disabled", "Forbidden");
        var bookings = profiles.BookingsInScope(scope).Where(b => b.Guest.GuestCrmProfileId == id).Select(b => b.Id);
        var reviews = await db.Reviews.Where(r => r.OrgId == scope.OrgId && r.Status != ReviewStatus.Removed && bookings.Contains(r.BookingId))
            .OrderByDescending(r => r.PublishedAt).Select(r => r.Text).Take(50).ToListAsync(ct);
        var messages = await db.GuestMessages.Where(m => m.OrgId == scope.OrgId && m.Sender == GuestMessageSender.Guest && bookings.Contains(m.Thread.BookingId))
            .OrderByDescending(m => m.CreatedAt).Select(m => m.Text).Take(50).ToListAsync(ct);
        var requests = await db.ServiceRequests.Where(r => r.OrgId == scope.OrgId && r.BookingId.HasValue && bookings.Contains(r.BookingId.Value))
            .OrderByDescending(r => r.CreatedAt).Select(r => r.Notes).Take(50).ToListAsync(ct);
        var counts = Evidence(reviews.Concat(messages).Concat(requests));
        string[] suggested = [];
        if (counts.Count > 0)
        {
            await quota.ReserveAsync(scope.OrgId, ct);
            var prompt = JsonSerializer.Serialize(counts);
            var response = await ai.GenerateAsync(new AiRequest(prompt, [],
                "Select justified leisure interests from these aggregate mention counts. Return only a JSON array of the input keys, or [] when evidence is insufficient."),
                AiModelTier.Economy, "marketing-interests-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))), ct);
            if (!response.ProviderConfigured) throw new DomainRuleException("marketing_ai_unavailable", "marketing_ai_unavailable");
            suggested = MarketingStudio.Parse<string[]>(response.Content).Where(counts.ContainsKey).Distinct().ToArray();
        }
        await db.Entry(profile).ReloadAsync(ct);
        var current = MarketingGuestCategories.Choices(profile);
        suggested = suggested.Except(current.DismissedInterests, StringComparer.OrdinalIgnoreCase).Except(current.Interests, StringComparer.OrdinalIgnoreCase).ToArray();
        Save(profile, current with { SuggestedInterests = suggested });
        await CommitAsync(ct);
        return suggested;
    }

    internal static Dictionary<string, int> Evidence(IEnumerable<string?> texts)
    {
        var counts = new Dictionary<string, int>();
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var words = Regex.Matches(text, @"\p{L}+", RegexOptions.CultureInvariant).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (topic, vocabulary) in Topics)
                if (vocabulary.Any(words.Contains)) counts[topic] = counts.GetValueOrDefault(topic) + 1;
        }
        return counts;
    }

    private void Save(GuestCrmProfile profile, MarketingCategoryChoices choices)
    {
        profile.MarketingCategoryOverridesJson = JsonSerializer.Serialize(choices);
        profile.UpdatedAt = clock.GetUtcNow().UtcDateTime;
    }

    private async Task CommitAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainConflictException("marketing_categories_changed", "Marketing categories changed. Reload and retry.");
        }
    }
}
