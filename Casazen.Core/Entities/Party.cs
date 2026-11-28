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

    public int Position { get; set; }

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

    public string? ContactPhone { get; set; }

    public bool IsExtraEU { get; set; }

    /// <summary>
    /// Residence of a person or registered office of a company, as the contract and the registration write it (municipality and
    /// address; at most <see cref="ResidenceMaxLength"/> characters, LR-12, decision LR-D8). <b>Encrypted at rest</b>
    /// (<c>EncryptedColumns</c>, purpose <c>Casazen.LeaseParty</c>) and, like the fiscal code and the email, never part of a
    /// masked DTO: the API says only that it is there (<c>hasResidence</c>). <c>null</c> for the parties written before LR-12, by
    /// <c>POST api/leases</c>, and after the anonymization. The column has no length of its own: the payload of the encryption is
    /// longer than the text.
    /// </summary>
    public string? Residence { get; set; }

    public DateOnly? DocumentsVerifiedOn { get; set; }

    /// <summary>
    /// When the personal data of the party were anonymized (LT-12): names, fiscal code, citizenship, email, residence and the
    /// verification of the documents replaced or cleared (<c>LeasePartyPrivacyService.AnonymizeParty</c>). <see cref="Role"/> and
    /// <see cref="IsExtraEU"/> stay: they keep the shape of the lease and no longer identify anyone.
    /// </summary>
    public DateTime? AnonymizedAt { get; set; }

    public const int ResidenceMaxLength = 200;
}
