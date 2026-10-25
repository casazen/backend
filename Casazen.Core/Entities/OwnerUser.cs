using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

/// <summary>
/// Owner identity across agencies (ADR-011-R1).
/// NOT ITenantOwned: one owner can have properties at multiple agencies.
/// PropertyOwnership links OwnerUser to properties and provides tenant scoping.
/// </summary>
[Table("OwnerUsers")]
public class OwnerUser
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Auth0 user ID (sub claim). Unique per owner.</summary>
    [Required]
    [MaxLength(128)]
    public string Auth0Sub { get; set; } = string.Empty;

    /// <summary>Owner email address. Used for invitation and login.</summary>
    [Required]
    [MaxLength(255)]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>Owner first name.</summary>
    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Owner last name.</summary>
    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>When this OwnerUser was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When this owner last logged in to the owner portal (UTC). Null if never logged in.</summary>
    public DateTime? LastLoginAt { get; set; }
}
