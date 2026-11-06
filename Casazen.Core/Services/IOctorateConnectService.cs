using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The connection of an organization to its Octorate account (ADR-023, section 9.1 of the source requirements, E02/F01/US02/T01). The
/// host gives the consent on the Octorate site: CasaZen never sees an Octorate username or password. One consent covers every
/// property the Octorate user sees (an agency: its whole network). A host who already authorized and is sent to the consent page again
/// gets a new code at once: that is how to reconnect.
/// </summary>
public interface IOctorateConnectService
{
    /// <summary>
    /// The address of the consent page with a new single-use, expiring <c>state</c> bound to the org, the user and the redirect
    /// address: <c>{AuthorizationEndpoint}?client_id=..&amp;redirect_uri=..&amp;state=..</c>. Only the hash of the state is stored.
    /// </summary>
    Task<string> StartAsync(Guid orgId, string userId, CancellationToken ct);

    /// <summary>
    /// Completes the consent: consumes the state (a reused, expired or foreign state is refused, always with the same answer),
    /// exchanges the code server to server, stores the tokens encrypted at once, checks that the credentials are of this environment
    /// (the connection is left disconnected otherwise) and reads how many properties the consent covers.
    /// </summary>
    Task<OctorateConnectResult> CompleteAsync(Guid orgId, string userId, string code, string state, CancellationToken ct);

    /// <summary>Where the org's connection stands, with the quota Octorate reports while it is connected.</summary>
    Task<OctorateConnectionState> GetStatusAsync(Guid orgId, CancellationToken ct);

    /// <summary>Disconnects the org: no call is made for it until it connects again. Mappings and imported data are kept.</summary>
    Task DisconnectAsync(Guid orgId, CancellationToken ct);
}

/// <summary>The outcome of a completed consent.</summary>
/// <param name="Status">The status of the connection afterwards.</param>
/// <param name="Environment">The Octorate environment of the connection.</param>
/// <param name="PropertyCount">How many accommodations the consent covers.</param>
public sealed record OctorateConnectResult(OctorateConnectionStatus Status, OctorateEnvironment Environment, int PropertyCount);

/// <summary>Where the connection of an org stands. Never a token.</summary>
/// <param name="Status">Connected, Disconnected (also when the org never connected) or Error.</param>
/// <param name="Environment">The Octorate environment of this CasaZen.</param>
/// <param name="AccountId">Which Octorate account or network the consent covers (display only), when known.</param>
/// <param name="LastSyncedAt">When CasaZen last read the org's account (UTC).</param>
/// <param name="QuotaLimit">The quota of the application, when Octorate answered.</param>
/// <param name="QuotaRemaining">What Octorate reports as left of the quota, when it answered.</param>
/// <param name="QuotaWarning">True when what is left is a tenth of the quota or less.</param>
public sealed record OctorateConnectionState(
    OctorateConnectionStatus Status,
    OctorateEnvironment Environment,
    string? AccountId,
    DateTime? LastSyncedAt,
    int? QuotaLimit,
    int? QuotaRemaining,
    bool QuotaWarning);
