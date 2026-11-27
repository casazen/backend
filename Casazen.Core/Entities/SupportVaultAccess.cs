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
    public const int ReasonMinLength = 10;

    public const int ReasonMaxLength = 300;

    public const int UserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid RequestId { get; set; }

    public Guid? PropertyId { get; set; }

    public SupportVaultCredentialKind Kind { get; set; }

    [Required]
    [MaxLength(ReasonMaxLength)]
    public string Reason { get; set; } = string.Empty;

    [Required]
    [MaxLength(UserIdMaxLength)]
    public string AccessedByUserId { get; set; } = string.Empty;

    public DateTime AccessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The credentials support can open for a «Do it for me» request (E20/F03/US01/T01). Stored as its number in <c>SupportVaultAccesses.Kind</c>; every
/// member has an explicit value and an existing number never changes.
/// </summary>
public enum SupportVaultCredentialKind
{
    OctoratePanel = 1,

    Police = 2,

    RegionalIstat = 3,
}
