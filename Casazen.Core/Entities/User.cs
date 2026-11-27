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
    public UserRole Role { get; set; } = UserRole.None;

    public RentalType? RentalType { get; set; }

    public Guid? OrgId { get; set; }
    public virtual Org? Org { get; set; }

    public Guid? SupplierOrgId { get; set; }

    [MaxLength(64)]
    public string? LastUsedContextKey { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Auth0 roles removed from the account when it was deactivated (role names such as <c>Admin</c>, <c>Supplier</c>),
    /// given back by the reactivation. Null while the account is active, and when no role could be removed (Auth0
    /// unreachable or not configured at deactivation time: the roles are then still in Auth0).
    /// </summary>
    public List<string>? SuspendedAuth0Roles { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

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
    LongTermLandlord,
    Supplier,

    None,
}

public enum RentalType
{
    ShortTerm,
    LongTerm,
    Both
}