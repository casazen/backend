using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// A credential CasaZen support keeps for a host in the support vault (E20/F03/US01/T01, decision O5, PO-19): the user of the channel manager
/// panel of a structure CasaZen created (its credentials arrive only at the CasaZen address of the org, never at the host), or regional ISTAT
/// credentials the host handed to support. Opened only on an open «Do it for me» request of the same org and property, with a reason, and every
/// opening is audited (<see cref="SupportVaultAccess"/>).
/// </summary>
/// <remarks>
/// <para><b>Encrypted at rest.</b> <see cref="Username"/>, <see cref="Password"/> and <see cref="ExtraCode"/> go through the Data Protection
/// converter with the purpose <c>Casazen.SupportVault</c> (<c>EncryptedColumns</c>); the columns are <c>text</c> because the payload is longer than
/// the value. Nothing else reads them: the lists answer whether a credential is there and since when, never a value.</para>
/// <para><b>One per org, property and kind.</b> The unique index <see cref="SecretIndexName"/> treats two NULLs as equal (<c>NULLS NOT
/// DISTINCT</c>), so a credential of the whole org (no property) is held once too. A second write of the same one replaces it.</para>
/// <para><b>Erasure.</b> The row follows its org and its property (cascade): erasing either erases the credential, it never outlives them and
/// never blocks their erasure. The users are opaque Auth0 subjects, not foreign keys.</para>
/// </remarks>
[Table("SupportVaultSecrets")]
public class SupportVaultSecret : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="PropertyId"/>, <see cref="Kind"/>).</summary>
    public const string SecretIndexName = "UIX_SupportVaultSecrets_OrgId_PropertyId_Kind";

    /// <summary>Longest <see cref="Username"/> accepted (plain text).</summary>
    public const int MaxUsernameLength = 200;

    /// <summary>Longest <see cref="Password"/> accepted (plain text).</summary>
    public const int MaxPasswordLength = 200;

    /// <summary>Longest <see cref="ExtraCode"/> accepted (plain text).</summary>
    public const int MaxExtraCodeLength = 100;

    /// <summary>Longest user id: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2): the org the credential belongs to. Server-set from the checked request, never trusted as it comes.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The structure the credential is for (FK, CASCADE); <c>null</c> for a credential of the whole org.</summary>
    public Guid? PropertyId { get; set; }

    public SupportVaultSecretKind Kind { get; set; }

    /// <summary>The user name, encrypted at rest.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>The password, encrypted at rest.</summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>A further code the panel or the portal asks for (the code of the structure, a PIN), encrypted at rest; <c>null</c> when there is none.</summary>
    public string? ExtraCode { get; set; }

    /// <summary>Who stored the credential first: the Auth0 subject of the support person.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who replaced it last: the Auth0 subject of the support person.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UpdatedByUserId { get; set; } = string.Empty;

    /// <summary>Last time the values were set or replaced (UTC).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What a credential of the support vault opens (E20/F03/US01/T01). Stored as its number in <c>SupportVaultSecrets.Kind</c>; every member has an
/// explicit value and an existing number never changes.
/// </summary>
public enum SupportVaultSecretKind
{
    /// <summary>The user of the channel manager panel of the structure, from the welcome e-mail sent to the CasaZen address of the org (PO-19).</summary>
    OctoratePanel = 1,

    /// <summary>Regional ISTAT credentials the host handed to support (the host's own form is <see cref="PropertyIstatCredentials"/>).</summary>
    RegionalIstat = 2,
}
