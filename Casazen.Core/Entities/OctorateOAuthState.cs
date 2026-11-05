using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The OAuth <c>state</c> of a consent the host was sent to give on the Octorate site (section 9.1, E02/F01/US02/T02): bound to the
/// org, the user and the redirect address, valid for a few minutes, used once. Only the SHA-256 of the state is stored
/// (<see cref="StateHash"/>); the plain value is handed out once, in the consent address, and never kept or logged.
/// </summary>
/// <remarks>Single use: the row is deleted when the state is consumed (<c>OctorateOAuthStateService</c>); the expired ones are deleted at
/// every new state.</remarks>
[Table("OctorateOAuthStates")]
public class OctorateOAuthState : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="StateHash"/>.</summary>
    public const string StateHashIndexName = "UIX_OctorateOAuthStates_StateHash";

    /// <summary>Length of <see cref="StateHash"/>: the hex of a SHA-256.</summary>
    public const int StateHashLength = 64;

    public const int UserIdMaxLength = 255;
    public const int RedirectUriMaxLength = 500;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org the consent is for.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The user who started the consent: only that user can complete it.</summary>
    [Required]
    [MaxLength(UserIdMaxLength)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the state, lowercase hex (unique).</summary>
    [Required]
    [MaxLength(StateHashLength)]
    public string StateHash { get; set; } = string.Empty;

    /// <summary>The redirect address the consent was started with: the callback must come back to the same one.</summary>
    [Required]
    [MaxLength(RedirectUriMaxLength)]
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>The Octorate environment the consent was started in.</summary>
    public OctorateEnvironment Environment { get; set; }

    /// <summary>After this instant (UTC) the state is refused.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
