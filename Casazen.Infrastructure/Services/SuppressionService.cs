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
/// <para>The index of an address is computed over the normalized form (<see cref="ShowcaseBookingRules.NormalizeEmail"/>: trimmed,
/// lowercase). Since E07/F06/US01/T02 a new entry holds the HMAC-SHA256 of <see cref="ICrmContactIndex"/> (keyed by
/// <c>Crm:ContactIndexKey</c>, like <c>ServiceCustomers.EmailHash</c>, NF-95): a copy of the table cannot be used to test addresses. The
/// entries made before it hold the plain SHA-256 of the same form; they are never rewritten or deleted (the address is not there to compute
/// the new index from) and the check reads both (<see cref="LegacyEmailHash"/> is only used to read), so nobody who opted out is written to
/// again. The column <c>EmailHash</c> is the same (64 hex characters): no schema change.</para>
/// <para>Adding an entry also flags the CRM contacts of the org with the same keyed hash (<c>GuestContact.IsSuppressed</c>), in the same
/// <c>SaveChanges</c>. No log line of this class holds an address or a hash: only the org and the source.</para>
/// </remarks>
public sealed class SuppressionService(
    AppDbContext db,
    ICrmContactIndex contactIndex,
    ILogger<SuppressionService> logger) : ISuppressionService
{
    /// <summary>Length of an <c>EmailHash</c>: a SHA-256 or an HMAC-SHA256 in lowercase hex.</summary>
    private const int HashLength = 64;

    public async Task<bool> IsSuppressedAsync(Guid orgId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        var keyedHash = contactIndex.Hash(ShowcaseBookingRules.NormalizeEmail(email));
        var legacyHash = LegacyEmailHash(email);

        return await db.SuppressionListEntries
            .AnyAsync(e => e.OrgId == orgId && (e.EmailHash == keyedHash || e.EmailHash == legacyHash), cancellationToken);
    }

    public async Task AddToSuppressionListAsync(Guid orgId, string email, string source, CancellationToken cancellationToken = default)
    {
        var added = await StageSuppressionAsync(orgId, email, source, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        LogAdded(orgId, source, added);
    }

    public async Task AddByHashAsync(Guid orgId, string emailHash, string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emailHash);
        if (emailHash.Length != HashLength || !emailHash.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A suppression hash is 64 hexadecimal characters.", nameof(emailHash));

        var added = await StageHashAsync(orgId, emailHash.ToLowerInvariant(), source, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        LogAdded(orgId, source, added);
    }

    public async Task<bool> StageSuppressionAsync(Guid orgId, string email, string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        return await StageHashAsync(orgId, contactIndex.Hash(ShowcaseBookingRules.NormalizeEmail(email)), source, cancellationToken);
    }

    /// <summary>
    /// Adds the entry for <paramref name="emailHash"/> to the unit of work unless the list (or the unit of work, not saved yet) already
    /// holds it, and flags the contacts with the same hash whether or not the entry was new: a contact created after the entry is flagged
    /// by the next call, and the call is cheap. Returns whether a new entry was added.
    /// </summary>
    private async Task<bool> StageHashAsync(Guid orgId, string emailHash, string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var now = DateTime.UtcNow;
        var exists = db.SuppressionListEntries.Local.Any(e => e.OrgId == orgId && e.EmailHash == emailHash)
            || await db.SuppressionListEntries.AnyAsync(e => e.OrgId == orgId && e.EmailHash == emailHash, cancellationToken);
        if (!exists)
        {
            db.SuppressionListEntries.Add(new SuppressionListEntry
            {
                OrgId = orgId,
                EmailHash = emailHash,
                Source = source,
                AddedAt = now,
            });
        }

        var contacts = await db.GuestContacts
            .Where(c => c.OrgId == orgId && c.ValueHash == emailHash && !c.IsSuppressed)
            .ToListAsync(cancellationToken);
        foreach (var contact in contacts)
        {
            contact.IsSuppressed = true;
            contact.SuppressedAt = now;
            contact.SuppressedSource = source;
            contact.UpdatedAt = now;
        }

        return !exists;
    }

    private void LogAdded(Guid orgId, string source, bool added)
    {
        if (added)
            logger.LogInformation("Address added to the suppression list: orgId={OrgId}, source={Source}", orgId, source);
        else
            logger.LogInformation("Address already in the suppression list: orgId={OrgId}, source={Source}", orgId, source);
    }

    /// <summary>
    /// The plain SHA-256 of the normalized address (lowercase hex): the index of the entries made before the index had a key, and of the
    /// unsubscribe links issued before it. Used only to read; nothing new is written with it.
    /// </summary>
    internal static string LegacyEmailHash(string email)
    {
        var normalized = ShowcaseBookingRules.NormalizeEmail(email);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
