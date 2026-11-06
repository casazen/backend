using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICrmContactIndex"/>
/// <remarks>
/// <para>HMAC-SHA256 keyed by <c>Crm:ContactIndexKey</c> over the UTF-8 bytes of the value the caller normalized, lowercase hex: the pattern of
/// <see cref="ServiceCustomerIndex"/> (<c>ServiceCustomers.EmailHash</c>). The key is configuration, never the database: whoever copies the
/// database cannot test addresses against the index. With <c>Features:CrmMarketing</c> on, outside Development and Testing, the key is
/// required where the application starts (<see cref="CrmOptionsValidator"/>); in Development and Testing a fixed key that protects nothing
/// stands in, and the log says so once (the index is a singleton).</para>
/// <para>Without a key outside Development and Testing the index never hashes with an empty key (that would be an unkeyed hash of the value):
/// <see cref="Hash"/> throws and names the variable, and <see cref="IsConfigured"/> is false so that a caller whose own record must not
/// depend on the index can skip it. The application starts in that state while <c>Features:CrmMarketing</c> is off. Nothing here writes the
/// key or a hash to a log.</para>
/// <para><b>The key must not change</b> while contacts or suppression entries exist: every hash made with the old key would stop matching
/// (the same person would become a new contact and an opt-out would stop being honored). Runbook <c>docs/runbooks/crm-marketing.md</c>.</para>
/// </remarks>
public sealed class CrmContactIndex : ICrmContactIndex
{
    /// <summary>The key of Development and Testing when none is configured: public on purpose, it protects nothing.</summary>
    internal const string DevelopmentKey = "casazen-development-crm-contact-index-key-not-secret";

    private readonly byte[] _key;

    public CrmContactIndex(IOptions<CrmOptions> options, IHostEnvironment environment, ILogger<CrmContactIndex> logger)
    {
        var configured = options.Value.ContactIndexKey?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            if (CrmOptionsValidator.RequiresKey(environment))
            {
                // The module is off (the validator refuses the startup when it is on): nothing may index a value with this key.
                logger.LogWarning(
                    "Crm:ContactIndexKey is not configured: the keyed index of the CRM contacts and of the suppression list cannot be computed");
                _key = [];
                return;
            }

            logger.LogWarning(
                "Crm:ContactIndexKey is not configured: using the fixed development key ({Environment}); never in production",
                environment.EnvironmentName);
            configured = DevelopmentKey;
        }

        _key = Encoding.UTF8.GetBytes(configured);
    }

    public bool IsConfigured => _key.Length > 0;

    public string Hash(string normalizedValue)
    {
        ArgumentNullException.ThrowIfNull(normalizedValue);
        if (_key.Length == 0)
        {
            // Never an HMAC with an empty key: it would be an unkeyed hash of the value.
            throw new InvalidOperationException("Crm:ContactIndexKey is not configured (environment variable Crm__ContactIndexKey).");
        }

        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalizedValue))).ToLowerInvariant();
    }
}
