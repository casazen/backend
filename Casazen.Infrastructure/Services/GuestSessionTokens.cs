using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The access token of a guest session (DB-06a, DB-D21): the org of the site, the hash of the guest's email and the end of the
/// session, sealed with ASP.NET Data Protection (authenticated encryption, the key ring of the application shared by every replica
/// and kept in the database). Stateless: nothing is stored, a token is valid while it opens and has not expired. The address is never
/// in the token in clear (only its hash, inside the encrypted payload) and never in a log.
/// </summary>
/// <remarks>
/// The token starts with <see cref="Prefix"/> so the JWT bearer handler of the hosts' sign-in leaves it alone (it is not a JWT) and the
/// guest scheme recognizes it. Format version 1; a later change of the payload bumps the purpose, so old tokens stop working.
/// </remarks>
public sealed class GuestSessionTokens
{
    /// <summary>First characters of every token of a guest session.</summary>
    public const string Prefix = "gs1.";

    /// <summary>Longest token that is even looked at (a real one is a few hundred characters).</summary>
    private const int MaxLength = 1024;

    private const string Purpose = "Casazen.GuestSession.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public GuestSessionTokens(IDataProtectionProvider dataProtection, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        _protector = dataProtection.CreateProtector(Purpose);
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True when <paramref name="value"/> looks like a token of a guest session (it is not checked).</summary>
    public static bool IsGuestToken(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The token of the guest with <paramref name="emailHash"/> on the site of <paramref name="orgId"/> until <paramref name="expiresAt"/> (UTC).</summary>
    public string Issue(Guid orgId, string emailHash, DateTime expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailHash);
        var payload = new Payload(1, orgId, emailHash, new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds());
        return Prefix + _protector.Protect(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>What <paramref name="token"/> proves, or <c>null</c> when it is not ours, was altered, is malformed or has expired.</summary>
    public GuestSessionTicket? Read(string? token)
    {
        if (!IsGuestToken(token) || token!.Length > MaxLength)
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token[Prefix.Length..]), JsonOptions);
            if (payload is not { Version: 1 } || payload.OrgId == Guid.Empty || string.IsNullOrWhiteSpace(payload.EmailHash))
                return null;

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnix).UtcDateTime;
            return expiresAt > _clock.GetUtcNow().UtcDateTime ? new GuestSessionTicket(payload.OrgId, payload.EmailHash, expiresAt) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("o")] Guid OrgId,
        [property: JsonPropertyName("h")] string EmailHash,
        [property: JsonPropertyName("x")] long ExpiresAtUnix);
}
