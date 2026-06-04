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

    [MaxLength(64)]
    public string? LastUsedContextKey { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserContextMembership> ContextMemberships { get; set; } = new List<UserContextMembership>();
}

public enum UserRole
{
    Admin,
    PropertyOwner,
    PropertyManager,
    Guest,
    Staff,
    LongTermLandlord // 5 — append only, do not insert before existing values
}