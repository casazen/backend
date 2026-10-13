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

    /// <summary>
    /// Order of the party among the parties of the same role, from 0 (LT-14): the order entered, used by the contract,
    /// the RLI pre-fill and the detail page. Unique per lease and role; leases created before LT-14 have one party per
    /// role at 0 (migration <c>LeaseMultipleParties</c>).
    /// </summary>
    public int Position { get; set; }

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Fiscal code, normalized (upper case, no spaces) and validated at creation (LT-14, <c>ItalianFiscalCode</c>): 16
    /// characters of a natural person or 11 digits. Rows created before LT-14 were only normalized by the migration.
    /// </summary>
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
