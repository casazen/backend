using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Casazen.Core.SiteConnections;

namespace Casazen.Core.Entities;

/// <summary>
/// The one-time code of the connection of a site to the org (S022, PLG-BE-CONNECT, site integration plan §10): a billing administrator of the org confirms
/// on the console that the site <see cref="SiteUrl"/> may connect, and the WordPress plugin of that site exchanges the code, with the PKCE proof of
/// <see cref="CodeChallenge"/>, for a credential of its own (<see cref="SiteConnection"/>). The code lives five minutes and opens once.
/// </summary>
/// <remarks>
/// <para><b>The secret is never stored.</b> The code travels once in the address WordPress is sent back to; the row keeps only its SHA-256
/// (<see cref="CodeHash"/>, lowercase hex, unique). Useless without the verifier whose S256 is <see cref="CodeChallenge"/>, which is not a secret.</para>
/// <para><b>One use.</b> <see cref="UsedAt"/> is set by one conditional update before anything else is checked: of two requests with the same code only
/// one goes on. The rows expired for more than a day are deleted when a new code is created (no job).</para>
/// <para>No personal data: the account that confirmed is its opaque id (the Auth0 subject), not a foreign key.</para>
/// </remarks>
[Table("SiteConnectionCodes")]
public class SiteConnectionCode : ITenantOwned
{
    public const string CodeHashIndexName = "UIX_SiteConnectionCodes_CodeHash";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(SiteConnectionRules.UserIdMaxLength)]
    public string CreatedByUserId { get; set; } = string.Empty;

    [Required, MaxLength(SiteConnectionRules.SiteUrlMaxLength)]
    public string SiteUrl { get; set; } = string.Empty;

    [Required, MaxLength(SiteConnectionTokens.HashLength)]
    public string CodeHash { get; set; } = string.Empty;

    [Required, MaxLength(SiteConnectionRules.CodeChallengeLength)]
    public string CodeChallenge { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
