using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Onboarding;

// The support vault and the regional ISTAT credentials (E20/F03/US01/T01, decision O5). The values travel in two bodies only: the write of a
// credential (staff into the vault, the host into its ISTAT form) and the one answer of a controlled opening. Every other answer is presence and dates.

/// <summary>
/// The body of <c>POST api/admin/onboarding/assist-requests/{id}/reveal</c>: which credential (<c>OctoratePanel</c>, <c>Police</c>,
/// <c>RegionalIstat</c>) and why (10 to 300 characters, kept in the audit, never shown to the host).
/// </summary>
public sealed class RevealSupportVaultDto
{
    public SupportVaultCredentialKind? Kind { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// The answer of a controlled opening, sent once with <c>Cache-Control: no-store</c>: the values in clear and <c>visibleUntil</c>, the end of the minute
/// the client shows them for. Never stored by CasaZen again, never logged.
/// </summary>
public sealed class SupportVaultRevealDto
{
    public SupportVaultCredentialKind Kind { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>The further code (the web service key of the police credentials, the code of the structure of the ISTAT ones); <c>null</c> when there is none.</summary>
    public string? ExtraCode { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime VisibleUntil { get; set; }

    public string? UnitCode { get; set; }

    public static SupportVaultRevealDto From(SupportVaultReveal reveal) => new()
    {
        Kind = reveal.Kind,
        Username = reveal.Username,
        Password = reveal.Password,
        ExtraCode = reveal.ExtraCode,
        OpenedAt = reveal.OpenedAt,
        VisibleUntil = reveal.VisibleUntil,
        UnitCode = reveal.UnitCode,
    };

    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(SupportVaultRevealDto) + " { *** }";
}

/// <summary>
/// The body of <c>PUT api/admin/onboarding/support-vault</c>: a credential of an org for the vault (the user of the panel of a structure, from the
/// welcome e-mail sent to the CasaZen address of the org; regional ISTAT credentials the host handed to support). The values are checked by the service
/// (422 <c>support_vault_value_invalid</c>): a user name and a password of at most 200 characters, a further code of at most 100.
/// </summary>
public sealed class StoreSupportVaultSecretDto
{
    public Guid? OrgId { get; set; }

    /// <summary>The structure; left out for a credential of the whole org.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary><c>OctoratePanel</c> or <c>RegionalIstat</c>.</summary>
    public SupportVaultSecretKind? Kind { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? ExtraCode { get; set; }

    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(StoreSupportVaultSecretDto) + " { *** }";
}

/// <summary>A credential of the vault as anybody reads it: that it is there, for what, since when and who stored it last. Never a value.</summary>
public sealed class SupportVaultSecretDto
{
    public Guid Id { get; set; }

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    public SupportVaultSecretKind Kind { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string UpdatedByUserId { get; set; } = string.Empty;

    public static SupportVaultSecretDto From(SupportVaultSecretPresence presence) => new()
    {
        Id = presence.Id,
        OrgId = presence.OrgId,
        PropertyId = presence.PropertyId,
        Kind = presence.Kind,
        CreatedAt = presence.CreatedAt,
        UpdatedAt = presence.UpdatedAt,
        UpdatedByUserId = presence.UpdatedByUserId,
    };
}

/// <summary>
/// Body of <c>PUT api/properties/{propertyId}/istat-credentials</c>: the regional ISTAT credentials of the property, the user name and the password
/// required every time (they replace the stored ones), the code of the structure when the portal asks for one. Never returned, never logged.
/// Validation messages are SharedResources keys.
/// </summary>
public sealed class SetIstatCredentialsRequest
{
    [Required(ErrorMessage = "IstatCredentialsUsernameRequired")]
    [MaxLength(PropertyIstatCredentials.MaxUsernameLength, ErrorMessage = "IstatCredentialsUsernameTooLong")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "IstatCredentialsPasswordRequired")]
    [MaxLength(PropertyIstatCredentials.MaxPasswordLength, ErrorMessage = "IstatCredentialsPasswordTooLong")]
    public string Password { get; set; } = string.Empty;

    [MaxLength(PropertyIstatCredentials.MaxStructureCodeLength, ErrorMessage = "IstatCredentialsStructureCodeTooLong")]
    public string? StructureCode { get; set; }

    public IstatCredentialsInput ToInput() => new(Username, Password, StructureCode);

    /// <summary>Hides the values from logs and debugger output.</summary>
    public override string ToString() => nameof(SetIstatCredentialsRequest) + " { *** }";
}

/// <summary>Answer of the ISTAT credentials endpoints: only whether they are configured and since when, never a value (write-only).</summary>
public sealed class IstatCredentialsStatusDto
{
    public bool Configured { get; init; }

    /// <summary>When the credentials were last set (UTC); <c>null</c> when not configured.</summary>
    public DateTime? ConfiguredAt { get; init; }

    public static IstatCredentialsStatusDto From(IstatCredentialsStatus status) => new()
    {
        Configured = status.Configured,
        ConfiguredAt = status.ConfiguredAt,
    };
}
