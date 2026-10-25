using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISuppressionService"/>
/// <remarks>
/// Uses SHA-256 blind index (not HMAC) to check if an email is suppressed without storing the plaintext.
/// The hash is computed over the normalized email (trim + lowercase) per ShowcaseBookingRules.NormalizeEmail.
/// </remarks>
public sealed class SuppressionService(
    AppDbContext db,
    ILogger<SuppressionService> logger) : ISuppressionService
{
    public async Task<bool> IsSuppressedAsync(Guid orgId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        var emailHash = ComputeEmailHash(email);

        var exists = await db.SuppressionListEntries
            .Where(e => e.OrgId == orgId && e.EmailHash == emailHash)
            .AnyAsync(cancellationToken);

        return exists;
    }

    public async Task AddToSuppressionListAsync(Guid orgId, string email, string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(source);

        var emailHash = ComputeEmailHash(email);

        // Check if already suppressed (idempotent)
        var existing = await db.SuppressionListEntries
            .Where(e => e.OrgId == orgId && e.EmailHash == emailHash)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "Email already suppressed: orgId={OrgId}, emailHash={EmailHash}, source={Source}",
                orgId, emailHash, source);
            return;
        }

        // Add to suppression list
        var entry = new SuppressionListEntry
        {
            OrgId = orgId,
            EmailHash = emailHash,
            Source = source,
            AddedAt = DateTime.UtcNow
        };

        db.SuppressionListEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Email added to suppression list: orgId={OrgId}, emailHash={EmailHash}, source={Source}",
            orgId, emailHash, source);
    }

    /// <summary>
    /// Computes SHA-256 hash of the normalized email address (blind index).
    /// Not HMAC - just SHA-256 of the normalized string.
    /// </summary>
    private static string ComputeEmailHash(string email)
    {
        var normalized = ShowcaseBookingRules.NormalizeEmail(email);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
