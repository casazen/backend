using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("Users")]
public class User
{
    [Key, MaxLength(255)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required, EmailAddress, MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Phone, MaxLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required]
    public UserRole Role { get; set; } = UserRole.PropertyOwner;

    public RentalType? RentalType { get; set; }

    /// <summary>The org this user belongs to (AC9). Nullable: a brand-new user pre-backfill has none.</summary>
    public Guid? OrgId { get; set; }
    public virtual Org? Org { get; set; }

    /// <summary>
    /// For dual-role users (host + supplier): the supplier org when different from <see cref="OrgId"/>.
    /// Set during auto-provisioning or registration so the supplier context resolver can find
    /// the supplier org without relying on email lookup.
    /// </summary>
    public Guid? SupplierOrgId { get; set; }

    [MaxLength(64)]
    public string? LastUsedContextKey { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when user completed onboarding. Used as source of truth for needsOnboarding() check. Immutable once set.</summary>
    public DateTime? OnboardingCompletedAt { get; set; }

    public ICollection<UserContextMembership> ContextMemberships { get; set; } = new List<UserContextMembership>();
}

public enum UserRole
{
    Admin,
    PropertyOwner,
    PropertyManager,
    Guest,
    Staff,
    LongTermLandlord, // 5 — append only, do not insert before existing values
    Supplier // 6
}

public enum RentalType
{
    ShortTerm,
    LongTerm,
    Both
}