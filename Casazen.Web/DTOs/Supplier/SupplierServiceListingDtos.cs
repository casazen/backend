using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class SaveSupplierServiceRequest
{
    [MaxLength(SupplierServiceCatalogLimits.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(SupplierServiceCatalogLimits.CategoryMaxLength)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(SupplierServiceCatalogLimits.SummaryMaxLength)]
    public string? Summary { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.DescriptionMaxLength)]
    public string? Description { get; set; }

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; } = SupplierServicePriceUnit.PerJob;

    /// <summary>Whether the prices (base and supplements) include VAT. False when left out: nothing is promised.</summary>
    public bool PricesIncludeVat { get; set; }

    public bool RequiresQuote { get; set; }

    public int? DurationMinutes { get; set; }

    public int? MinNoticeHours { get; set; }

    [MaxLength(7)]
    public List<DayOfWeek>? Weekdays { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.MaxSupplements)]
    public List<SupplierServiceSupplementDto?>? Supplements { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.MaxListLinesInBody)]
    public List<string?>? Included { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.MaxListLinesInBody)]
    public List<string?>? Excluded { get; set; }

    /// <summary>
    /// Only on <c>PUT</c>: the photos to keep, in the new order (the first is the cover), a subset of the service's own
    /// photos: this is how a photo is removed or moved. Left out (<c>null</c>) keeps the photos as they are. A photo is
    /// added with <c>POST .../photos</c>; on <c>POST</c> of a new service the list must be empty.
    /// </summary>
    [MaxLength(SupplierServiceCatalogLimits.MaxPhotos)]
    public List<string?>? PhotoUrls { get; set; }

    public int? SortOrder { get; set; }

    public bool UrgentEnabled { get; set; }

    public int? UrgentSurchargeCents { get; set; }
}

public class UpdateSupplierServiceRequest : SaveSupplierServiceRequest
{
    [Required]
    public uint? Version { get; set; }
}

public class SupplierServiceSupplementDto
{
    [MaxLength(SupplierServiceCatalogLimits.SupplementCodeMaxLength)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(SupplierServiceCatalogLimits.SupplementLabelMaxLength)]
    public string Label { get; set; } = string.Empty;

    public int AmountCents { get; set; }

    [MaxLength(20)]
    public string Per { get; set; } = string.Empty;

    public int? Max { get; set; }
}

public class SupplierServiceDto
{
    public Guid Id { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? Description { get; set; }

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; }

    public bool PricesIncludeVat { get; set; }

    public bool RequiresQuote { get; set; }

    public int? DurationMinutes { get; set; }

    public int? MinNoticeHours { get; set; }

    public IReadOnlyList<DayOfWeek> Weekdays { get; set; } = [];

    public bool UrgentEnabled { get; set; }

    public int? UrgentSurchargeCents { get; set; }

    public IReadOnlyList<SupplierServiceSupplementDto> Supplements { get; set; } = [];

    public IReadOnlyList<string> Included { get; set; } = [];

    public IReadOnlyList<string> Excluded { get; set; } = [];

    public IReadOnlyList<string> PhotoUrls { get; set; } = [];

    public SupplierServiceListingStatus Status { get; set; }

    public int SortOrder { get; set; }

    public bool Publishable { get; set; }

    public IReadOnlyList<string> MissingForPublication { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public uint Version { get; set; }
}

public class SupplierServiceListResponse
{
    public IReadOnlyList<SupplierServiceDto> Items { get; set; } = [];

    public int Total { get; set; }

    public int Limit { get; set; } = SupplierServiceCatalogLimits.MaxServicesPerSupplier;
}

public static class SupplierServiceMapper
{
    public static SupplierServiceDto ToDto(SupplierServiceListing listing)
    {
        var missing = SupplierServiceListingRules.MissingForPublication(listing);
        return new SupplierServiceDto
        {
            Id = listing.Id,
            Slug = listing.Slug,
            Name = listing.Name,
            Category = listing.Category,
            Summary = listing.Summary,
            Description = listing.Description,
            PriceFromCents = listing.PriceFromCents,
            PriceUnit = listing.PriceUnit,
            PricesIncludeVat = listing.PricesIncludeVat,
            RequiresQuote = listing.RequiresQuote,
            DurationMinutes = listing.DurationMinutes,
            MinNoticeHours = listing.MinNoticeHours,
            Weekdays = SupplierServiceWeekdays.FromMask(listing.WeekdaysMask),
            UrgentEnabled = listing.UrgentEnabled,
            UrgentSurchargeCents = listing.UrgentSurchargeCents,
            Supplements = SupplierServiceListingJson.ReadSupplements(listing.SupplementsJson)
                .Select(s => new SupplierServiceSupplementDto
                {
                    Code = s.Code,
                    Label = s.Label,
                    AmountCents = s.AmountCents,
                    Per = s.Per,
                    Max = s.Max,
                })
                .ToList(),
            Included = SupplierServiceListingJson.ReadStrings(listing.IncludedJson),
            Excluded = SupplierServiceListingJson.ReadStrings(listing.ExcludedJson),
            PhotoUrls = SupplierServiceListingJson.ReadStrings(listing.PhotoUrlsJson),
            Status = listing.Status,
            SortOrder = listing.SortOrder,
            Publishable = missing.Count == 0,
            MissingForPublication = missing,
            CreatedAt = listing.CreatedAt,
            UpdatedAt = listing.UpdatedAt,
            Version = listing.Version,
        };
    }

    public static SupplierServiceListingInput ToInput(SaveSupplierServiceRequest request) =>
        new(
            request.Name,
            request.Category,
            request.Summary,
            request.Description,
            request.PriceFromCents,
            request.PriceUnit,
            request.PricesIncludeVat,
            request.RequiresQuote,
            request.DurationMinutes,
            request.MinNoticeHours,
            request.Weekdays,
            request.Supplements?
                .Select(s => s is null
                    ? null
                    : new SupplierServiceSupplement(s.Code, s.Label, s.AmountCents, s.Per, s.Max))
                .ToList(),
            request.Included,
            request.Excluded,
            request.PhotoUrls,
            request.SortOrder,
            request.UrgentEnabled,
            request.UrgentSurchargeCents);
}
