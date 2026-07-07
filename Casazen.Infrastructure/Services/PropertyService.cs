using System.Text.RegularExpressions;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyService(IPropertyRepository repository, ILogger<PropertyService> logger) : IPropertyService
{
    public async Task<Property?> GetPropertyAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId)
    {
        return await repository.GetByOwnerAsync(ownerId);
    }

    public async Task<IEnumerable<Property>> GetAllPropertiesAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Property> CreatePropertyAsync(Property property)
    {
        logger.LogInformation("Creating property: {Name}", property.Name);
        property.Slug = await ResolveSlugForCreateAsync(property.OrgId, property.Name, property.Slug);
        return await repository.AddAsync(property);
    }

    public async Task<Property> UpdatePropertyAsync(Property property)
    {
        logger.LogInformation("Updating property: {Id}", property.Id);
        if (!string.IsNullOrWhiteSpace(property.Slug))
        {
            property.Slug = PropertySlugHelper.NormalizeOptional(property.Slug);
            if (await repository.SlugExistsInOrgAsync(property.OrgId, property.Slug, property.Id))
                throw new InvalidOperationException("Slug already in use within this organization.");
        }

        return await repository.UpdateAsync(property);
    }

    public async Task<bool> DeletePropertyAsync(Guid id)
    {
        logger.LogInformation("Deleting property: {Id}", id);
        await repository.DeleteAsync(id);
        return true;
    }

    public async Task<IEnumerable<PublicPropertyDto>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice)
    {
        logger.LogInformation("Searching properties: city={City}, bedrooms={Bedrooms}, maxPrice={MaxPrice}", city, bedrooms, maxPrice);

        var rows = await repository.GetSearchQueryable(city, bedrooms, maxPrice)
            .OrderBy(p => p.City)
            .ThenBy(p => p.NightlyRate)
            .Take(50)
            .Select(p => new PublicPropertyRow
            {
                Id = p.Id,
                Slug = p.Slug,
                Name = p.Name,
                Description = p.Description,
                City = p.City,
                PostalCode = p.PostalCode,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Bedrooms = p.Bedrooms,
                Bathrooms = p.Bathrooms,
                MaxGuests = p.MaxGuests,
                NightlyRate = p.NightlyRate,
                CleaningFee = p.CleaningFee,
                Amenities = p.Amenities,
                PhotoUrls = p.PhotoUrls,
                CinCode = p.CinCode,
                Timezone = p.Timezone,
            })
            .ToListAsync();

        return rows.Select(MapPublicProperty).ToList();
    }

    public async Task<IEnumerable<PublicPropertyDto>> SearchByOrgAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Searching public properties for org {OrgId}", orgId);

        var rows = await repository.GetSearchQueryable(null, null, null, orgId)
            .OrderBy(p => p.City)
            .ThenBy(p => p.NightlyRate)
            .Take(50)
            .Select(p => new PublicPropertyRow
            {
                Id = p.Id,
                Slug = p.Slug,
                Name = p.Name,
                Description = p.Description,
                City = p.City,
                PostalCode = p.PostalCode,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Bedrooms = p.Bedrooms,
                Bathrooms = p.Bathrooms,
                MaxGuests = p.MaxGuests,
                NightlyRate = p.NightlyRate,
                CleaningFee = p.CleaningFee,
                Amenities = p.Amenities,
                PhotoUrls = p.PhotoUrls,
                CinCode = p.CinCode,
                Timezone = p.Timezone,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(MapPublicProperty).ToList();
    }

    public async Task<PublicPropertyDetailDto?> GetPublicPropertyAsync(Guid id)
    {
        var row = await repository.GetSearchQueryable(null, null, null)
            .Where(p => p.Id == id)
            .Select(p => new PublicPropertyDetailRow
            {
                Id = p.Id,
                Slug = p.Slug,
                Name = p.Name,
                Description = p.Description,
                City = p.City,
                PostalCode = p.PostalCode,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Bedrooms = p.Bedrooms,
                Bathrooms = p.Bathrooms,
                MaxGuests = p.MaxGuests,
                NightlyRate = p.NightlyRate,
                CleaningFee = p.CleaningFee,
                Amenities = p.Amenities,
                PhotoUrls = p.PhotoUrls,
                CinCode = p.CinCode,
                Timezone = p.Timezone,
                HouseRules = p.HouseRules,
                CancellationPolicySummary = p.CancellationPolicy != null ? p.CancellationPolicy.Description : string.Empty,
            })
            .FirstOrDefaultAsync();

        return row is null ? null : MapPublicPropertyDetail(row);
    }

    public async Task<PublicPropertyDetailDto?> GetPublicPropertyForOrgAsync(string slugOrId, Guid orgId)
    {
        var query = repository.GetSearchQueryable(null, null, null, orgId);
        if (Guid.TryParse(slugOrId, out var id))
            query = query.Where(p => p.Id == id);
        else
            query = query.Where(p => p.Slug == slugOrId);

        var row = await query
            .Select(p => new PublicPropertyDetailRow
            {
                Id = p.Id,
                Slug = p.Slug,
                Name = p.Name,
                Description = p.Description,
                City = p.City,
                PostalCode = p.PostalCode,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Bedrooms = p.Bedrooms,
                Bathrooms = p.Bathrooms,
                MaxGuests = p.MaxGuests,
                NightlyRate = p.NightlyRate,
                CleaningFee = p.CleaningFee,
                Amenities = p.Amenities,
                PhotoUrls = p.PhotoUrls,
                CinCode = p.CinCode,
                Timezone = p.Timezone,
                HouseRules = p.HouseRules,
                CancellationPolicySummary = p.CancellationPolicy != null ? p.CancellationPolicy.Description : string.Empty,
            })
            .FirstOrDefaultAsync();

        return row is null ? null : MapPublicPropertyDetail(row);
    }

    public async Task<Property> AddImageAsync(Guid propertyId, string imageUrl)
    {
        var property = await repository.GetByIdAsync(propertyId);
        if (property == null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        // Add image URL to the list
        property.PhotoUrls.Add(imageUrl);
        property.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation("Adding image to property {PropertyId}: {ImageUrl}", propertyId, imageUrl);
        return await repository.UpdateAsync(property);
    }

    public async Task<Property> RemoveImageAsync(Guid propertyId, int imageIndex)
    {
        var property = await repository.GetByIdAsync(propertyId);
        if (property == null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        if (imageIndex < 0 || imageIndex >= property.PhotoUrls.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(imageIndex), $"Invalid image index {imageIndex}");
        }

        // Remove image URL from the list
        property.PhotoUrls.RemoveAt(imageIndex);
        property.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation("Removing image at index {Index} from property {PropertyId}", imageIndex, propertyId);
        return await repository.UpdateAsync(property);
    }

    public async Task<Property> ReorderImagesAsync(Guid propertyId, List<string> orderedImageUrls)
    {
        var property = await repository.GetByIdAsync(propertyId);
        if (property == null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        // Validate that all URLs in the new order exist in the current list
        var currentUrls = property.PhotoUrls.ToHashSet();
        if (!orderedImageUrls.All(url => currentUrls.Contains(url)) ||
            orderedImageUrls.Count != property.PhotoUrls.Count)
        {
            throw new InvalidOperationException("Invalid image URLs provided for reordering");
        }

        // Update the order
        property.PhotoUrls = orderedImageUrls;
        property.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation("Reordering images for property {PropertyId}", propertyId);
        return await repository.UpdateAsync(property);
    }

    public async Task<PropertyDetailResponse> GetPropertyDetailAsync(Guid propertyId)
    {
        var property = await repository.GetPropertyDetailAsync(propertyId)
            ?? throw new InvalidOperationException($"Property {propertyId} not found");

        var now = DateTime.UtcNow;
        return new PropertyDetailResponse
        {
            Id = property.Id,
            OwnerId = property.OwnerId,
            Name = property.Name,
            Description = property.Description,
            Address = property.Address,
            City = property.City,
            PostalCode = property.PostalCode,
            Bedrooms = property.Bedrooms,
            Bathrooms = property.Bathrooms,
            MaxGuests = property.MaxGuests,
            NightlyRate = property.NightlyRate,
            CleaningFee = property.CleaningFee,
            DamageDeposit = property.DamageDeposit,
            CinCode = property.CinCode,
            CinStatus = ResolveCinStatus(property.CinCode),
            Timezone = property.Timezone,
            Amenities = property.Amenities.Select(a => a.ToString()).ToList(),
            PhotoUrls = property.PhotoUrls,
            HouseRules = property.HouseRules,
            IsActive = property.IsActive,
            CreatedAt = property.CreatedAt,
            UpdatedAt = property.UpdatedAt,
            Documents = property.PropertyDocuments.Select(MapDocument).ToList(),
            OtaIntegrations = property.OtaIntegrations.Select(o => new OtaIntegrationSummaryDto
            {
                Id = o.Id,
                Platform = o.Platform,
                IsActive = o.IsActive,
                SyncEnabled = o.SyncEnabled,
                LastSyncAt = o.LastSyncAt,
                SyncStatus = o.SyncStatus != null && Enum.TryParse<OtaSyncStatus>(o.SyncStatus, out var status) ? status : null
            }).ToList(),
            BookingsSummary = new BookingsSummaryDto
            {
                TotalBookings = property.Bookings.Count,
                UpcomingBookings = property.Bookings.Count(b =>
                    b.CheckInDate > now && b.Status == BookingStatus.Confirmed),
                ActiveBookings = property.Bookings.Count(b =>
                    b.CheckInDate <= now && b.CheckOutDate > now && b.Status == BookingStatus.CheckedIn),
                NextCheckIn = property.Bookings
                    .Where(b => b.CheckInDate > now)
                    .MinBy(b => b.CheckInDate)?.CheckInDate,
                NextCheckOut = property.Bookings
                    .Where(b => b.CheckOutDate > now)
                    .MinBy(b => b.CheckOutDate)?.CheckOutDate
            },
            PricingAdapterSummary = property.PricingAdapterConfig == null
                ? new PricingAdapterSummaryDto()
                : new PricingAdapterSummaryDto
                {
                    IsEnabled = property.PricingAdapterConfig.IsEnabled,
                    LastAdaptedAt = property.PricingAdapterConfig.LastAdaptedAt,
                    NextScheduledRunAt = property.PricingAdapterConfig.NextScheduledRunAt
                }
        };
    }

    public static PropertyDocumentDto MapDocument(PropertyDocument d) => new()
    {
        Id = d.Id,
        FileName = d.FileName,
        FileType = ResolveFileType(d),
        UploadedAt = d.UploadedAt,
        DownloadUrl = d.StorageUrl
    };

    private static string ResolveFileType(PropertyDocument document)
    {
        var extension = Path.GetExtension(document.FileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension.TrimStart('.').ToLowerInvariant();
        }

        return document.DocumentType.ToString();
    }

    private static readonly Regex CinRegex = new(@"^IT-\d{5}-\d{10}$", RegexOptions.Compiled);

    internal static CinStatus ResolveCinStatus(string? cinCode)
    {
        if (string.IsNullOrWhiteSpace(cinCode)) return CinStatus.Missing;
        return CinRegex.IsMatch(cinCode) ? CinStatus.Valid : CinStatus.Invalid;
    }

    public async Task<OwnerCinComplianceResult> GetOwnerCinComplianceAsync(
        string ownerId, string? cinStatus, int page, int pageSize)
    {
        if (!string.IsNullOrWhiteSpace(cinStatus) &&
            cinStatus is not ("valid" or "missing" or "invalid"))
        {
            throw new ArgumentException($"Unknown cinStatus value '{cinStatus}'", nameof(cinStatus));
        }

        var properties = await repository.GetByOwnerForComplianceAsync(ownerId);
        var items = properties.Select(p => new OwnerCinComplianceItem(
            PropertyId: p.Id,
            PropertyName: p.Name,
            CinCode: p.CinCode,
            CinStatus: CinComplianceRules.ResolveStatus(p.CinCode),
            City: p.City)).ToList();

        var valid = items.Count(i => i.CinStatus == "valid");
        var missing = items.Count(i => i.CinStatus == "missing");
        var invalid = items.Count(i => i.CinStatus == "invalid");
        var daysUntilDeadline = CinComplianceRules.DaysUntilDeadline();

        var summary = new CinComplianceSummary(
            Valid: valid,
            Missing: missing,
            Invalid: invalid,
            DaysUntilDeadline: daysUntilDeadline,
            Deadline: CinComplianceRules.RegulatoryDeadline,
            HasNonCompliant: missing + invalid > 0);

        IEnumerable<OwnerCinComplianceItem> filtered = items;
        if (!string.IsNullOrWhiteSpace(cinStatus))
            filtered = items.Where(i => i.CinStatus == cinStatus);

        var list = filtered.ToList();
        var paged = list
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new OwnerCinComplianceResult(paged, list.Count, summary);
    }

    public async Task UpdatePropertyCinAsync(Guid propertyId, string? cinCode)
    {
        var property = await repository.GetByIdAsync(propertyId)
            ?? throw new KeyNotFoundException($"Property {propertyId} not found");

        var normalized = string.IsNullOrWhiteSpace(cinCode) ? null : cinCode.Trim();

        if (normalized != null)
        {
            if (!CinRegex.IsMatch(normalized))
            {
                throw new ArgumentException(
                    "CIN code must match format IT-XXXXX-XXXXXXXXXX (e.g., IT-12345-0123456789).");
            }

            if (await repository.CinCodeExistsOnOtherPropertyAsync(normalized, propertyId))
            {
                throw new InvalidOperationException("CIN code is already assigned to another property.");
            }
        }

        property.CinCode = normalized;
        await repository.UpdateAsync(property);
    }

    private async Task<string> ResolveSlugForCreateAsync(Guid orgId, string name, string? requestedSlug)
    {
        if (!string.IsNullOrWhiteSpace(requestedSlug))
        {
            var normalized = PropertySlugHelper.NormalizeOptional(requestedSlug);
            if (await repository.SlugExistsInOrgAsync(orgId, normalized, null))
                throw new InvalidOperationException("Slug already in use within this organization.");
            return normalized;
        }

        return await AllocateUniqueSlugAsync(orgId, name, null);
    }

    private async Task<string> AllocateUniqueSlugAsync(Guid orgId, string name, Guid? excludePropertyId)
    {
        var baseSlug = PropertySlugHelper.Sanitize(name);
        if (baseSlug.Length > 90)
            baseSlug = baseSlug[..90].TrimEnd('-');

        var candidate = baseSlug;
        var suffix = 0;
        while (await repository.SlugExistsInOrgAsync(orgId, candidate, excludePropertyId))
        {
            suffix++;
            candidate = $"{baseSlug}-{suffix}";
        }

        return candidate;
    }

    private static PublicPropertyDto MapPublicProperty(PublicPropertyRow row) => new()
    {
        Id = row.Id,
        Slug = row.Slug,
        Name = row.Name,
        Description = row.Description,
        City = row.City,
        PostalCode = row.PostalCode,
        Latitude = row.Latitude,
        Longitude = row.Longitude,
        Bedrooms = row.Bedrooms,
        Bathrooms = row.Bathrooms,
        MaxGuests = row.MaxGuests,
        NightlyRate = row.NightlyRate,
        CleaningFee = row.CleaningFee,
        Amenities = row.Amenities.Select(a => a.ToString()).ToList(),
        PhotoUrls = row.PhotoUrls,
        CinCode = row.CinCode,
        CinStatus = ResolveCinStatus(row.CinCode),
        Timezone = row.Timezone,
    };

    private static PublicPropertyDetailDto MapPublicPropertyDetail(PublicPropertyDetailRow row) => new()
    {
        Id = row.Id,
        Slug = row.Slug,
        Name = row.Name,
        Description = row.Description,
        City = row.City,
        PostalCode = row.PostalCode,
        Latitude = row.Latitude,
        Longitude = row.Longitude,
        Bedrooms = row.Bedrooms,
        Bathrooms = row.Bathrooms,
        MaxGuests = row.MaxGuests,
        NightlyRate = row.NightlyRate,
        CleaningFee = row.CleaningFee,
        Amenities = row.Amenities.Select(a => a.ToString()).ToList(),
        PhotoUrls = row.PhotoUrls,
        CinCode = row.CinCode,
        CinStatus = ResolveCinStatus(row.CinCode),
        Timezone = row.Timezone,
        HouseRules = row.HouseRules,
        CancellationPolicySummary = row.CancellationPolicySummary,
        MinNights = null,
        Currency = "EUR",
    };

    private class PublicPropertyRow
    {
        public Guid Id { get; init; }
        public string? Slug { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public decimal Latitude { get; init; }
        public decimal Longitude { get; init; }
        public int Bedrooms { get; init; }
        public int Bathrooms { get; init; }
        public int MaxGuests { get; init; }
        public decimal NightlyRate { get; init; }
        public decimal CleaningFee { get; init; }
        public List<PropertyAmenity> Amenities { get; init; } = [];
        public List<string> PhotoUrls { get; init; } = [];
        public string? CinCode { get; init; }
        public string Timezone { get; init; } = "Europe/Rome";
    }

    private sealed class PublicPropertyDetailRow : PublicPropertyRow
    {
        public string HouseRules { get; init; } = string.Empty;
        public string CancellationPolicySummary { get; init; } = string.Empty;
    }
}