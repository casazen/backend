using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Casazen.Core.SiteConnections;

namespace Casazen.Core.Entities;

/// <summary>
/// A site connected to the org (S022, PLG-BE-CONNECT, site integration plan §10): the credential of the WordPress plugin of <see cref="SiteUrl"/>, limited to
/// the org that issued it and to the operations of its domain (<c>api/site/v1/*</c>, scheme <c>SiteToken</c>). Created by the exchange of a
/// <see cref="SiteConnectionCode"/>, listed and revoked by the billing administrators of the org, revoked by the plugin itself when it disconnects.
/// </summary>
/// <remarks>
/// <para><b>The secret is never stored.</b> The credential (<c>czs_</c> plus 32 random bytes in base64url) is shown once, in the answer of the exchange;
/// the row keeps only its SHA-256 (<see cref="TokenHash"/>, lowercase hex, unique), the value the scheme looks up.</para>
/// <para><b>One active credential for each org and site</b> (<see cref="ActiveSiteIndexName"/>, filtered on <c>RevokedAt IS NULL</c>): connecting the same
/// site again revokes the previous one. A revoked row stays (who, when) and is never active again.</para>
/// </remarks>
[Table("SiteConnections")]
public class SiteConnection : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="TokenHash"/>.</summary>
    public const string TokenHashIndexName = "UIX_SiteConnections_TokenHash";

    /// <summary>Name of the unique index on (org, site) of the active rows (<see cref="RevokedAt"/> null).</summary>
    public const string ActiveSiteIndexName = "UIX_SiteConnections_OrgId_SiteUrl_Active";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The org of the credential (TN-2): the only org it acts on.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The origin of the site (<c>https://www.example.com</c>), as the code had it.</summary>
    [Required, MaxLength(SiteConnectionRules.SiteUrlMaxLength)]
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the credential; unique. The credential itself is never stored.</summary>
    [Required, MaxLength(SiteConnectionTokens.HashLength)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The billing administrator who confirmed the connection: the opaque id of the account.</summary>
    [Required, MaxLength(SiteConnectionRules.UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC of the last call of the plugin, kept to <see cref="SiteConnectionRules.LastUsedResolution"/>; <c>null</c> until the first.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>UTC when the credential stopped working (console, plugin or a newer connection of the same site); <c>null</c> while active.</summary>
    public DateTime? RevokedAt { get; set; }
}
