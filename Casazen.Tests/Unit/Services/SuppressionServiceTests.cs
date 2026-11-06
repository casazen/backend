using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-05: Suppression list must block marketing sends to unsubscribed addresses.
/// Covers AC-NF05-14: blind index for privacy.
/// Covers AC-NF05-12: one-click unsubscribe via signed link.
/// E07/F06/US01/T02 (NF-95): the index is keyed (HMAC-SHA256 by <c>Crm:ContactIndexKey</c>), the entries made with the plain SHA-256 before
/// it are still honored, and the contacts with the same hash are flagged.
/// </summary>
public class SuppressionServiceTests
{
    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

    private static SuppressionService Service(AppDbContext context, ICrmContactIndex? index = null) =>
        new(context, index ?? CrmTestIndex.Create(), NullLogger<SuppressionService>.Instance);

    /// <summary>The plain SHA-256 of an address already normalized: the form of the entries made before the index had a key.</summary>
    private static string PlainSha256(string normalizedEmail) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail))).ToLowerInvariant();

    private static GuestContact EmailContact(Guid orgId, string valueHash) => new()
    {
        OrgId = orgId,
        Channel = GuestContactChannel.Email,
        Value = "ospite@example.com",
        ValueHash = valueHash,
        Origin = GuestContactOrigin.CheckIn,
    };

    /// <summary>
    /// Proof test per ADR-007-R18, R22: proves that a suppressed email address (hash in the suppression list)
    /// blocks marketing sends. This test simulates a campaign send filter that MUST check suppression before sending.
    /// Without this check, unsubscribed users would receive unwanted emails (CAN-SPAM and GDPR violations).
    /// </summary>
    [Fact]
    public async Task SuppressionList_BlocksSend()
    {
        // Arrange: Set up in-memory database with a suppression entry
        var options = NewOptions();

        var orgId = Guid.NewGuid();
        var suppressedEmail = "unsubscribed@example.com";

        await using (var context = new AppDbContext(options))
        {
            var service = Service(context);

            // Add the email to the suppression list (one-click unsubscribe)
            await service.AddToSuppressionListAsync(orgId, suppressedEmail, "unsubscribe");
        }

        // Act: Check if the email is suppressed in a fresh context (simulating a campaign send check)
        await using (var context = new AppDbContext(options))
        {
            var service = Service(context);

            var isSuppressed = await service.IsSuppressedAsync(orgId, suppressedEmail);
            var isNotSuppressed = await service.IsSuppressedAsync(orgId, "allowed@example.com");

            // Assert: Suppressed email MUST block sends
            Assert.True(isSuppressed,
                "Suppressed email address must block marketing sends (one-click unsubscribe honored)");
            Assert.False(isNotSuppressed,
                "Non-suppressed email address should not be blocked");

            // Verify the suppression list entry exists
            var entry = await context.SuppressionListEntries
                .Where(e => e.OrgId == orgId)
                .FirstOrDefaultAsync();
            Assert.NotNull(entry);
            Assert.Equal("unsubscribe", entry.Source);
            Assert.Equal(64, entry.EmailHash.Length); // SHA-256 and HMAC-SHA256 hex hashes are 64 chars
        }
    }

    /// <summary>
    /// Proof test: AddToSuppressionListAsync is idempotent - adding the same email twice creates only one entry.
    /// </summary>
    [Fact]
    public async Task AddToSuppressionList_Idempotent()
    {
        // Arrange
        var options = NewOptions();

        var orgId = Guid.NewGuid();
        var email = "test@example.com";

        await using var context = new AppDbContext(options);
        var service = Service(context);

        // Act: Add the same email twice
        await service.AddToSuppressionListAsync(orgId, email, "manual");
        await service.AddToSuppressionListAsync(orgId, email, "manual");

        // Assert: Only one entry should exist
        var count = await context.SuppressionListEntries
            .Where(e => e.OrgId == orgId)
            .CountAsync();
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Proof test: Email normalization (trim + lowercase) works correctly.
    /// Two variants of the same email (different case, whitespace) produce the same hash.
    /// </summary>
    [Fact]
    public async Task SuppressionList_NormalizesEmail()
    {
        // Arrange
        var options = NewOptions();

        var orgId = Guid.NewGuid();

        await using var context = new AppDbContext(options);
        var service = Service(context);

        // Act: Suppress with lowercase
        await service.AddToSuppressionListAsync(orgId, "test@example.com", "unsubscribe");

        // Assert: Uppercase variant is also suppressed (same hash)
        var isSuppressedUpper = await service.IsSuppressedAsync(orgId, "TEST@EXAMPLE.COM");
        var isSuppressedWithSpace = await service.IsSuppressedAsync(orgId, " test@example.com ");

        Assert.True(isSuppressedUpper, "Email check should be case-insensitive");
        Assert.True(isSuppressedWithSpace, "Email check should handle leading/trailing whitespace");
    }

    /// <summary>
    /// Proof test (privacy rule, NF-95 / AC-NF95-3): the value stored for a new entry is the HMAC of the normalized address keyed by
    /// <c>Crm:ContactIndexKey</c>, not its plain SHA-256: a copy of the table cannot be used to test addresses, and another key gives another value.
    /// </summary>
    [Fact]
    public async Task Suppression_NewEntry_StoresKeyedHashNotPlainSha256_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();

        await using (var context = new AppDbContext(options))
            await Service(context).AddToSuppressionListAsync(orgId, "  Mario.Rossi@Example.com ", "unsubscribe");

        await using var verify = new AppDbContext(options);
        var entry = await verify.SuppressionListEntries.SingleAsync(e => e.OrgId == orgId);
        Assert.Matches("^[0-9a-f]{64}$", entry.EmailHash);
        Assert.NotEqual(PlainSha256("mario.rossi@example.com"), entry.EmailHash);
        Assert.Equal(CrmTestIndex.Create().Hash("mario.rossi@example.com"), entry.EmailHash);
        Assert.NotEqual(CrmTestIndex.Create(CrmTestIndex.Key + "-another").Hash("mario.rossi@example.com"), entry.EmailHash);
    }

    /// <summary>
    /// Proof test (data rule, AC-NF95-3 and AC-NF93-4): an opt-out stored before the change, as the plain SHA-256 of the address, keeps
    /// blocking the send, so nobody who opted out is written to again; only for the org that holds it, and only for that address.
    /// </summary>
    [Fact]
    public async Task Suppression_LegacyPlainHashEntry_StillSuppresses_Test()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            seed.SuppressionListEntries.Add(new SuppressionListEntry
            {
                OrgId = orgId,
                EmailHash = PlainSha256("legacy@example.com"),
                Source = "unsubscribe",
            });
            await seed.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);
        var service = Service(context);

        Assert.True(await service.IsSuppressedAsync(orgId, "Legacy@Example.com "));
        Assert.False(await service.IsSuppressedAsync(orgId, "someone.else@example.com"));
        Assert.False(await service.IsSuppressedAsync(Guid.NewGuid(), "legacy@example.com"));
    }

    /// <summary>
    /// AC-NF95-3 and AC-NF89-4 for the signed link: the hash it carries is stored as given (keyed for a new link, the plain SHA-256 for an
    /// older one), once however many times it is posted, and the contacts of the org with the same keyed hash are flagged as suppressed with the
    /// source <c>unsubscribe</c>; the contact of another org with the same hash is not touched.
    /// </summary>
    [Fact]
    public async Task AddByHash_StoresTheGivenHashOnce_AndFlagsTheContactsOfThatOrgWithTheSameKeyedHash()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        var keyedHash = CrmTestIndex.Create().Hash("ospite@example.com");
        var legacyHash = PlainSha256("vecchio@example.com");
        await using (var seed = new AppDbContext(options))
        {
            seed.GuestContacts.AddRange(EmailContact(orgId, keyedHash), EmailContact(otherOrgId, keyedHash));
            await seed.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(options))
        {
            var service = Service(context);
            await service.AddByHashAsync(orgId, keyedHash, "unsubscribe");
            await service.AddByHashAsync(orgId, keyedHash, "unsubscribe");
            await service.AddByHashAsync(orgId, legacyHash, "unsubscribe");
        }

        await using var verify = new AppDbContext(options);
        var hashes = await verify.SuppressionListEntries.Where(e => e.OrgId == orgId).Select(e => e.EmailHash).ToListAsync();
        Assert.Equal(2, hashes.Count);
        Assert.Contains(keyedHash, hashes);
        Assert.Contains(legacyHash, hashes);

        var flagged = await verify.GuestContacts.SingleAsync(c => c.OrgId == orgId);
        Assert.True(flagged.IsSuppressed);
        Assert.Equal("unsubscribe", flagged.SuppressedSource);
        Assert.NotNull(flagged.SuppressedAt);
        Assert.False((await verify.GuestContacts.SingleAsync(c => c.OrgId == otherOrgId)).IsSuppressed);
        Assert.False(await verify.SuppressionListEntries.AnyAsync(e => e.OrgId == otherOrgId));
    }

    /// <summary>The address added through the service flags the contact with its keyed hash in the same save; a malformed hash is refused.</summary>
    [Fact]
    public async Task AddToSuppressionList_FlagsTheContactOfTheAddress_AndAMalformedHashIsRefused()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            seed.GuestContacts.Add(EmailContact(orgId, CrmTestIndex.Create().Hash("ospite@example.com")));
            await seed.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(options))
        {
            var service = Service(context);
            await service.AddToSuppressionListAsync(orgId, "Ospite@Example.com", "manual");
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddByHashAsync(orgId, "not-a-hash", "unsubscribe"));
        }

        await using var verify = new AppDbContext(options);
        var contact = await verify.GuestContacts.SingleAsync();
        Assert.True(contact.IsSuppressed);
        Assert.Equal("manual", contact.SuppressedSource);
    }

    /// <summary>Outside Development and Testing, without the key, nothing is hashed with an empty key: the call fails and nothing is written.</summary>
    [Fact]
    public async Task AddToSuppressionList_WithoutTheKey_FailsAndWritesNothing()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();

        await using (var context = new AppDbContext(options))
        {
            var service = Service(context, CrmTestIndex.Create(key: null));
            Assert.False(service.CanIndexAddresses);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddToSuppressionListAsync(orgId, "ospite@example.com", "manual"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.IsSuppressedAsync(orgId, "ospite@example.com"));
        }

        await using var verify = new AppDbContext(options);
        Assert.False(await verify.SuppressionListEntries.AnyAsync());
    }
}
