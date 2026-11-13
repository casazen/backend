using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerContactIndex"/>
/// <remarks>
/// <para>HMAC-SHA256 keyed by <c>OwnerPortal:ContactIndexKey</c> over the address trimmed and lowercased, lowercase hex: the pattern of
/// <see cref="ServiceCustomerIndex"/>. The key is configuration, never the database. Outside Development and Testing the key is required where the owner portal
/// runs (<see cref="OwnerPortalOptionsValidator"/> stops the startup without it); in Development and Testing a fixed key that protects nothing stands in, and the
/// log says so once. Without a key the index never hashes with an empty key (that would be an unkeyed hash of the address): <see cref="HashEmail"/> throws.</para>
/// <para><b>The key must not change</b> while contacts exist: every hash made with the old key would stop matching.</para>
/// </remarks>
public sealed class OwnerContactIndex : IOwnerContactIndex
{
    /// <summary>The key of Development and Testing when none is configured: public on purpose, it protects nothing.</summary>
    internal const string DevelopmentKey = "casazen-development-owner-contact-index-key-not-secret";

    private readonly byte[] _key;

    public OwnerContactIndex(IOptions<OwnerPortalOptions> options, IHostEnvironment environment, ILogger<OwnerContactIndex> logger)
    {
        var configured = options.Value.ContactIndexKey?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            if (OwnerPortalOptionsValidator.RequiresKey(environment))
            {
                // The owner portal is off (the validator refuses the startup when it is on): nothing may index an address with this key.
                logger.LogWarning("OwnerPortal:ContactIndexKey is not configured: the keyed index of the contacts of the owners cannot be computed");
                _key = [];
                return;
            }

            logger.LogWarning(
                "OwnerPortal:ContactIndexKey is not configured: using the fixed development key ({Environment}); never in production",
                environment.EnvironmentName);
            configured = DevelopmentKey;
        }

        _key = Encoding.UTF8.GetBytes(configured);
    }

    public bool IsConfigured => _key.Length > 0;

    public string HashEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (_key.Length == 0)
        {
            // Never an HMAC with an empty key: it would be an unkeyed hash of the address.
            throw new InvalidOperationException("OwnerPortal:ContactIndexKey is not configured (environment variable OwnerPortal__ContactIndexKey).");
        }

        var normalized = email.Trim().ToLowerInvariant();
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
