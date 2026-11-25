using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed class PropertyImportReviewService(AppDbContext db, TimeProvider clock) : IPropertyImportReviewService
{
    private const string Tenant = AppDbContext.TenantQueryFilter;
    private const string TodoEntity = "PropertyImportReview";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Confirmable = new(StringComparer.Ordinal)
    {
        "descriptionIt", "descriptionEn", "beds", "maxGuests", "cin", "photos", "nightlyRate", "ratePlans",
    };

    public async Task<PropertyImportReviewView?> GetAsync(HostScope scope, Guid propertyId, CancellationToken ct)
    {
        var property = await ScopedPropertyAsync(scope, propertyId, ct);
        var review = await db.PropertyImportReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == propertyId && r.OrgId == scope.OrgId, ct);
        return review is null ? null : View(review, property);
    }

    public async Task<PropertyImportReviewView> RecordAsync(Guid orgId, Guid propertyId, PropertyImportSnapshot snapshot, CancellationToken ct)
    {
        Validate(snapshot);
        var provider = snapshot.Provider.Trim().ToLowerInvariant();
        var sourceId = snapshot.SourceId.Trim();
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.PropertyImportReview, $"source:{orgId:N}:{provider}:{sourceId}"),
            (PostgresAdvisoryLocks.Scope.PropertyImportReview, $"property:{propertyId:N}"));

        var property = await db.Properties.IgnoreQueryFilters([Tenant])
            .SingleOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId && p.RentalMode == RentalMode.Short, ct)
            ?? throw NotFound();
        if (await db.PropertyImportReviews.IgnoreQueryFilters([Tenant])
            .AnyAsync(r => r.Id != propertyId && r.OrgId == orgId && r.Provider == provider && r.SourceId == sourceId, ct))
            throw new DomainConflictException("property_import_source_assigned", "PropertyImportSourceAssigned");

        var review = await db.PropertyImportReviews.IgnoreQueryFilters([Tenant])
            .SingleOrDefaultAsync(r => r.Id == propertyId && r.OrgId == orgId, ct);
        var serialized = JsonSerializer.Serialize(snapshot with { Provider = provider, SourceId = sourceId }, Json);
        if (review is null)
        {
            review = new PropertyImportReview
            {
                Id = propertyId, OrgId = orgId, Provider = provider, SourceId = sourceId,
                Snapshot = serialized, ResolvedFields = "[]",
            };
            db.PropertyImportReviews.Add(review);
            Prefill(property, snapshot);
        }
        else
        {
            if (review.Provider != provider || review.SourceId != sourceId)
                throw new DomainConflictException("property_import_source_assigned", "PropertyImportSourceAssigned");

            if (review.Snapshot != serialized)
            {
                var old = ReadSnapshot(review);
                var resolved = ReadResolved(review);
                foreach (var field in resolved.ToArray())
                {
                    if (!old.Findings.Where(f => f.Field == field).SequenceEqual(snapshot.Findings.Where(f => f.Field == field)))
                        resolved.Remove(field);
                }
                review.ResolvedFields = JsonSerializer.Serialize(resolved, Json);
                review.Snapshot = serialized;
            }
        }

        var result = await EvaluateAsync(review, property, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<PropertyImportReviewView> RecheckAsync(HostScope scope, Guid propertyId, string? confirmedField, CancellationToken ct)
    {
        await ScopedPropertyAsync(scope, propertyId, ct);
        return await RecheckCoreAsync(scope.OrgId, propertyId, confirmedField, ct) ?? throw NotFound();
    }

    public async Task RefreshAsync(Guid orgId, Guid propertyId, CancellationToken ct)
    {
        if (await db.PropertyImportReviews.AnyAsync(r => r.Id == propertyId && r.OrgId == orgId, ct))
            await RecheckCoreAsync(orgId, propertyId, null, ct);
    }

    private async Task<PropertyImportReviewView?> RecheckCoreAsync(Guid orgId, Guid propertyId, string? confirmedField, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.PropertyImportReview, $"property:{propertyId:N}"));
        var review = await db.PropertyImportReviews.SingleOrDefaultAsync(r => r.Id == propertyId && r.OrgId == orgId, ct);
        if (review is null)
            return null;

        var property = await db.Properties.AsNoTracking().SingleOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, ct)
            ?? throw NotFound();
        if (confirmedField is not null)
        {
            if (!Confirmable.Contains(confirmedField) || !ReadSnapshot(review).Findings.Any(f => f.Field == confirmedField && f.Status == "confirm"))
                throw Invalid();
            var fields = ReadResolved(review);
            fields.Add(confirmedField);
            review.ResolvedFields = JsonSerializer.Serialize(fields, Json);
        }

        var result = await EvaluateAsync(review, property, ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<PropertyImportReviewView> EvaluateAsync(PropertyImportReview review, Property property, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        review.CheckedAt = now;
        var view = View(review, property);
        var todo = review.TodoId is { } todoId
            ? await db.InternalActivities.IgnoreQueryFilters([Tenant])
                .SingleOrDefaultAsync(a => a.Id == todoId && a.OrgId == review.OrgId && a.PropertyId == property.Id, ct)
            : null;
        if (view.OpenFindings.Count > 0)
        {
            if (todo?.Status != InternalActivity.StatusPending)
            {
                todo = new InternalActivity
                {
                    OrgId = review.OrgId, PropertyId = property.Id, EntityId = property.Id, EntityType = TodoEntity,
                    ActivityType = ActivityType.ImportedPropertyReview.ToString(), Action = ActivityAction.ReviewImportedProperty,
                    Urgency = ActivityUrgency.Medium, ScheduledAt = now, CreatedAt = now, Status = InternalActivity.StatusPending,
                };
                db.InternalActivities.Add(todo);
                review.TodoId = todo.Id;
            }
            todo.Reason = string.Join(",", view.OpenFindings.Select(f => f.Field).Distinct());
        }
        else if (todo?.Status == InternalActivity.StatusPending)
        {
            todo.Status = InternalActivity.StatusCompleted;
            todo.CompletedAt = now;
            todo.ClosedBy = InternalActivity.ClosedBySystem;
        }
        await db.SaveChangesAsync(ct);
        return view;
    }

    private async Task<Property> ScopedPropertyAsync(HostScope scope, Guid propertyId, CancellationToken ct) =>
        await db.Properties.AsNoTracking().Where(p => p.Id == propertyId && p.OrgId == scope.OrgId && p.RentalMode == RentalMode.Short)
            .InScope(scope).SingleOrDefaultAsync(ct) ?? throw NotFound();

    private static void Prefill(Property property, PropertyImportSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(property.Description) && !string.IsNullOrWhiteSpace(snapshot.DescriptionIt))
            property.Description = snapshot.DescriptionIt;
        if (property.NightlyRate <= 0 && snapshot.NightlyRate is > 0 and <= 100_000 && property.Currency == snapshot.Currency)
            property.NightlyRate = snapshot.NightlyRate.Value;

        var profile = SitePropertyProfileJson.Parse(property.SiteProfileJson);
        var english = profile.Translations.GetValueOrDefault("en") ?? new ListingTexts();
        if (string.IsNullOrWhiteSpace(english.Description) && !string.IsNullOrWhiteSpace(snapshot.DescriptionEn))
        {
            var translations = new Dictionary<string, ListingTexts>(profile.Translations, StringComparer.Ordinal)
            {
                ["en"] = english with { Description = snapshot.DescriptionEn },
            };
            property.SiteProfileJson = SitePropertyProfileJson.Serialize(profile with { Translations = translations });
        }
    }

    private static PropertyImportReviewView View(PropertyImportReview review, Property property)
    {
        var snapshot = ReadSnapshot(review);
        var resolved = ReadResolved(review);
        var findings = snapshot.Findings.Where(f => f.Status != "confirm" || !resolved.Contains(f.Field)).ToList();
        var english = SitePropertyProfileJson.Parse(property.SiteProfileJson).Translations.GetValueOrDefault("en")?.Description;
        AddMissing("descriptionIt", string.IsNullOrWhiteSpace(property.Description));
        AddMissing("descriptionEn", string.IsNullOrWhiteSpace(english));
        AddMissing("beds", property.Beds is null);
        AddMissing("maxGuests", property.MaxGuests <= 0);
        AddMissing("cin", property.Country == "IT" && string.IsNullOrWhiteSpace(property.CinCode));
        AddMissing("photos", property.PhotoUrls.Count == 0);
        AddMissing("nightlyRate", property.NightlyRate <= 0);
        return new PropertyImportReviewView(property.Id, snapshot, findings, review.CheckedAt);

        void AddMissing(string field, bool isMissing)
        {
            if (isMissing && !findings.Any(f => f.Field == field))
                findings.Add(new PropertyImportFinding(field, "missing", "missing", snapshot.SourceUrl));
            if (!isMissing)
                findings.RemoveAll(f => f.Field == field && f.Status == "missing");
        }
    }

    private static void Validate(PropertyImportSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Provider) || string.IsNullOrWhiteSpace(snapshot.SourceId)
            || !HttpSource(snapshot.SourceUrl) || snapshot.RatePlans is null || snapshot.Findings is null
            || snapshot.NightlyRate is <= 0 or > 100_000
            || (snapshot.NightlyRate is not null && snapshot.Currency is not ("EUR" or "CHF"))
            || snapshot.RatePlans.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name)
                || p.Currency is not ("EUR" or "CHF") || string.IsNullOrWhiteSpace(p.Unit)
                || p.PriceKind is not ("quote" or "list" or "effective") || (p.From is null) != (p.To is null)
                || p.From > p.To || p.Amount is <= 0 or > 100_000
                || (p.Prices is not null && (p.Prices.Any(d => d is null || d.Amount is <= 0 or > 100_000)
                    || p.Prices.Select(d => d.Date).Distinct().Count() != p.Prices.Count)))
            || snapshot.RatePlans.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.RatePlans.Count
            || snapshot.Findings.Any(f => f is null || !Confirmable.Contains(f.Field) || f.Status is not ("missing" or "confirm" or "mapping")
                || (f.Source is not null && !HttpSource(f.Source)))
            || (snapshot.UnmappedFields is not null && snapshot.UnmappedFields.Any(f => f.Key.Length > 120 || f.Value is null || f.Value.Length > 2000)))
            throw Invalid();
    }

    private static bool HttpSource(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);
    private static PropertyImportSnapshot ReadSnapshot(PropertyImportReview review) =>
        JsonSerializer.Deserialize<PropertyImportSnapshot>(review.Snapshot, Json)!;
    private static HashSet<string> ReadResolved(PropertyImportReview review) =>
        JsonSerializer.Deserialize<HashSet<string>>(review.ResolvedFields, Json)!;
    private static DomainRuleException Invalid() => new("property_import_invalid", "PropertyImportInvalid");
    private static NotFoundException NotFound() => new("Property not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };
}
