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
    public const int TokenBytes = 24;

    public const int TokenMaxLength = 64;

    public const int CreatedByUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    [Required, MaxLength(TokenMaxLength)]
    public string Token { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAt { get; set; }

    [MaxLength(CreatedByUserIdMaxLength)]
    public string? CreatedByUserId { get; set; }
}
