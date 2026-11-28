using System.Text.Json.Serialization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The property record returned by <c>GET /api/properties/{id}</c> (A2-32): the fields the edit forms and the pages
/// of one property read, and nothing else. It never carries the bookings of the property (whose check-in tokens are
/// credentials), its OTA integrations, documents, pricing configuration or the internal safety checklist: the
/// bookings are read from the booking endpoints, the rest from <c>GET /api/properties/{id}/detail</c>.
/// </summary>
public sealed class PropertyResponse
{
    public Guid Id { get; init; }
    public string OwnerId { get; init; } = string.Empty;

    public string? ResponsibleUserId { get; init; }

    public Guid OrgId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;

    public string? Unit { get; init; }
    public string City { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int Bedrooms { get; init; }
    public int Bathrooms { get; init; }
    public int MaxGuests { get; init; }
    public decimal NightlyRate { get; init; }

    public int? MinNights { get; init; }

    public decimal WeekendSurchargePercent { get; init; }
    public decimal CleaningFee { get; init; }
    public decimal DamageDeposit { get; init; }
    public IReadOnlyList<PropertyAmenity> Amenities { get; init; } = [];
    public IReadOnlyList<string> PhotoUrls { get; init; } = [];
    public string HouseRules { get; init; } = string.Empty;

    public AccommodationType? AccommodationType { get; init; }

    public int? Beds { get; init; }

    public int? AreaSqm { get; init; }

    public string? ListingTitle { get; init; }

    public bool PublishedOnSite { get; init; }

    public TimeOnly? CheckInFrom { get; init; }

    public TimeOnly? CheckOutBy { get; init; }

    public SmokingPolicy? SmokingPolicy { get; init; }

    public PetsPolicy? PetsPolicy { get; init; }

    public PartiesPolicy? PartiesPolicy { get; init; }

    public ChildrenPolicy? ChildrenPolicy { get; init; }

    public TimeOnly? QuietHoursFrom { get; init; }

    public TimeOnly? QuietHoursTo { get; init; }

    public decimal? ExtraGuestFeePerNight { get; init; }

    public string? CinCode { get; init; }

    /// <summary>ISTAT code of the comune chosen from the official list (SU-04); null until chosen.</summary>
    public string? ComuneIstatCode { get; init; }

    public string? RegionCode { get; init; }

    /// <summary>The CIN is valid and its ISTAT comune differs from <see cref="ComuneIstatCode"/>: a non-blocking warning.</summary>
    public bool CinIstatMismatch { get; init; }

    public string? TicinoStructureId { get; init; }

    /// <summary>
    /// The property is in canton Ticino, so its guests are notified to the cantonal police (notifiche.ti.ch) instead of Alloggiati Web: the canton of
    /// <see cref="SwissPlace"/> (the one resolver, E29/F01/US01/T02). Known only on <c>GET /api/properties/{id}</c>; false on the lists.
    /// </summary>
    public bool IsInTicino { get; init; }

    public string? SwissMunicipalityCode { get; init; }

    public string? SwissMunicipalityName { get; init; }

    public string? SwissCantonCode { get; init; }

    /// <summary>
    /// Where the rules place the property (the one resolver of the place of a Swiss property, NF-124): the stored municipality (<c>source: Stored</c>), or,
    /// without it, the one its postal code and city give (<c>source: Derived</c>: not stored, the form proposes it for confirmation), or
    /// <c>ambiguous: true</c> with no municipality and no canton when the postal code leaves several municipalities open (the form asks the host).
    /// Only on <c>GET /api/properties/{id}</c>, for a property in Switzerland that has a place; left out otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SwissPlaceDto? SwissPlace { get; init; }
    public string Timezone { get; init; } = "Europe/Rome";
    public Guid? CancellationPolicyId { get; init; }
    public int? CancellationFullRefundHours { get; init; }
    public int? CancellationPartialRefundHours { get; init; }
    public decimal? CancellationPartialRefundPercent { get; init; }
    public HostCancellationRefundType CancellationRefundType { get; init; }
    public bool IsActive { get; init; }

    public bool IsPaused { get; init; }

    public DateTime? PausedAt { get; init; }

    public RentalMode RentalMode { get; init; }
    public PropertyComplianceStatus ComplianceStatus { get; init; }
    public DateTime? ComplianceCompletedAt { get; init; }

    public string? CadastralSheet { get; init; }
    public string? CadastralParcel { get; init; }
    public string? CadastralSubaltern { get; init; }
    public string? CadastralCategory { get; init; }
    public decimal? CadastralIncome { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PropertyLifecycleStatus? LifecycleStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PropertyMissingItem>? Missing { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Occupancy30 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PhotoCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverPhotoUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PropertySiteVisibility? SiteVisibility { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SuspensionReasons { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PropertyModeChangeDto? ScheduledModeChange { get; init; }

    public static PropertyResponse From(Property property, PropertyOverview? overview = null, SwissPlace? swissPlace = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        var stored = swissPlace is { Source: SwissPlaceSource.Stored, IsKnown: true } ? swissPlace : null;
        return new PropertyResponse
        {
            LifecycleStatus = overview?.LifecycleStatus,
            Missing = overview?.Missing,
            Occupancy30 = overview?.Occupancy30,
            PhotoCount = overview?.PhotoCount,
            CoverPhotoUrl = overview?.CoverPhotoUrl,
            SiteVisibility = overview?.SiteVisibility,
            SuspensionReasons = overview?.SuspensionReasons,
            ScheduledModeChange = overview?.ScheduledModeChange is { } scheduled ? PropertyModeChangeDto.From(scheduled) : null,
            Id = property.Id,
            OwnerId = property.OwnerId,
            ResponsibleUserId = property.ResponsibleUserId,
            OrgId = property.OrgId,
            Name = property.Name,
            Slug = property.Slug,
            Description = property.Description,
            Address = property.Address,
            Unit = property.Unit,
            City = property.City,
            PostalCode = property.PostalCode,
            Latitude = property.Latitude,
            Longitude = property.Longitude,
            Bedrooms = property.Bedrooms,
            Bathrooms = property.Bathrooms,
            MaxGuests = property.MaxGuests,
            NightlyRate = property.NightlyRate,
            MinNights = property.MinNights,
            WeekendSurchargePercent = property.WeekendSurchargePercent,
            CleaningFee = property.CleaningFee,
            DamageDeposit = property.DamageDeposit,
            Amenities = [.. property.Amenities],
            PhotoUrls = [.. property.PhotoUrls],
            HouseRules = property.HouseRules,
            AccommodationType = property.AccommodationType,
            Beds = property.Beds,
            AreaSqm = property.AreaSqm,
            ListingTitle = property.ListingTitle,
            PublishedOnSite = property.PublishedOnSite,
            CheckInFrom = property.CheckInFrom,
            CheckOutBy = property.CheckOutBy,
            SmokingPolicy = property.SmokingPolicy,
            PetsPolicy = property.PetsPolicy,
            PartiesPolicy = property.PartiesPolicy,
            ChildrenPolicy = property.ChildrenPolicy,
            QuietHoursFrom = property.QuietHoursFrom,
            QuietHoursTo = property.QuietHoursTo,
            ExtraGuestFeePerNight = property.ExtraGuestFeePerNight,
            CinCode = property.CinCode,
            ComuneIstatCode = property.ComuneIstatCode,
            RegionCode = property.RegionCode,
            CinIstatMismatch = CinFormat.HasIstatComuneMismatch(property.CinCode, property.ComuneIstatCode),
            TicinoStructureId = property.TicinoStructureId,
            IsInTicino = swissPlace?.IsInTicino == true,
            SwissMunicipalityCode = property.SwissMunicipalityCode,
            SwissMunicipalityName = stored?.MunicipalityName,
            SwissCantonCode = stored?.CantonCode,
            SwissPlace = swissPlace is null ? null : SwissPlaceDto.From(swissPlace),
            Timezone = property.Timezone,
            CancellationPolicyId = property.CancellationPolicyId,
            CancellationFullRefundHours = property.CancellationFullRefundHours,
            CancellationPartialRefundHours = property.CancellationPartialRefundHours,
            CancellationPartialRefundPercent = property.CancellationPartialRefundPercent,
            CancellationRefundType = property.CancellationRefundType,
            IsActive = property.IsActive,
            IsPaused = property.IsPaused,
            PausedAt = property.PausedAt,
            RentalMode = property.RentalMode,
            ComplianceStatus = property.ComplianceStatus,
            ComplianceCompletedAt = property.ComplianceCompletedAt,
            CadastralSheet = property.CadastralSheet,
            CadastralParcel = property.CadastralParcel,
            CadastralSubaltern = property.CadastralSubaltern,
            CadastralCategory = property.CadastralCategory,
            CadastralIncome = property.CadastralIncome,
            CreatedAt = property.CreatedAt,
            UpdatedAt = property.UpdatedAt,
        };
    }
}

public sealed class SwissPlaceDto
{
    public string? MunicipalityCode { get; init; }

    public string? MunicipalityName { get; init; }

    public string? CantonCode { get; init; }

    public SwissPlaceSource Source { get; init; }

    public bool Ambiguous { get; init; }

    public static SwissPlaceDto From(SwissPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        return new SwissPlaceDto
        {
            MunicipalityCode = place.MunicipalityCode,
            MunicipalityName = place.MunicipalityName,
            CantonCode = place.CantonCode,
            Source = place.Source,
            Ambiguous = place.Ambiguous,
        };
    }
}
