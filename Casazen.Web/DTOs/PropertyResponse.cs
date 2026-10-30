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

    /// <summary>The member in charge of the property (AM-03): the one who is told, with the org's administrators, when something happens on it; <c>null</c> while nobody is named (the creator is told).</summary>
    public string? ResponsibleUserId { get; init; }

    public Guid OrgId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;

    /// <summary>Interno / scala (PC-06); null when the property has none.</summary>
    public string? Unit { get; init; }
    public string City { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int Bedrooms { get; init; }
    public int Bathrooms { get; init; }
    public int MaxGuests { get; init; }
    public decimal NightlyRate { get; init; }

    /// <summary>Fewest nights the public site accepts (DB-03); null = no minimum.</summary>
    public int? MinNights { get; init; }

    /// <summary>Percent added to the nightly rate on the Friday and Saturday nights (DB-03); 0 = no surcharge.</summary>
    public decimal WeekendSurchargePercent { get; init; }
    public decimal CleaningFee { get; init; }
    public decimal DamageDeposit { get; init; }
    public IReadOnlyList<PropertyAmenity> Amenities { get; init; } = [];
    public IReadOnlyList<string> PhotoUrls { get; init; } = [];
    public string HouseRules { get; init; } = string.Empty;

    // The fields of the wizard «Aggiungi immobile» (SR-07a): null = the host never said.

    /// <summary>The kind of accommodation; null = never chosen.</summary>
    public AccommodationType? AccommodationType { get; init; }

    /// <summary>Number of beds; null = not set.</summary>
    public int? Beds { get; init; }

    /// <summary>Living area in square metres; null = not set.</summary>
    public int? AreaSqm { get; init; }

    /// <summary>The headline of the listing; null = the listing uses the name.</summary>
    public string? ListingTitle { get; init; }

    /// <summary>«Mostra la casa sul sito»: <c>false</c> = the host keeps the property off the booking site. It is one condition among the others of the publication, never a way around the activation.</summary>
    public bool PublishedOnSite { get; init; }

    /// <summary>The earliest time guests can arrive, on the property's clock; null = not set.</summary>
    public TimeOnly? CheckInFrom { get; init; }

    /// <summary>The time by which guests must leave, on the property's clock; null = not set.</summary>
    public TimeOnly? CheckOutBy { get; init; }

    /// <summary>What the house rules say about smoking; null = not set.</summary>
    public SmokingPolicy? SmokingPolicy { get; init; }

    /// <summary>What the house rules say about pets; null = not set.</summary>
    public PetsPolicy? PetsPolicy { get; init; }

    /// <summary>What the house rules say about parties and events; null = not set.</summary>
    public PartiesPolicy? PartiesPolicy { get; init; }

    /// <summary>Quiet hours start, on the property's clock; null = no quiet hours.</summary>
    public TimeOnly? QuietHoursFrom { get; init; }

    /// <summary>Quiet hours end, on the property's clock; null = no quiet hours.</summary>
    public TimeOnly? QuietHoursTo { get; init; }

    /// <summary>What the host asks per night for each extra guest, in euros; null = extra guests are not accepted.</summary>
    public decimal? ExtraGuestFeePerNight { get; init; }

    public string? CinCode { get; init; }

    /// <summary>ISTAT code of the comune chosen from the official list (SU-04); null until chosen.</summary>
    public string? ComuneIstatCode { get; init; }

    /// <summary>CasaZen's region code (<c>LOM</c>) that follows <see cref="ComuneIstatCode"/>; null until a comune is chosen.</summary>
    public string? RegionCode { get; init; }

    /// <summary>The CIN is valid and its ISTAT comune differs from <see cref="ComuneIstatCode"/>: a non-blocking warning.</summary>
    public bool CinIstatMismatch { get; init; }
    public string Timezone { get; init; } = "Europe/Rome";
    public Guid? CancellationPolicyId { get; init; }
    public int? CancellationFullRefundHours { get; init; }
    public int? CancellationPartialRefundHours { get; init; }
    public decimal? CancellationPartialRefundPercent { get; init; }
    public HostCancellationRefundType CancellationRefundType { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Host-set pause (PC-03, A2-05): hidden from public bookings until reactivated, own slot and history kept.</summary>
    public bool IsPaused { get; init; }

    /// <summary>UTC instant the property was paused; null when not paused.</summary>
    public DateTime? PausedAt { get; init; }

    /// <summary>How the property is let (PM-01): <c>Short</c> (short stays) or <c>Long</c> (long-term leases), exclusive.</summary>
    public RentalMode RentalMode { get; init; }
    public PropertyComplianceStatus ComplianceStatus { get; init; }
    public DateTime? ComplianceCompletedAt { get; init; }

    /// <summary>Cadastral identification of the unit (LT-10): the lease contract states it; edited with <c>PUT {id}/cadastral</c>.</summary>
    public string? CadastralSheet { get; init; }
    public string? CadastralParcel { get; init; }
    public string? CadastralSubaltern { get; init; }
    public string? CadastralCategory { get; init; }
    public decimal? CadastralIncome { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    // What the host's list adds to the record (SR-07a, GET /api/properties?view=overview). Left out of the JSON everywhere else,
    // so the record of one property and the list of today read exactly as they always did.

    /// <summary>The one state of a short-stay property: <c>NeedsAttention</c> (activation not complete), <c>Active</c>, <c>Paused</c> or <c>Suspended</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PropertyLifecycleStatus? LifecycleStatus { get; init; }

    /// <summary>What it lacks, as the server works it out: <c>blocking</c> ones keep the activation from completing, the others are advice. Never worked out again by the client.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PropertyMissingItem>? Missing { get; init; }

    /// <summary>Percent of the nights of the last 30 days taken (the Home's rule); only for an <c>Active</c> property with nights available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Occupancy30 { get; init; }

    /// <summary>Photos in the gallery.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PhotoCount { get; init; }

    /// <summary>The first photo, the cover; left out without photos.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverPhotoUrl { get; init; }

    /// <summary>Where it stands on the booking site: <c>Published</c> (on the site), <c>Hidden</c> (ready but paused or switched off by the host) or <c>NotReady</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PropertySiteVisibility? SiteVisibility { get; init; }

    /// <summary>Why a <c>Suspended</c> property was suspended: the blocker codes (<c>activation_cin_missing</c>, ...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SuspensionReasons { get; init; }

    /// <summary>
    /// The change of rental mode waiting for its day (PM-02, AM-13b): the same shape as `GET /api/properties/{id}/mode` gives, so the list
    /// can say «passa ad affitto lungo dal …» without asking each property; left out when none is scheduled.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PropertyModeChangeDto? ScheduledModeChange { get; init; }

    public static PropertyResponse From(Property property, PropertyOverview? overview = null)
    {
        ArgumentNullException.ThrowIfNull(property);
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
            QuietHoursFrom = property.QuietHoursFrom,
            QuietHoursTo = property.QuietHoursTo,
            ExtraGuestFeePerNight = property.ExtraGuestFeePerNight,
            CinCode = property.CinCode,
            ComuneIstatCode = property.ComuneIstatCode,
            RegionCode = property.RegionCode,
            CinIstatMismatch = CinFormat.HasIstatComuneMismatch(property.CinCode, property.ComuneIstatCode),
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
