namespace Casazen.Core.Services;

/// <summary>
/// The OAuth tokens of the connection of an organization to Octorate (ADR-023-R1, R2, R12, R13): kept encrypted, refreshed one
/// at a time per connection, never logged. The org is always the one the caller works for: inside an authenticated request it must
/// be the org of the request (another org is refused); a job or a webhook handler, which runs with no request, passes the org it
/// works for.
/// </summary>
public interface IOctorateTokenService
{
    /// <summary>
    /// A valid access token of the org's connection in the environment of this CasaZen, refreshed first when it expires within five
    /// minutes. The refresh is single-flight per connection (a PostgreSQL advisory lock). A refresh token Octorate says is not valid
    /// (<c>errRefreshTokenInvalid</c>, <c>invalid_grant</c>) puts the connection in <c>Disconnected</c> with no retry; any other refusal of
    /// the refresh (credentials of the other environment, a wrong client secret) is thrown as its typed error and keeps the tokens.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">An authenticated request asks for another org's token.</exception>
    Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Stores the tokens Octorate gave for the org (the code exchange, the creation of a property, a refresh), encrypted. The refresh
    /// token is saved first, because it arrives once; a <c>null</c> refresh token keeps the stored one, a <c>null</c> access token keeps
    /// the stored one. Creates the connection of the environment of this CasaZen when the org has none, and marks it connected.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">An authenticated request writes another org's tokens.</exception>
    Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct);

    /// <summary>
    /// A new access token for the org after Octorate refused <paramref name="rejectedAccessToken"/> (403 <c>ApiLoginInvalid</c>): refreshed
    /// under the same lock as <see cref="GetAccessTokenAsync"/>, unless another caller already replaced the rejected token, whose new
    /// token is returned. The client calls it once per request, never in a loop.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">An authenticated request asks for another org's token.</exception>
    Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct);

    /// <summary>
    /// Marks the org's connection <c>Disconnected</c> and forgets its access token: no call is made for the org until the host gives the
    /// consent again. The refresh token stays encrypted and unused, because Octorate gives it only with the first authorization and the
    /// reconnection reuses it; it is forgotten when Octorate refuses it. The mappings and the data imported from Octorate are kept.
    /// Nothing happens when the org has no connection.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">An authenticated request disconnects another org.</exception>
    Task DisconnectAsync(Guid orgId, CancellationToken ct);
}

/// <summary>
/// Tokens Octorate answered with. The code exchange gives both; the creation of a property (<c>POST /rest/v2/accommodation</c>) gives
/// only the refresh token; a refresh gives only an access token (no rotation unless asked).
/// </summary>
/// <param name="AccessToken">The access token, <c>null</c> when the answer carries none.</param>
/// <param name="RefreshToken">The refresh token, <c>null</c> when the answer carries none (the stored one is kept).</param>
/// <param name="ExpiresAt">When the access token expires (UTC), read from <c>expires_in</c> or <c>expireDate</c>; <c>null</c> when neither says it.</param>
public sealed record OctorateTokenResponse(string? AccessToken, string? RefreshToken, DateTime? ExpiresAt)
{
    /// <summary>Never prints the tokens (a record would by default): the log of an exception or of a variable must not leak them.</summary>
    public override string ToString() =>
        $"{nameof(OctorateTokenResponse)} {{ AccessToken = {(AccessToken is null ? "none" : "***")}, " +
        $"RefreshToken = {(RefreshToken is null ? "none" : "***")}, ExpiresAt = {ExpiresAt:O} }}";
}
