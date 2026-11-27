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
    /// <summary>Name of the unique index on <see cref="PropertyId"/>: a property has one row.</summary>
    public const string PropertyIndexName = "UIX_PropertyOnboardingData_PropertyId";

    /// <summary>Most room numbers a property may hold in <see cref="AlloggiatiRoomNumbersJson"/>.</summary>
    public const int MaxRoomNumbers = 20;

    /// <summary>Longest user id: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (FK, CASCADE: the data goes with the property).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>
    /// The Alloggiati Web room numbers, a JSON array of at most <see cref="MaxRoomNumbers"/> short codes; <c>null</c> = none entered yet.
    /// The service that writes it validates the array; the column only keeps it.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string? AlloggiatiRoomNumbersJson { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who changed the row last: the Auth0 subject of the account.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UpdatedByUserId { get; set; } = string.Empty;
}
