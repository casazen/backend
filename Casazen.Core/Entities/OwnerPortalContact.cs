using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A person an owner lets into its owner portal for one owner profile of one agency (E07/F07/US01/T02, AC-NF111-4): a relative, an accountant, a manager.
/// It signs in with <b>its own</b> account (an <see cref="OwnerUser"/> of its own, never the owner's login) and holds only the permissions the owner gave it
/// (<see cref="Permissions"/>), each independent of the others, on the ownerships in force of that owner profile. A withdrawal is effective from the next
/// request (every request resolves the access again) and the row stays for the audit.
/// </summary>
[Table("OwnerPortalContacts")]
public class OwnerPortalContact : ITenantOwned
{
    public const int EmailMaxLength = 255;
    public const int NameMaxLength = 100;
    public const int HashLength = 64;
    public const int ActorMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant key: the agency of the owner profile.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The owner profile the contact acts for (<c>PropertyOwner.Id</c>).</summary>
    public Guid PropertyOwnerId { get; set; }

    /// <summary>The invited address, normalized (trimmed, lower case): the account that accepts must hold it, verified.</summary>
    [Required]
    [MaxLength(EmailMaxLength)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>What the contact may do (a mask of <see cref="OwnerPortalPermission"/>); never empty.</summary>
    public OwnerPortalPermission Permissions { get; set; }

    public OwnerPortalContactStatus Status { get; set; } = OwnerPortalContactStatus.Invited;

    /// <summary>SHA-256 (lower-case hex) of the secret of the invitation link; the secret itself is never stored.</summary>
    [Required]
    [MaxLength(HashLength)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>When the link of the invitation lapses (UTC).</summary>
    public DateTime InvitationExpiresAt { get; set; }

    /// <summary>The owner who invited (<c>OwnerUser.Id</c>).</summary>
    public Guid InvitedByOwnerUserId { get; set; }

    /// <summary>The account of the contact once it accepted (<c>OwnerUser.Id</c>).</summary>
    public Guid? OwnerUserId { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>Who withdrew it: <c>User.Id</c> of the agency, or <c>owner:{OwnerUser.Id}</c>.</summary>
    [MaxLength(ActorMaxLength)]
    public string? RevokedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
