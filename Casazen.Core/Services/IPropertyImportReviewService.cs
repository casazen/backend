using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

public interface IPropertyImportReviewService
{
    Task<PropertyImportReviewView?> GetAsync(HostScope scope, Guid propertyId, CancellationToken ct);
    Task<PropertyImportReviewView> RecordAsync(Guid orgId, Guid propertyId, PropertyImportSnapshot snapshot, CancellationToken ct);
    Task<PropertyImportReviewView> RecheckAsync(HostScope scope, Guid propertyId, string? confirmedField, CancellationToken ct);
    Task RefreshAsync(Guid orgId, Guid propertyId, CancellationToken ct);
}

public sealed record PropertyImportSnapshot(
    [Required, MaxLength(80)] string Provider,
    [Required, MaxLength(240)] string SourceId,
    [Required, MaxLength(1000)] string SourceUrl,
    [MaxLength(240)] string? ParentSourceId,
    [MaxLength(ListingTextLimits.DescriptionMaxLength)] string? DescriptionIt,
    [MaxLength(ListingTextLimits.DescriptionMaxLength)] string? DescriptionEn,
    decimal? NightlyRate,
    [MaxLength(3)] string? Currency,
    [MaxLength(100)] IReadOnlyList<PropertyImportRatePlan> RatePlans,
    [MaxLength(20)] IReadOnlyList<PropertyImportFinding> Findings,
    [MaxLength(100)] IReadOnlyDictionary<string, string>? UnmappedFields);

public sealed record PropertyImportRatePlan(
    [Required, MaxLength(160)] string Id,
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(3)] string Currency,
    [Required, MaxLength(32)] string Unit,
    [Range(1, 100)] int? Occupants,
    [Range(0.01, 100000)] decimal? Amount,
    DateOnly? From,
    DateOnly? To,
    [MaxLength(2500)] string? Conditions,
    [MaxLength(2500)] string? Payment,
    [MaxLength(2500)] string? Restrictions,
    [MaxLength(160)] string? DerivedFrom,
    [MaxLength(1000)] IReadOnlyList<PropertyImportRateDay>? Prices,
    [Required, MaxLength(32)] string PriceKind = "quote",
    [MaxLength(2500)] string? Supplements = null);

public sealed record PropertyImportRateDay(DateOnly Date, [Range(0.01, 100000)] decimal Amount);

public sealed record PropertyImportFinding(
    [Required, MaxLength(40)] string Field,
    [Required, MaxLength(32)] string Status,
    [Required, MaxLength(350)] string Reason,
    [MaxLength(1000)] string? Source);

public sealed record PropertyImportReviewView(
    Guid PropertyId,
    PropertyImportSnapshot Snapshot,
    IReadOnlyList<PropertyImportFinding> OpenFindings,
    DateTime CheckedAt);
