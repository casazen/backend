using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// CRM profile for guest matching and marketing (NF-23, ADR-021): aggregates a guest's stays, spend,
/// marketing consent and tags across bookings. Matched by SHA-256 composite key of normalized guest data.
/// Excludes Alloggiati submission data per ADR-021-R5.
/// </summary>
/// <remarks>
/// <para>Matching beyond the e-mail (NF-88, E07/F01): the e-mail of a booking made through an online travel agency is a forward address, so
/// the profile also carries two keyed hashes, <see cref="PhoneKeyHash"/> and <see cref="NameBirthKeyHash"/>, each indexed with the org, to
/// find the profiles that share a phone or a name and a birth date. The hashes are keyed, so they say nothing without the key and are not
/// a copy of the data. The index on the org alone is declared on purpose: without it EF would drop the one the list of the profiles of an org
/// reads, now that two composite indexes start with the org. <b>No Alloggiati field</b> (document, citizenship, place of birth) is ever added
/// to the profile (AC-NF88-6, Q1-05).</para>
/// <para>The contacts of the profile (e-mail, phone, WhatsApp, with origin, confirmation and legal basis) are <see cref="GuestContact"/>
/// rows; the duplicates the matching is not sure about are <see cref="GuestMergeCandidate"/> rows.</para>
/// </remarks>
[Table("GuestCrmProfiles")]
[Index(nameof(OrgId), Name = "IX_GuestCrmProfiles_OrgId")]
[Index(nameof(OrgId), nameof(PhoneKeyHash), Name = "IX_GuestCrmProfiles_OrgId_PhoneKeyHash")]
[Index(nameof(OrgId), nameof(NameBirthKeyHash), Name = "IX_GuestCrmProfiles_OrgId_NameBirthKeyHash")]
public class GuestCrmProfile : ITenantOwned
{
    public const int KeyHashLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public string CompositeKeyHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string ConfirmedMarketingEmail { get; set; } = string.Empty;

    public bool MarketingConsentEmail { get; set; }

    public bool MarketingConsentWhatsApp { get; set; }

    [MaxLength(10)]
    public string Language { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;

    public int TotalStays { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSpend { get; set; }

    [MaxLength(2000)]
    public string PropertyIdsJson { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string TagsJson { get; set; } = string.Empty;

    [ConcurrencyCheck]
    public string MarketingCategoryOverridesJson { get; set; } = "{}";

    public DateTime FirstStayAt { get; set; }

    public DateTime LastStayAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Keyed hash (HMAC-SHA256, lowercase hex) of the normalized phone number: finds the profiles of the same org that share a phone
    /// (NF-88, AC-NF88-2) without keeping the number here. <c>null</c> when the profile has no phone. Never the clear data.
    /// </summary>
    [MaxLength(KeyHashLength)]
    public string? PhoneKeyHash { get; set; }

    /// <summary>
    /// Keyed hash (HMAC-SHA256, lowercase hex) of the normalized name and the birth date together: finds the profiles of the same org
    /// that share both (NF-88, AC-NF88-2). <c>null</c> when the name or the birth date is missing. Never the clear data.
    /// </summary>
    [MaxLength(KeyHashLength)]
    public string? NameBirthKeyHash { get; set; }

    public Guid? PrimaryGuestId { get; set; }

    /// <summary>
    /// Tombstone of a merged profile: the id of the profile this one was merged into; <c>null</c> for a profile that is alive. The row is kept
    /// so that what pointed at it can be followed to the profile that took its place. A plain id and not a foreign key, for the same reason.
    /// </summary>
    public Guid? MergedIntoProfileId { get; set; }

    public virtual ICollection<Guest> Guests { get; set; } = new List<Guest>();
}
