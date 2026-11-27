using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The link behind the welcome QR code of a property (NF-90, CRM-03, AC-NF90-3): the guest scans the code, lands on the welcome page of the
/// property and can leave a contact and the consents for it. A property has one active link at a time; a link that is revoked stays in the
/// table, so that the code printed on it stops working for good and is never given to another property.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The token is the address.</b> <see cref="Token"/> is random (192 bits, base64url, so 32 characters), unique in the whole table
/// (revoked links included) and carries nothing about the property: it is not derived from an id and cannot be guessed. It only says which
/// property the page is about.</item>
/// <item><b>One active link for each property</b>: the partial unique index on <see cref="PropertyId"/> where <see cref="RevokedAt"/> is
/// <c>NULL</c>. Revoking the link and creating the new one is the way to change the code.</item>
/// <item><b>Tenant.</b> <see cref="ITenantOwned"/>: <see cref="OrgId"/> is the org of the property, copied from it when the link is created
/// (TN-2). The rows follow their property (cascade).</item>
/// </list>
/// </remarks>
[Table("PropertyWelcomeLinks")]
public class PropertyWelcomeLink : ITenantOwned
{
    /// <summary>Random bytes of a <see cref="Token"/>: 192 bits.</summary>
    public const int TokenBytes = 24;

    /// <summary>Column length of <see cref="Token"/>: room beyond the 32 characters of a base64url token of <see cref="TokenBytes"/> bytes.</summary>
    public const int TokenMaxLength = 64;

    /// <summary>Column length of <see cref="CreatedByUserId"/>: the one of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int CreatedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property when the link is created (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    /// <summary>The random token in the address of the welcome page (base64url, unique).</summary>
    [Required, MaxLength(TokenMaxLength)]
    public string Token { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant the link was revoked; <c>null</c> = the link is the active one of its property.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>The person who created the link (<c>User.Id</c>, the Auth0 subject: a string in this code base, not a Guid); <c>null</c> when the system did.</summary>
    [MaxLength(CreatedByUserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
