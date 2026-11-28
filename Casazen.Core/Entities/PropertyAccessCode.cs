using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Encrypted property access code (NF-13, E06/F03/US01/T01).
/// Stores one encrypted access code per property (gate, lockbox, smart lock PIN).
/// NEVER stored in plain text (ADR-007 encryption pattern, similar to PropertyOwner financial data).
/// Only released to verified guests after biometric or operator verification passes.
/// </summary>
[Table("PropertyAccessCodes")]
public class PropertyAccessCode : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;

    [ForeignKey("Property")]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    [Required]
    public string EncryptedCode { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
