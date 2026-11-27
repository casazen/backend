using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Credentials of the Alloggiati Web (Questura) account of a property: username, password and web service key (WSKey)
/// the transmission needs (the web service client is not implemented, see <c>docs/runbooks/alloggiati.md</c>). Write-only for clients (CO-14, A5-30): the API sets or replaces them and answers only
/// whether they are configured and since when, never a value. The three values are encrypted at rest by the EF value
/// converter of <c>AppDbContext</c> (Data Protection, <c>docs/runbooks/encryption.md</c>); the columns are
/// <c>text</c> because the payload is longer than the value.
/// </summary>
/// <remarks>
/// AW-R02 (E03/F01/US05/T01): the row also says which Questura account it belongs to (<see cref="AccountKey"/>, <see cref="Profile"/>,
/// <see cref="ApartmentId"/>), which version of the values it holds, the Questura office, the attestation of the accreditation and the
/// last verification. None of these is a secret and none is encrypted; the last verification is a stable code, never a message.
/// </remarks>
[Table("PropertyQuesturaCredentials")]
public class PropertyQuesturaCredentials : ITenantOwned
{
    public const int MaxUsernameLength = 100;

    public const int MaxPasswordLength = 200;

    public const int MaxWsKeyLength = 200;

    public const int MaxApartmentIdLength = 20;

    public const int MaxQuesturaOfficeNameLength = 200;

    public const int MaxUserIdLength = 255;

    public const int MaxVerificationCodeLength = 100;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    public Guid OrgId { get; set; }

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Alloggiati Web service key (WSKey), encrypted at rest.</summary>
    [Required]
    public string WsKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The Questura account the row belongs to (AW-R02): a key of its own for a single structure, shared by the properties of one
    /// «Gestione Appartamenti» account. An attempt and a receipt keep the key they used, so a later change of the property's account does
    /// not rewrite what was sent. The rows that existed before the column each got a key of their own.
    /// </summary>
    public Guid AccountKey { get; set; } = Guid.NewGuid();

    /// <summary>The profile of the account: <see cref="AlloggiatiProfile.SingleStructure"/> unless the agency chose «Gestione Appartamenti».</summary>
    public AlloggiatiProfile Profile { get; set; } = AlloggiatiProfile.SingleStructure;

    [MaxLength(MaxApartmentIdLength)]
    public string? ApartmentId { get; set; }

    public int CredentialVersion { get; set; } = 1;

    /// <summary>The Questura (office) the account is registered with, as the agency names it (AW-R02: the onboarding works for any Questura).</summary>
    [MaxLength(MaxQuesturaOfficeNameLength)]
    public string? QuesturaOfficeName { get; set; }

    /// <summary>When the agency or the host attested having completed the accreditation with that Questura (AW-R02); <c>null</c> until then.</summary>
    public DateTime? AccreditationAttestedAt { get; set; }

    [MaxLength(MaxUserIdLength)]
    public string? AccreditationAttestedByUserId { get; set; }

    public DateTime? LastVerifiedAt { get; set; }

    public bool? LastVerificationOk { get; set; }

    [MaxLength(MaxVerificationCodeLength)]
    public string? LastVerificationCode { get; set; }
}
