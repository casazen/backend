using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("Parties")]
public class Party
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid LeaseContractId { get; set; }

    [ForeignKey(nameof(LeaseContractId))]
    [JsonIgnore]
    public virtual LeaseContract LeaseContract { get; set; } = null!;

    [Required]
    public PartyRole Role { get; set; }

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [MaxLength(16)]
    public string FiscalCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(2)]
    public string Citizenship { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    [EmailAddress]
    public string ContactEmail { get; set; } = string.Empty;

    public bool IsExtraEU { get; set; }

    /// <summary>
    /// When the personal data of the party were anonymized (LT-12): names, fiscal code, citizenship and email replaced
    /// (<c>LeasePartyAnonymizer</c>). <see cref="Role"/> and <see cref="IsExtraEU"/> stay: they keep the shape of the
    /// lease and no longer identify anyone.
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }
}
