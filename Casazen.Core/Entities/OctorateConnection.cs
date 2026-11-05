using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// The connection of an organization to its Octorate account in one environment (ADR-023, E02/F01/US01/T01): the OAuth tokens
/// the host's consent gave CasaZen and whether they still work. One per organization and environment (unique index
/// <see cref="OrgEnvironmentIndexName"/>): one consent covers every property the Octorate user sees, an agency's whole network
/// included, and every call of the client acts for the org of its scope.
/// </summary>
/// <remarks>
/// <para>The two token properties hold the clear value in memory; the Data Protection value converter of
/// <c>EncryptedColumns</c> (purpose <c>Casazen.Octorate.OAuth</c>) writes the payload to the column, so no code encrypts or
/// decrypts by hand and the database never holds a token in clear (ADR-023-R1). A token is never logged, never returned by an
/// endpoint and never put in an exception message (ADR-023-R13).</para>
/// <para>The refresh token arrives once, with the first authorization (or the creation of a property): it is kept until the host
/// disconnects or Octorate refuses it. A refresh gives a new access token and no new refresh token.</para>
/// </remarks>
[Table("OctorateConnections")]
public class OctorateConnection : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="OrgId"/>, <see cref="Environment"/>).</summary>
    public const string OrgEnvironmentIndexName = "UIX_OctorateConnections_OrgId_Environment";

    /// <summary>Longest <see cref="OctorateAccountId"/>.</summary>
    public const int AccountIdMaxLength = 64;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the organization the consent was given for.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The Octorate environment of the tokens: verified against <c>GET /rest/v1/api/configuration</c> at every connection.</summary>
    public OctorateEnvironment Environment { get; set; }

    /// <summary>
    /// Which Octorate account or network the consent covers, for display and audit only (not part of the key): the network of the
    /// accommodations when they share one, the accommodation when the consent covers exactly one, otherwise empty.
    /// </summary>
    [MaxLength(AccountIdMaxLength)]
    public string? OctorateAccountId { get; set; }

    /// <summary>The access token, in clear in memory and encrypted in the column (no length: the payload is longer than the text).</summary>
    public string? EncryptedAccessToken { get; set; }

    /// <summary>The refresh token, in clear in memory and encrypted in the column; empty once the host disconnects.</summary>
    public string? EncryptedRefreshToken { get; set; }

    /// <summary>When the access token expires (UTC), as Octorate said with the token; <c>null</c> when not known (refreshed before use).</summary>
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>When CasaZen last read the account of the org through this connection (UTC).</summary>
    public DateTime? LastSyncedAt { get; set; }

    public OctorateConnectionStatus Status { get; set; } = OctorateConnectionStatus.Disconnected;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
