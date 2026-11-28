using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The tourist tax rate a host declares for a comune that has no official one in CasaZen (NF-46, AC-NF46-6, ADR-037-R5): a fixed amount per
/// person per night, with its seasons, the exemption age, the cap of nights and where the amount comes from. Tenant-owned: it is private to the
/// org, never read for another org nor for the public pages, and never used where an official rate (<see cref="TouristTaxRate"/>) is in force.
/// </summary>
[Table("OrgTouristTaxRates")]
public class OrgTouristTaxRate : ITenantOwned
{
    public const int IstatCodeMaxLength = 6;

    public const int CityMaxLength = 100;

    public const int SourceNoteMaxLength = 500;

    public const int UserIdMaxLength = 255;

    public const decimal MinRate = 0.01m;

    public const decimal MaxRate = 50m;

    public const string HostAuthority = "host";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    /// <summary>ISTAT code of the comune when the property has a trusted one; null otherwise (the comune is then matched by name).</summary>
    [MaxLength(IstatCodeMaxLength)]
    public string? IstatCode { get; set; }

    [Required, MaxLength(CityMaxLength)]
    public string City { get; set; } = string.Empty;

    [Precision(18, 2)]
    public decimal RatePerPersonPerNight { get; set; }

    public int? MaxNights { get; set; }

    public int MinimumAge { get; set; }

    [MaxLength(5)]
    public string? SeasonStart { get; set; }

    [MaxLength(5)]
    public string? SeasonEnd { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    [Required, MaxLength(SourceNoteMaxLength)]
    public string SourceNote { get; set; } = string.Empty;

    [Required, MaxLength(UserIdMaxLength)]
    public string EnteredByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// A transient <see cref="TouristTaxRate"/> with the values of this row, for the calculator only: never attached to a context, never
    /// saved. Its id is the id of this row, its authority <see cref="HostAuthority"/> and its verification level third party (T): the
    /// amount is the host's declaration, not an official source.
    /// </summary>
    public TouristTaxRate ToTransientRate() => new()
    {
        Id = Id,
        City = City,
        IstatCode = IstatCode,
        CalculationMethod = TouristTaxCalculationMethod.PerPersonPerNight,
        RatePerPersonPerNight = RatePerPersonPerNight,
        MaxNights = MaxNights,
        MinimumAge = MinimumAge,
        SeasonStart = SeasonStart,
        SeasonEnd = SeasonEnd,
        IsActive = true,
        EffectiveFrom = EffectiveFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        Notes = SourceNote,
        SourceAuthority = HostAuthority,
        VerificationLevel = TouristTaxRateVerification.ThirdParty,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
