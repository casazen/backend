using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The connection of the caller's org to Octorate (<c>GET api/octorate/connection</c>). Never a token, never the state of a consent.
/// </summary>
/// <param name="Status">Connected, Disconnected (also when the org never connected) or Error.</param>
/// <param name="Environment">The Octorate environment of this CasaZen: Test (the sandbox) or Production.</param>
/// <param name="AccountId">Which Octorate account or network the consent covers, for display, when known.</param>
/// <param name="LastSyncedAt">When CasaZen last read the org's account (UTC).</param>
/// <param name="QuotaLimit">The quota of calls Octorate reports, while connected and when it answered.</param>
/// <param name="QuotaRemaining">What Octorate reports as left of the quota.</param>
/// <param name="QuotaWarning">True when what is left is a tenth of the quota or less.</param>
public sealed record OctorateConnectionResponse(
    OctorateConnectionStatus Status,
    OctorateEnvironment Environment,
    string? AccountId,
    DateTime? LastSyncedAt,
    int? QuotaLimit,
    int? QuotaRemaining,
    bool QuotaWarning)
{
    public static OctorateConnectionResponse From(OctorateConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new OctorateConnectionResponse(
            state.Status,
            state.Environment,
            state.AccountId,
            state.LastSyncedAt,
            state.QuotaLimit,
            state.QuotaRemaining,
            state.QuotaWarning);
    }
}

/// <summary>
/// Where to send the host to give the consent (<c>POST api/octorate/connect/start</c>): the Octorate consent page with the client id,
/// the CasaZen callback and the single-use state of this consent. The state travels only inside this address, as OAuth requires.
/// </summary>
/// <param name="AuthorizationUrl">The address of the consent page.</param>
public sealed record OctorateConnectStartResponse(string AuthorizationUrl);

/// <summary>What the CasaZen callback page received from Octorate (<c>POST api/octorate/connect/complete</c>).</summary>
public sealed class OctorateConnectCompleteRequest
{
    /// <summary>The authorization code of the callback.</summary>
    [Required(ErrorMessage = "OctorateCodeRequired")]
    [MaxLength(2048, ErrorMessage = "OctorateCodeRequired")]
    public string Code { get; set; } = string.Empty;

    /// <summary>The state of the callback, the one <c>connect/start</c> put in the consent address.</summary>
    [Required(ErrorMessage = "OctorateStateRequired")]
    [MaxLength(256, ErrorMessage = "OctorateStateRequired")]
    public string State { get; set; } = string.Empty;
}

/// <summary>The outcome of a completed consent: never the code, the state or a token.</summary>
/// <param name="Status">The status of the connection afterwards.</param>
/// <param name="Environment">The Octorate environment of the connection.</param>
/// <param name="PropertyCount">How many accommodations the consent covers.</param>
public sealed record OctorateConnectCompleteResponse(OctorateConnectionStatus Status, OctorateEnvironment Environment, int PropertyCount);
