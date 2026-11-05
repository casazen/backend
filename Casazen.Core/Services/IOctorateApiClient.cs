namespace Casazen.Core.Services;

/// <summary>
/// The Octorate API (ADR-023), against the real Octorate sandbox or production: there is no mock and no fixture adapter (PO decision
/// 2026-11-04, ADR-023 amended on 2026-11-05). One method per Octorate operation; a later task adds the operation it needs, with the
/// same error mapping and the same rate-limit gate. No method takes an org: every call acts for <see cref="IOctorateOrgScope.OrgId"/>,
/// which is how tenant isolation is enforced (ADR-023-R4).
/// </summary>
/// <remarks>
/// Errors are typed (<c>Casazen.Infrastructure.Octorate.OctorateException</c> and its subclasses): not found, conflict, refused
/// credentials or token (also a refusal of the guest data before CasaZen's outbound IPs are whitelisted, never an empty result), wrong
/// environment, rate limit, server failure. A token Octorate refuses (403 <c>ApiLoginInvalid</c>) is refreshed and the call made again
/// once; a 429 is retried after 1, 2 and 4 seconds.
/// </remarks>
public interface IOctorateApiClient
{
    /// <summary>The application's configuration: the environment of the credentials, the quota (<c>GET /rest/v1/api/configuration</c>).</summary>
    Task<OctorateConfiguration> GetConfigurationAsync(CancellationToken ct);

    /// <summary>
    /// The accommodations the connection of the org sees (<c>GET /rest/v1/accommodation</c>): with an OAuth consent the ones of that
    /// consent, an agency's whole network included. Every page is read.
    /// </summary>
    Task<OctorateProperty[]> GetPropertiesAsync(CancellationToken ct);

    /// <summary>One accommodation (<c>GET /rest/v1/accommodation/{acc}</c>).</summary>
    Task<OctorateProperty> GetPropertyAsync(string accommodationId, CancellationToken ct);

    /// <summary>Changes the members of <paramref name="update"/> that are set, and nothing else (<c>PATCH /rest/v2/accommodation/{acc}</c>).</summary>
    Task UpdatePropertyAsync(string accommodationId, OctoratePropertyUpdate update, CancellationToken ct);

    /// <summary>
    /// The products of an accommodation, rooms and the rates derived from them, with their guests, bedrooms, bathrooms and beds
    /// (<c>GET /rest/v3/roomrates/{acc}?fields=...</c>; the v3 read requires <c>fields</c>).
    /// </summary>
    Task<OctorateRoom[]> GetRoomsAsync(string accommodationId, CancellationToken ct);

    /// <summary>
    /// The channel connections of an accommodation and the id of the listing on each channel
    /// (<c>GET /rest/v1/connection?accommodation={acc}&amp;fields=id,channelId,channelName,hotelId,status</c>): only those fields are asked,
    /// so the portal credentials a connection holds never reach CasaZen.
    /// </summary>
    Task<OctorateChannelConnection[]> GetConnectionsAsync(string accommodationId, CancellationToken ct);
}
