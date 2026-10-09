using Casazen.Core.Authorization;
using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Pricing;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <remarks>
/// Every change of the CIN or of the property row (base data, city, CIN sent with the PATCH) re-evaluates the compliance
/// status (<see cref="IPropertyComplianceStatusService.ReevaluateAsync"/>, CO-06): an active property that loses a
/// requirement is suspended from the booking site, a suspended one whose requirements are complete again is reactivated.
/// </remarks>
public class PropertyService(
    IPropertyRepository repository,
    IPropertyComplianceStatusService complianceStatus,
    CinDeadlineCalendar cinDeadline,
    ILogger<PropertyService> logger,
    TimeProvider? timeProvider = null) : IPropertyService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>422: the cancellation policy chosen for a property does not exist.</summary>
    public const string CancellationPolicyNotFoundCode = "cancellation_policy_not_found";

    /// <summary>422: a latitude outside -90..90 or a longitude outside -180..180.</summary>
    public const string PropertyCoordinatesInvalidCode = "property_coordinates_invalid";

    /// <summary>The unique index of <c>(OrgId, Slug)</c> (see <c>AppDbContext</c>).</summary>
    private const string SlugUniqueIndexName = "UIX_Properties_OrgId_Slug";

    public async Task<Property?> GetPropertyAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<Property?> GetPropertyRecordAsync(Guid id)
    {
        return await repository.GetRecordAsync(id);
    }

    public async Task<IReadOnlyList<CancellationPolicyOptionDto>> GetCancellationPoliciesAsync()
    {
        var policies = await repository.GetCancellationPoliciesAsync();
        return policies
            .Select(p => new CancellationPolicyOptionDto(
                p.Id, p.Name, p.Description, p.FullRefundHours, p.PartialRefundPercent, p.PartialRefundHours))
            .ToList();
    }

    public async Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId)
    {
        return await repository.GetByOwnerAsync(ownerId);
    }

    public async Task<IEnumerable<Property>> GetPropertiesAsync(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await repository.GetByScopeAsync(scope);
    }

    public async Task<IEnumerable<Property>> GetPropertiesAsync(HostScope scope, RentalMode mode)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await repository.GetByScopeAsync(scope, mode);
    }

    public async Task<IEnumerable<Property>> GetAllPropertiesAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Property> CreatePropertyAsync(Property property)
    {
        logger.LogInformation("Creating property: {Name}", property.Name);
        property.CinCode = CinFormat.Normalize(property.CinCode);
        NormalizeLocation(property);
        property.Slug = await ResolveSlugForCreateAsync(property.OrgId, property.Name, property.Slug);
        await EnsureCancellationPolicyExistsAsync(property);
        try
        {
            return await repository.AddAsync(property);
        }
        catch (DbUpdateException ex) when (ToUniqueConflict(ex) is { } conflict)
        {
            // The unique indexes are the guarantee under concurrency (A2-19): no address check before the insert.
            logger.LogWarning("Property of org {OrgId} refused by a unique index: {Code}", property.OrgId, conflict.Code);
            throw conflict;
        }
    }

    public async Task<Property> UpdatePropertyAsync(Property property)
    {
        logger.LogInformation("Updating property: {Id}", property.Id);
        property.CinCode = CinFormat.Normalize(property.CinCode);
        NormalizeLocation(property);
        if (!string.IsNullOrWhiteSpace(property.Slug))
        {
            property.Slug = PropertySlugHelper.NormalizeOptional(property.Slug);
            if (await repository.SlugExistsInOrgAsync(property.OrgId, property.Slug, property.Id))
                throw new DomainConflictException("duplicate_property_slug", "PropertySlugTaken");
        }

        await EnsureCancellationPolicyExistsAsync(property);
        Property updated;
        try
        {
            updated = await repository.UpdateAsync(property);
        }
        catch (DbUpdateException ex) when (ToUniqueConflict(ex) is { } conflict)
        {
            // Another property took the address (or the slug) between the read and this save (A2-19).
            logger.LogWarning("Update of property {Id} refused by a unique index: {Code}", property.Id, conflict.Code);
            throw conflict;
        }

        await complianceStatus.ReevaluateAsync(updated.Id);
        return updated;
    }

    /// <summary>
    /// The unit and the coordinates as stored (trimmed unit, coordinates with the precision of the column), and a
    /// coordinate outside the earth refused before it reaches the database (422): the API validates the same ranges at its
    /// boundary, this protects every other caller of the service and the <c>numeric(9,6)</c> columns (PC-06, A2-33).
    /// </summary>
    private static void NormalizeLocation(Property property)
    {
        if (!PropertyAddress.IsValidLatitude(property.Latitude) || !PropertyAddress.IsValidLongitude(property.Longitude))
            throw new DomainRuleException(PropertyCoordinatesInvalidCode, "PropertyCoordinatesInvalid");

        property.Unit = PropertyAddress.NormalizeUnit(property.Unit);
        property.Latitude = PropertyAddress.RoundCoordinate(property.Latitude);
        property.Longitude = PropertyAddress.RoundCoordinate(property.Longitude);
    }

    /// <summary>
    /// 409 for a violation of the unique address index (same org, same address and unit) or of the unique slug index of
    /// the org, null for any other database error. Neither index spans orgs: the conflict never tells that ANOTHER org
    /// has a property at this address.
    /// </summary>
    private static DomainConflictException? ToUniqueConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
            ? postgres.ConstraintName switch
            {
                PropertyAddress.UniqueIndexName => new DomainConflictException(PropertyAddress.DuplicateCode, "PropertyAddressTaken"),
                SlugUniqueIndexName => new DomainConflictException("duplicate_property_slug", "PropertySlugTaken"),
                _ => null,
            }
            : null;

    public async Task<Property> PausePropertyAsync(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!property.IsPaused)
        {
            logger.LogInformation("Pausing property: {Id}", property.Id);
            var now = _clock.GetUtcNow().UtcDateTime;
            property.IsPaused = true;
            property.PausedAt = now;
            property.UpdatedAt = now;
            await repository.UpdateAsync(property);
        }
        return property;
    }

    public async Task<Property> ActivatePropertyAsync(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.IsPaused)
        {
            logger.LogInformation("Reactivating property: {Id}", property.Id);
            property.IsPaused = false;
            property.PausedAt = null;
            property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await repository.UpdateAsync(property);
        }
        return property;
    }

    /// <summary>An unknown policy id would otherwise fail on the foreign key as a 500 (A2-04).</summary>
    private async Task EnsureCancellationPolicyExistsAsync(Property property)
    {
        if (property.CancellationPolicyId is { } policyId && !await repository.CancellationPolicyExistsAsync(policyId))
            throw new DomainRuleException(CancellationPolicyNotFoundCode, "PropertyCancellationPolicyNotFound");
    }

    /// <summary>
    /// 409: the property has a pending, confirmed or checked-in stay whose check-out has not passed (PC-05, A2-18): a
    /// host cannot make a property with a guest already booked disappear.
    /// </summary>
    public const string HasUpcomingBookingsCode = "property_has_upcoming_bookings";

    /// <summary>409: the property has a lease in force or in progress that has not ended yet (PC-05, A2-18).</summary>
    public const string HasActiveLeasesCode = "property_has_active_leases";

    /// <summary>
    /// Soft-deletes the property (PC-05, A2-18): its historical bookings and fiscal data are kept and stay reachable by
    /// the fiscal reports, never removed. False when there is nothing to delete (unknown or already deleted).
    /// </summary>
    /// <exception cref="DomainConflictException">
    /// <see cref="HasUpcomingBookingsCode"/> or <see cref="HasActiveLeasesCode"/>: nothing changed.
    /// </exception>
    public async Task<bool> DeletePropertyAsync(Guid id)
    {
        logger.LogInformation("Deleting property: {Id}", id);
        var outcome = await repository.SoftDeleteAsync(id, _clock.GetUtcNow().UtcDateTime, _clock.TodayInRome());
        switch (outcome)
        {
            case PropertySoftDeleteOutcome.HasUpcomingStays:
                logger.LogWarning("Delete of property {Id} refused: it has a stay that has not checked out yet", id);
                throw new DomainConflictException(HasUpcomingBookingsCode, "PropertyHasUpcomingBookings");
            case PropertySoftDeleteOutcome.HasActiveLeases:
                logger.LogWarning("Delete of property {Id} refused: it has a lease that has not ended yet", id);
                throw new DomainConflictException(HasActiveLeasesCode, "PropertyHasActiveLeases");
            default:
                return outcome == PropertySoftDeleteOutcome.Deleted;
        }
    }

    public async Task<IEnumerable<PublicPropertyDto>> SearchAsync(PublicPropertySearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        logger.LogInformation(
            "Searching properties: city={City}, price={MinPrice}-{MaxPrice}, minBedrooms={MinBedrooms}, minBathrooms={MinBathrooms}, guests={Guests}",
            criteria.City, criteria.MinPrice, criteria.MaxPrice, criteria.MinBedrooms, criteria.MinBathrooms, criteria.Guests);

        var rows = await repository.GetSearchQueryable(criteria)
            .OrderBy(p => p.City)
            .ThenBy(p => p.NightlyRate)
            .Take(50)
            .Select(p => new PublicPropertyRow
            {
                Id = p.Id,
                Slug = p.Slug,
                OrgSlug = p.Org.Slug,
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

        var rows = await repository.GetSearchQueryable(PublicPropertySearchCriteria.None, orgId)
            .OrderBy(p => p.City)
            .ThenBy(p => p.NightlyRate)
            .Take(50)
            .Select(p => new PublicPropertyRow
            {
                Id = p.Id,
                Slug = p.Slug,
                OrgSlug = p.Org.Slug,
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
        var row = await repository.GetSearchQueryable(PublicPropertySearchCriteria.None)
            .Where(p => p.Id == id)
            .Select(p => new PublicPropertyDetailRow
            {
                Id = p.Id,
                Slug = p.Slug,
                OrgSlug = p.Org.Slug,
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
        var query = repository.GetSearchQueryable(PublicPropertySearchCriteria.None, orgId);
        if (Guid.TryParse(slugOrId, out var id))
            query = query.Where(p => p.Id == id);
        else
            query = query.Where(p => p.Slug == slugOrId);

        var row = await query
            .Select(p => new PublicPropertyDetailRow
            {
                Id = p.Id,
                Slug = p.Slug,
                OrgSlug = p.Org.Slug,
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

    public async Task<PropertyDetailResponse> GetPropertyDetailAsync(Guid propertyId)
    {
        // 404 through the error middleware (FD-05); any other failure stays a 500, never a "not found" (A2-36).
        var property = await repository.GetPropertyDetailAsync(propertyId)
            ?? throw new NotFoundException($"Property {propertyId} not found");

        return new PropertyDetailResponse
        {
            Id = property.Id,
            OwnerId = property.OwnerId,
            Name = property.Name,
            Description = property.Description,
            Address = property.Address,
            Unit = property.Unit,
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
            CinIstatMismatch = CinFormat.HasIstatComuneMismatch(property.CinCode, property.ComuneIstatCode),
            ComuneIstatCode = property.ComuneIstatCode,
            RegionCode = property.RegionCode,
            Timezone = property.Timezone,
            Amenities = property.Amenities.Select(a => a.ToString()).ToList(),
            PhotoUrls = property.PhotoUrls,
            HouseRules = property.HouseRules,
            IsActive = property.IsActive,
            IsPaused = property.IsPaused,
            PausedAt = property.PausedAt,
            RentalMode = property.RentalMode,
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
            BookingsSummary = BuildBookingsSummary(property.Bookings, _clock.TodayInRome()),
            PricingAdapterSummary = property.PricingAdapterConfig == null
                ? new PricingAdapterSummaryDto()
                : new PricingAdapterSummaryDto
                {
                    IsEnabled = property.PricingAdapterConfig.IsEnabled,
                    LastAdaptedAt = property.PricingAdapterConfig.LastAdaptedAt,
                    NextRunOn = property.PricingAdapterConfig.IsEnabled
                        ? SeasonalSuggestionSchedule.NextRunOn(
                            property.PricingAdapterConfig.AdaptationFrequency,
                            property.PricingAdapterConfig.LastAdaptedAt)
                        : null
                }
        };
    }

    /// <summary>
    /// Bookings KPIs of the property detail (A2-36) on the rules of the host dashboard (<see cref="StayKpiRules"/>), so
    /// a cancelled booking is never the next check-in and an arrival of today (Europe/Rome) is upcoming until the host
    /// registers it, then in progress.
    /// </summary>
    public static BookingsSummaryDto BuildBookingsSummary(IEnumerable<Booking> bookings, DateTime todayInRome)
    {
        var all = bookings as IReadOnlyCollection<Booking> ?? bookings.ToList();
        var confirmed = StayKpiRules.IsConfirmedStay().Compile();
        var upcoming = StayKpiRules.UpcomingCheckIn(todayInRome).Compile();
        var inProgress = StayKpiRules.InProgress(todayInRome).Compile();
        var upcomingCheckOut = StayKpiRules.UpcomingCheckOut(todayInRome).Compile();

        return new BookingsSummaryDto
        {
            TotalBookings = all.Count(confirmed),
            UpcomingBookings = all.Count(upcoming),
            ActiveBookings = all.Count(inProgress),
            NextCheckIn = all.Where(upcoming).Select(b => (DateTime?)StayKpiRules.RomeDateOf(b.CheckInDate)).Min(),
            NextCheckOut = all.Where(upcomingCheckOut).Select(b => (DateTime?)StayKpiRules.RomeDateOf(b.CheckOutDate)).Min(),
        };
    }

    public static PropertyDocumentDto MapDocument(PropertyDocument d) => new()
    {
        Id = d.Id,
        FileName = d.FileName,
        FileType = ResolveFileType(d),
        DocumentType = d.DocumentType,
        UploadedAt = d.UploadedAt,
        ApeCode = d.DocumentType == DocumentType.Ape ? d.ApeCode : null,
        ApeEnergyClass = d.DocumentType == DocumentType.Ape ? d.ApeEnergyClass : null,
        // Documents live in the private bucket: the only way to read one is the authenticated
        // download endpoint (bearer token + tenant/ownership check), never the storage reference.
        DownloadUrl = DocumentDownloadPath(d.PropertyId, d.Id)
    };

    /// <summary>API path (relative to the API base URL) of the authenticated document download.</summary>
    public static string DocumentDownloadPath(Guid propertyId, Guid documentId) =>
        $"/api/properties/{propertyId}/documents/{documentId}/download";

    private static string ResolveFileType(PropertyDocument document)
    {
        var extension = Path.GetExtension(document.FileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension.TrimStart('.').ToLowerInvariant();
        }

        return document.DocumentType.ToString();
    }

    internal static CinStatus ResolveCinStatus(string? cinCode) => CinFormat.GetStatus(cinCode);

    public async Task<OwnerCinComplianceResult> GetCinComplianceAsync(
        HostScope scope, string? cinStatus, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!string.IsNullOrWhiteSpace(cinStatus) &&
            cinStatus is not ("valid" or "missing" or "invalid"))
        {
            throw new ArgumentException($"Unknown cinStatus value '{cinStatus}'", nameof(cinStatus));
        }

        var properties = await repository.GetByScopeForComplianceAsync(scope);
        var items = properties.Select(p => new OwnerCinComplianceItem(
            PropertyId: p.Id,
            PropertyName: p.Name,
            CinCode: p.CinCode,
            CinStatus: CinComplianceRules.ResolveStatus(p.CinCode),
            City: p.City)).ToList();

        var valid = items.Count(i => i.CinStatus == "valid");
        var missing = items.Count(i => i.CinStatus == "missing");
        var invalid = items.Count(i => i.CinStatus == "invalid");

        var summary = new CinComplianceSummary(
            Valid: valid,
            Missing: missing,
            Invalid: invalid,
            Deadline: cinDeadline.Today(),
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

        var normalized = CinFormat.Normalize(cinCode);

        if (normalized != null)
        {
            if (!CinFormat.IsValid(normalized))
                throw new DomainRuleException(CinFormat.InvalidFormatCode, CinFormat.InvalidFormatMessageKey);

            if (await repository.CinCodeExistsOnOtherPropertyAsync(normalized, propertyId))
                throw new DomainConflictException("duplicate_cin", "CinAlreadyAssigned");
        }

        property.CinCode = normalized;
        await repository.UpdateAsync(property);
        // A removed CIN suspends an active property (CO-06, A5-20); a valid CIN entered again reactivates a suspended one
        // whose other requirements are complete.
        await complianceStatus.ReevaluateAsync(propertyId);
    }

    public async Task UpdateCadastralDataAsync(Guid propertyId, PropertyCadastralData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var property = await repository.GetByIdAsync(propertyId)
            ?? throw new KeyNotFoundException($"Property {propertyId} not found");

        property.CadastralSheet = Clean(data.Sheet);
        property.CadastralParcel = Clean(data.Parcel);
        property.CadastralSubaltern = Clean(data.Subaltern);
        property.CadastralCategory = Clean(data.Category)?.ToUpperInvariant();
        property.CadastralIncome = data.Income;
        property.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(property);

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task<string> ResolveSlugForCreateAsync(Guid orgId, string name, string? requestedSlug)
    {
        if (!string.IsNullOrWhiteSpace(requestedSlug))
        {
            var normalized = PropertySlugHelper.NormalizeOptional(requestedSlug);
            if (await repository.SlugExistsInOrgAsync(orgId, normalized, null))
                throw new DomainConflictException("duplicate_property_slug", "PropertySlugTaken");
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
        OrgSlug = row.OrgSlug,
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
        OrgSlug = row.OrgSlug,
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
        public string OrgSlug { get; init; } = string.Empty;
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