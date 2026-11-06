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
    /// <summary>Longest username accepted (plain text).</summary>
    public const int MaxUsernameLength = 100;

    /// <summary>Longest password accepted (plain text).</summary>
    public const int MaxPasswordLength = 200;

    /// <summary>Longest web service key accepted (plain text).</summary>
    public const int MaxWsKeyLength = 200;

    /// <summary>Longest <see cref="ApartmentId"/> (the <c>IdAppartamento</c> of the Questura).</summary>
    public const int MaxApartmentIdLength = 20;

    /// <summary>Longest <see cref="QuesturaOfficeName"/>.</summary>
    public const int MaxQuesturaOfficeNameLength = 200;

    /// <summary>Longest user id (<see cref="AccreditationAttestedByUserId"/>).</summary>
    public const int MaxUserIdLength = 255;

    /// <summary>Longest <see cref="LastVerificationCode"/>.</summary>
    public const int MaxVerificationCodeLength = 100;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey(nameof(Property))]
    public Guid PropertyId { get; set; }
    public virtual Property Property { get; set; } = null!;

    /// <summary>Tenant of the row, copied from <see cref="Property"/> when it is created (TN-2).</summary>
    public Guid OrgId { get; set; }

    /// <summary>Alloggiati Web username, encrypted at rest.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>Alloggiati Web password, encrypted at rest.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Alloggiati Web service key (WSKey), encrypted at rest.</summary>
    [Required]
    public string WsKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time the credentials were set or replaced: the "configured on" date shown to the host.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The Questura account the row belongs to (AW-R02): a key of its own for a single structure, shared by the properties of one
    /// «Gestione Appartamenti» account. An attempt and a receipt keep the key they used, so a later change of the property's account does
    /// not rewrite what was sent. The rows that existed before the column each got a key of their own.
    /// </summary>
    public Guid AccountKey { get; set; } = Guid.NewGuid();

    /// <summary>The profile of the account: <see cref="AlloggiatiProfile.SingleStructure"/> unless the agency chose «Gestione Appartamenti».</summary>
    public AlloggiatiProfile Profile { get; set; } = AlloggiatiProfile.SingleStructure;

    /// <summary>The <c>IdAppartamento</c> of the property in a «Gestione Appartamenti» account; <c>null</c> for a single structure.</summary>
    [MaxLength(MaxApartmentIdLength)]
    public string? ApartmentId { get; set; }

    /// <summary>
    /// The version of the values: 1 for the first ones, one more at every replacement of the username, password or key (AW-R02: the
    /// token cache and the attempts are keyed by it).
    /// </summary>
    public int CredentialVersion { get; set; } = 1;

    /// <summary>The Questura (office) the account is registered with, as the agency names it (AW-R02: the onboarding works for any Questura).</summary>
    [MaxLength(MaxQuesturaOfficeNameLength)]
    public string? QuesturaOfficeName { get; set; }

    /// <summary>When the agency or the host attested having completed the accreditation with that Questura (AW-R02); <c>null</c> until then.</summary>
    public DateTime? AccreditationAttestedAt { get; set; }

    /// <summary>Who attested it: the Auth0 subject of the account, not a foreign key.</summary>
    [MaxLength(MaxUserIdLength)]
    public string? AccreditationAttestedByUserId { get; set; }

    /// <summary>When the credentials were last verified with the service, without sending anything; <c>null</c> before the first verification.</summary>
    public DateTime? LastVerifiedAt { get; set; }

    /// <summary>The outcome of the last verification; <c>null</c> before the first.</summary>
    public bool? LastVerificationOk { get; set; }

    /// <summary>A stable code (snake_case) of the last verification, never a message, a value or a piece of the answer.</summary>
    [MaxLength(MaxVerificationCodeLength)]
    public string? LastVerificationCode { get; set; }
}
