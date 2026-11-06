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
    /// <summary>Length of <see cref="IstatCode"/>.</summary>
    public const int IstatCodeMaxLength = 6;

    /// <summary>Length of <see cref="City"/>.</summary>
    public const int CityMaxLength = 100;

    /// <summary>Length of <see cref="SourceNote"/>.</summary>
    public const int SourceNoteMaxLength = 500;

    /// <summary>Length of <see cref="EnteredByUserId"/>: the one of <c>User.Id</c>.</summary>
    public const int UserIdMaxLength = 255;

    /// <summary>Lowest amount per person per night a host may declare.</summary>
    public const decimal MinRate = 0.01m;

    /// <summary>Highest amount per person per night a host may declare (no Italian comune is near it).</summary>
    public const decimal MaxRate = 50m;

    /// <summary>The <see cref="TouristTaxRate.SourceAuthority"/> of the transient rate built from this row.</summary>
    public const string HostAuthority = "host";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key, from the org of the caller; never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>ISTAT code of the comune when the property has a trusted one; null otherwise (the comune is then matched by name).</summary>
    [MaxLength(IstatCodeMaxLength)]
    public string? IstatCode { get; set; }

    /// <summary>Name of the comune as the property carries it.</summary>
    [Required, MaxLength(CityMaxLength)]
    public string City { get; set; } = string.Empty;

    /// <summary>Amount per person per night in euros, above zero.</summary>
    [Precision(18, 2)]
    public decimal RatePerPersonPerNight { get; set; }

    /// <summary>Only the first <c>MaxNights</c> nights of a stay are taxed (at least 1); null: no cap.</summary>
    public int? MaxNights { get; set; }

    /// <summary>Guests younger than this age at check-in are exempt (0 to 18).</summary>
    public int MinimumAge { get; set; }

    /// <summary>First day of the season, <c>MM-dd</c>; null together with <see cref="SeasonEnd"/>: the whole year.</summary>
    [MaxLength(5)]
    public string? SeasonStart { get; set; }

    /// <summary>Last day (inclusive) of the season, <c>MM-dd</c>.</summary>
    [MaxLength(5)]
    public string? SeasonEnd { get; set; }

    /// <summary>First day the rate applies (Europe/Rome calendar date).</summary>
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Where the amount comes from (the comune's page, a resolution, the comune's office): required.</summary>
    [Required, MaxLength(SourceNoteMaxLength)]
    public string SourceNote { get; set; } = string.Empty;

    /// <summary>The user who declared the rate.</summary>
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
