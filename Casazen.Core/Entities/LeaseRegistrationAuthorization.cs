using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Casazen.Core.Entities;

[Table("LeaseRegistrationAuthorizations")]
public class LeaseRegistrationAuthorization
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [Required]
    public Guid LeaseContractId { get; set; }

    [ForeignKey(nameof(LeaseContractId))]
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required]
    [MaxLength(200)]
    public string AuthorizerUserId { get; set; } = string.Empty;

    public DateTime AuthorizedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(100)]
    public string Scope { get; set; } = "rli-filing";

    [Required]
    [MaxLength(80)]
    public string TosVersion { get; set; } = string.Empty;

    public bool AttestationAccepted { get; set; }
}
