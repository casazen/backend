using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The per-property data the Octorate activation collects and no other table of the property holds (NF-105, E10/F01/US01/T01): today the
/// Alloggiati Web room numbers, one for each room of the property (the police portal asks for them). One row per property (unique index
/// <see cref="PropertyIndexName"/>), going with the property (cascade) and carrying its org (TN-2, restrict).
/// </summary>
/// <remarks>
/// <b>Not here: the lock code.</b> It stays in the existing encrypted <see cref="PropertyAccessCode"/> (ADR-013); this table never holds a
/// secret. The room numbers are short codes of the building, not personal data. The user is the opaque Auth0 subject (<c>string</c>, not a
/// foreign key).
/// </remarks>
[Table("PropertyOnboardingData")]
public class PropertyOnboardingData : ITenantOwned
{
    public const string PropertyIndexName = "UIX_PropertyOnboardingData_PropertyId";

    public const int MaxRoomNumbers = 20;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>
    /// The Alloggiati Web room numbers, a JSON array of at most <see cref="MaxRoomNumbers"/> short codes; <c>null</c> = none entered yet.
    /// The service that writes it validates the array; the column only keeps it.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? AlloggiatiRoomNumbersJson { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UpdatedByUserId { get; set; } = string.Empty;
}
