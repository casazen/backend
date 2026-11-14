using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// One opening of a credential of the support vault (E20/F03/US01/T01, decision O5): who opened which kind of credential, when, for which «Do it
/// for me» request and why. <b>Never the value</b>: the row holds ids, a kind, an instant and the reason the support person wrote. It is also the
/// count of the limit of openings of a request (<c>SupportVaultLimits.MaxOpeningsPerRequestPerHour</c>).
/// </summary>
/// <remarks>
/// The row carries the org of the request (TN-2) and follows the request and the org (cascade): erasing the org erases its vault, the audit with
/// it. The users are opaque Auth0 subjects, not foreign keys; the property is a plain copy of the request's, not a foreign key.
/// </remarks>
[Table("SupportVaultAccesses")]
public class SupportVaultAccess : ITenantOwned
{
    /// <summary>Shortest <see cref="Reason"/>, once trimmed.</summary>
    public const int ReasonMinLength = 10;

    /// <summary>Longest <see cref="Reason"/>, once trimmed.</summary>
    public const int ReasonMaxLength = 300;

    /// <summary>Longest user id: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row (TN-2): the org of the request, never the support person's.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The «Do it for me» request the credential was opened for (FK, CASCADE).</summary>
    public Guid RequestId { get; set; }

    /// <summary>The property of the request when it has one (a copy, not a foreign key).</summary>
    public Guid? PropertyId { get; set; }

    public SupportVaultCredentialKind Kind { get; set; }

    /// <summary>Why the support person opened it (10 to 300 characters), as it wrote it. Never shown to the host and never logged.</summary>
    [Required]
    [MaxLength(ReasonMaxLength)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Who opened it: the Auth0 subject of the support person.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string AccessedByUserId { get; set; } = string.Empty;

    /// <summary>When it was opened (UTC).</summary>
    public DateTime AccessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The credentials support can open for a «Do it for me» request (E20/F03/US01/T01). Stored as its number in <c>SupportVaultAccesses.Kind</c>; every
/// member has an explicit value and an existing number never changes.
/// </summary>
public enum SupportVaultCredentialKind
{
    /// <summary>The user of the channel manager panel of the structure (<see cref="SupportVaultSecretKind.OctoratePanel"/>).</summary>
    OctoratePanel = 1,

    /// <summary>The Alloggiati Web credentials of the property (<see cref="PropertyQuesturaCredentials"/>).</summary>
    Police = 2,

    /// <summary>The regional ISTAT credentials of the property (<see cref="PropertyIstatCredentials"/>, else <see cref="SupportVaultSecretKind.RegionalIstat"/>).</summary>
    RegionalIstat = 3,
}
