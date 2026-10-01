using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;

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
    public Guid OrgId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Slug { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string PostalCode { get; init; } = string.Empty;
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public int Bedrooms { get; init; }
    public int Bathrooms { get; init; }
    public int MaxGuests { get; init; }
    public decimal NightlyRate { get; init; }
    public decimal CleaningFee { get; init; }
    public decimal DamageDeposit { get; init; }
    public IReadOnlyList<PropertyAmenity> Amenities { get; init; } = [];
    public IReadOnlyList<string> PhotoUrls { get; init; } = [];
    public string HouseRules { get; init; } = string.Empty;
    public string? CinCode { get; init; }
    public string Timezone { get; init; } = "Europe/Rome";
    public Guid? CancellationPolicyId { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Host-set pause (PC-03, A2-05): hidden from public bookings until reactivated, own slot and history kept.</summary>
    public bool IsPaused { get; init; }

    /// <summary>UTC instant the property was paused; null when not paused.</summary>
    public DateTime? PausedAt { get; init; }
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

    public static PropertyResponse From(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new PropertyResponse
        {
            Id = property.Id,
            OwnerId = property.OwnerId,
            OrgId = property.OrgId,
            Name = property.Name,
            Slug = property.Slug,
            Description = property.Description,
            Address = property.Address,
            City = property.City,
            PostalCode = property.PostalCode,
            Latitude = property.Latitude,
            Longitude = property.Longitude,
            Bedrooms = property.Bedrooms,
            Bathrooms = property.Bathrooms,
            MaxGuests = property.MaxGuests,
            NightlyRate = property.NightlyRate,
            CleaningFee = property.CleaningFee,
            DamageDeposit = property.DamageDeposit,
            Amenities = [.. property.Amenities],
            PhotoUrls = [.. property.PhotoUrls],
            HouseRules = property.HouseRules,
            CinCode = property.CinCode,
            Timezone = property.Timezone,
            CancellationPolicyId = property.CancellationPolicyId,
            IsActive = property.IsActive,
            IsPaused = property.IsPaused,
            PausedAt = property.PausedAt,
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
