using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceCustomerIndex"/>
/// <remarks>
/// <para>HMAC-SHA256 keyed by <c>Suppliers:CustomerIndexKey</c>, over the address trimmed and lowercased
/// (<see cref="ShowcaseBookingRules.NormalizeEmail"/>), lowercase hex. The key is configuration, never the database: whoever copies
/// the database cannot test addresses against the index. Outside Development and Testing the key is required where the booking
/// can run (<see cref="ShowcaseBookingOptionsValidator"/> stops the startup without it); in Development and Testing a fixed
/// key that protects nothing stands in, and the log says so once.</para>
/// <para><b>The key must not change</b> while customers exist: every index made with the old key would stop matching (the same
/// person would become a new customer and the cap of unverified bookings would forget the old ones). Runbook
/// <c>docs/runbooks/suppliers.md</c>, section 23.</para>
/// </remarks>
public sealed class ServiceCustomerIndex : IServiceCustomerIndex
{
    /// <summary>The key of Development and Testing when none is configured: public on purpose, it protects nothing.</summary>
    internal const string DevelopmentKey = "casazen-development-customer-index-key-not-secret";

    private readonly byte[] _key;

    public ServiceCustomerIndex(
        IOptions<ServiceCustomerIndexOptions> options,
        IHostEnvironment environment,
        ILogger<ServiceCustomerIndex> logger)
    {
        var configured = options.Value.CustomerIndexKey?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            if (ShowcaseBookingOptionsValidator.RequiresKey(environment))
            {
                // The booking is off (the validator refuses the startup when it is on): nothing indexes an address with this key.
                logger.LogWarning(
                    "Suppliers:CustomerIndexKey is not configured: the e-mail index of the customers of the suppliers cannot be computed");
                _key = [];
                return;
            }

            logger.LogWarning(
                "Suppliers:CustomerIndexKey is not configured: using the fixed development key ({Environment}); never in production",
                environment.EnvironmentName);
            configured = DevelopmentKey;
        }

        _key = Encoding.UTF8.GetBytes(configured);
    }

    public string HashEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        if (_key.Length == 0)
        {
            // Never an HMAC with an empty key: it would be an unkeyed hash of the address.
            throw new InvalidOperationException("Suppliers:CustomerIndexKey is not configured.");
        }

        var normalized = ShowcaseBookingRules.NormalizeEmail(email);
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
