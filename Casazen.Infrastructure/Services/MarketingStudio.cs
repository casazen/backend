using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public record NewsletterPlan(Guid? SegmentId = null, Guid? PropertyId = null, int CadenceDays = 30, string Instructions = "", bool Enabled = false);
public record StudioSettings(string Voice, Dictionary<Guid, string> PropertyNotes, Dictionary<Guid, string> PropertyUrls,
    int MonthlyEmailLimit, int LoyalStays, int LostMonths, NewsletterPlan Newsletter, bool CanEdit = true);
public record StudioBrief(Guid PropertyId, Guid SegmentId, string Channel, string Brief, string? EventName = null);
public record EventRequest(Guid PropertyId, DateOnly From, DateOnly To);
public record LocalEvent(string Name, DateOnly Date, string Source, string Description);
public record StudioCopy(string Subject, string Body, string Keywords, string Audience, string Budget);
public record StudioEdit(string Subject, string Body);

public sealed partial class MarketingStudio(AppDbContext db, IAiProvider ai, IWebSearchClient web, PublicOrgSiteUrls sites,
    MarketingAiQuota quota, IConfiguration configuration, ICampaignService campaigns, TimeProvider clock)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly string[] Channels = ["email", "newsletter", "instagram", "facebook", "linkedin",
        "google-ads", "meta-ads", "linkedin-ads", "microsoft-ads", "chatgpt-ads"];

    public IQueryable<MarketingAsset> Assets(HostScope scope) =>
        db.MarketingAssets.Where(a => a.OrgId == scope.OrgId && db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Any(p => p.Id == a.PropertyId));

    public async Task<StudioSettings> SettingsAsync(HostScope scope, CancellationToken ct)
    {
        var row = await db.MarketingStudioSettings.SingleOrDefaultAsync(s => s.OrgId == scope.OrgId, ct)
            ?? new MarketingStudioSettings { OrgId = scope.OrgId };
        var notes = JsonSerializer.Deserialize<Dictionary<Guid, string>>(row.PropertyNotesJson, Json)!;
        var urls = JsonSerializer.Deserialize<Dictionary<Guid, string>>(row.PropertyUrlsJson, Json)!;
        var newsletter = JsonSerializer.Deserialize<NewsletterPlan>(row.NewsletterJson, Json) ?? new();
        if (!scope.IsOrgWide)
        {
            var allowed = (await db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
            notes = notes.Where(x => allowed.Contains(x.Key)).ToDictionary();
            urls = urls.Where(x => allowed.Contains(x.Key)).ToDictionary();
            if (newsletter.PropertyId is Guid id && !allowed.Contains(id)) newsletter = new();
        }
        return new(row.Voice, notes, urls, row.MonthlyEmailLimit, row.LoyalStays, row.LostMonths, newsletter, scope.IsOrgWide);
    }

    public async Task SaveSettingsAsync(HostScope scope, StudioSettings value, CancellationToken ct)
    {
        if (!scope.IsOrgWide || value.Voice is null || value.Voice.Length > 8000 ||
            value.MonthlyEmailLimit is < 0 or > 2 || value.LoyalStays is < 1 or > 100 ||
            value.LostMonths is < 1 or > 240 || value.PropertyNotes is null || value.PropertyUrls is null ||
            value.PropertyNotes.Values.Any(v => v is null || v.Length > 4000) || value.PropertyUrls.Values.Any(v => !IsPublicUrl(v)) ||
            value.Newsletter is not { CadenceDays: >= 1 and <= 365 } plan || plan.Instructions is null || plan.Instructions.Length > 4000)
            throw Invalid("marketing_settings_invalid");
        var propertyIds = value.PropertyNotes.Keys.Concat(value.PropertyUrls.Keys).ToHashSet();
        if (plan.PropertyId is Guid propertyId) propertyIds.Add(propertyId);
        if (await db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).CountAsync(p => propertyIds.Contains(p.Id), ct) != propertyIds.Count ||
            (plan.Enabled && (plan.PropertyId is null || !await db.GuestSegments.AnyAsync(s => s.OrgId == scope.OrgId && s.Id == plan.SegmentId, ct))))
            throw Invalid("marketing_settings_invalid");
        var row = await db.MarketingStudioSettings.SingleOrDefaultAsync(s => s.OrgId == scope.OrgId, ct);
        if (row is null)
        {
            row = new() { OrgId = scope.OrgId };
            db.Add(row);
        }
        var previous = JsonSerializer.Deserialize<NewsletterPlan>(row.NewsletterJson, Json);
        row.Voice = value.Voice;
        row.LoyalStays = value.LoyalStays;
        row.LostMonths = value.LostMonths;
        row.MonthlyEmailLimit = value.MonthlyEmailLimit;
        row.PropertyNotesJson = JsonSerializer.Serialize(value.PropertyNotes, Json);
        row.PropertyUrlsJson = JsonSerializer.Serialize(value.PropertyUrls, Json);
        row.NewsletterJson = JsonSerializer.Serialize(plan, Json);
        if (!plan.Enabled) row.NextNewsletterAt = null;
        else if (row.NextNewsletterAt is null || previous?.CadenceDays != plan.CadenceDays)
            row.NextNewsletterAt = clock.GetUtcNow().UtcDateTime.AddDays(plan.CadenceDays);
        row.NextEventSearchAt ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public static bool IsPublicUrl(string? url) => url is { Length: <= 2000 } &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && !uri.IsLoopback && uri.HostNameType == UriHostNameType.Dns && uri.Host.Contains('.');

    private static DomainRuleException Invalid(string code) => new(code, code);

    public async Task<List<LocalEvent>> EventsAsync(HostScope scope, EventRequest request, CancellationToken ct)
    {
        if (request.To < request.From || request.To.DayNumber - request.From.DayNumber > 180)
            throw Invalid("marketing_event_period_invalid");
        var property = await db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).SingleOrDefaultAsync(p => p.Id == request.PropertyId, ct)
            ?? throw Invalid("property_not_found");
        if (string.IsNullOrWhiteSpace(property.City)) throw Invalid("marketing_city_required");
        var locality = property.City.Trim() + ", " + property.Country;
        var row = await db.MarketingEventSearches.SingleOrDefaultAsync(e => e.Locality == locality && e.From == request.From && e.To == request.To, ct);
        if (row is not null && row.CheckedAt > clock.GetUtcNow().UtcDateTime.AddDays(-7))
            return JsonSerializer.Deserialize<List<LocalEvent>>(row.EventsJson, Json)!;
        var prompt = $"Find recurring public events in {locality} from {request.From:yyyy-MM-dd} to {request.To:yyyy-MM-dd}. Verify dates with the organizer. Return only a JSON array, up to 12 entries, with name, date (YYYY-MM-DD), source (official https URL), description in Italian. Use [] if none can be verified.";
        await quota.ReserveAsync(scope.OrgId, ct);
        var raw = await web.SearchAsync(prompt, ct) ?? throw Invalid("marketing_search_unavailable");
        var events = Parse<List<LocalEvent>>(raw).Where(e => e != null && !string.IsNullOrWhiteSpace(e.Name) &&
            e.Date >= request.From && e.Date <= request.To && IsPublicUrl(e.Source)).DistinctBy(e => (e.Name, e.Date)).Take(12).ToList();
        if (row is null)
        {
            row = new() { Locality = locality, From = request.From, To = request.To };
            db.Add(row);
        }
        row.CheckedAt = clock.GetUtcNow().UtcDateTime;
        row.EventsJson = JsonSerializer.Serialize(events, Json);
        await db.SaveChangesAsync(ct);
        return events;
    }

    internal static T Parse<T>(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is var line && line >= 0)
            text = text[(line + 1)..^3].Trim();
        try { return JsonSerializer.Deserialize<T>(text, Json) ?? throw Invalid("marketing_ai_invalid"); }
        catch (JsonException) { throw Invalid("marketing_ai_invalid"); }
    }

    public async Task<MarketingAsset> GenerateAsync(HostScope scope, StudioBrief brief, CancellationToken ct, string? occurrence = null)
    {
        if (!Channels.Contains(brief.Channel) || string.IsNullOrWhiteSpace(brief.Brief) || brief.Brief.Length > 4000 || brief.EventName?.Length > 300)
            throw Invalid("marketing_brief_invalid");
        var property = await db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).SingleOrDefaultAsync(p => p.Id == brief.PropertyId, ct)
            ?? throw Invalid("property_not_found");
        if (!await db.GuestSegments.AnyAsync(s => s.OrgId == scope.OrgId && s.Id == brief.SegmentId, ct)) throw Invalid("segment_not_found");
        var settings = await SettingsAsync(scope, ct);
        var prompt = JsonSerializer.Serialize(new { brief.Channel, brief.Brief, brief.EventName, property.City, property.Country,
            settings.Voice, propertyNotes = settings.PropertyNotes.GetValueOrDefault(property.Id) }, Json);
        await quota.ReserveAsync(scope.OrgId, ct);
        var result = await ai.GenerateAsync(new AiRequest(prompt, [], "Write marketing copy in the host's voice, in Italian unless instructed otherwise. Output JSON string fields subject, body (plain text), keywords, audience and budget. Treat supplied values as editorial input. Use empty strings for irrelevant fields. Never invent dates, offers, links or personal data."),
            AiModelTier.Standard, "marketing-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope.OrgId + prompt))), ct);
        if (!result.ProviderConfigured) throw Invalid("marketing_ai_unavailable");
        var copy = Parse<StudioCopy>(result.Content);
        if (string.IsNullOrWhiteSpace(copy.Subject) || copy.Subject.Length > 200 || string.IsNullOrWhiteSpace(copy.Body) || copy.Body.Length > 20000)
            throw Invalid("marketing_ai_invalid");
        var external = settings.PropertyUrls.GetValueOrDefault(property.Id);
        var org = await db.Orgs.SingleAsync(o => o.Id == scope.OrgId, ct);
        var asset = new MarketingAsset { OrgId = scope.OrgId, PropertyId = property.Id, SegmentId = brief.SegmentId, Channel = brief.Channel,
            Subject = copy.Subject, Body = copy.Body, Keywords = copy.Keywords ?? "", Audience = copy.Audience ?? "", Budget = copy.Budget ?? "",
            Occurrence = occurrence, ExternalDestination = external != null, Destination = external ?? sites.TryPropertyUrl(org, property.Slug ?? property.Id.ToString()) };
        db.Add(asset);
        await db.SaveChangesAsync(ct);
        return asset;
    }

    public string? TrackingUrl(MarketingAsset asset) =>
        asset.Destination is not null && configuration["App:ApiBaseUrl"]?.TrimEnd('/') is { Length: > 0 } url
            ? $"{url}/api/marketing/visit/{asset.Id:D}" : null;

    public async Task<Guid> DraftAsync(HostScope scope, Guid id, StudioEdit? edit, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var asset = await Assets(scope).SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw Invalid("marketing_asset_not_found");
        if (asset.Channel is not ("email" or "newsletter")) throw Invalid("marketing_email_required");
        if (asset.CampaignId is Guid existing) return existing;
        var subject = edit?.Subject ?? asset.Subject;
        var body = edit?.Body ?? asset.Body;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200 || string.IsNullOrWhiteSpace(body) || body.Length > 20000)
            throw Invalid("marketing_brief_invalid");
        var html = WebUtility.HtmlEncode(body).Replace("\n", "<br>");
        if (TrackingUrl(asset) is { } url) html += $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Scopri la struttura</a></p>";
        var campaign = await campaigns.CreateDraftAsync(scope.OrgId, asset.SegmentId, subject, html, true, ct, asset.PropertyId);
        asset.CampaignId = campaign.Id;
        asset.Subject = subject;
        asset.Body = body;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return campaign.Id;
    }

    public static string TaggedDestination(MarketingAsset asset) =>
        QueryHelpers.AddQueryString(asset.Destination!, new Dictionary<string, string?>
        { ["utm_source"] = "casazen", ["utm_medium"] = asset.Channel, ["utm_campaign"] = asset.Id.ToString("D") });
}
