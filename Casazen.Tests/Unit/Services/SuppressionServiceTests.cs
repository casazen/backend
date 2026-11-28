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
        var options = NewOptions();

        var orgId = Guid.NewGuid();
        var suppressedEmail = "unsubscribed@example.com";

        await using (var context = new AppDbContext(options))
        {
            var service = Service(context);

            await service.AddToSuppressionListAsync(orgId, suppressedEmail, "unsubscribe");
        }

        await using (var context = new AppDbContext(options))
        {
            var service = Service(context);

            var isSuppressed = await service.IsSuppressedAsync(orgId, suppressedEmail);
            var isNotSuppressed = await service.IsSuppressedAsync(orgId, "allowed@example.com");

            Assert.True(isSuppressed,
                "Suppressed email address must block marketing sends (one-click unsubscribe honored)");
            Assert.False(isNotSuppressed,
                "Non-suppressed email address should not be blocked");

            var entry = await context.SuppressionListEntries
                .Where(e => e.OrgId == orgId)
                .FirstOrDefaultAsync();
            Assert.NotNull(entry);
            Assert.Equal("unsubscribe", entry.Source);
            Assert.Equal(64, entry.EmailHash.Length);
        }
    }

    /// <summary>
    /// Proof test: AddToSuppressionListAsync is idempotent - adding the same email twice creates only one entry.
    /// </summary>
    [Fact]
    public async Task AddToSuppressionList_Idempotent()
    {
        var options = NewOptions();

        var orgId = Guid.NewGuid();
        var email = "test@example.com";

        await using var context = new AppDbContext(options);
        var service = Service(context);

        await service.AddToSuppressionListAsync(orgId, email, "manual");
        await service.AddToSuppressionListAsync(orgId, email, "manual");

        var count = await context.SuppressionListEntries
            .Where(e => e.OrgId == orgId)
            .CountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SuppressionList_NormalizesEmail()
    {
        var options = NewOptions();

        var orgId = Guid.NewGuid();

        await using var context = new AppDbContext(options);
        var service = Service(context);

        await service.AddToSuppressionListAsync(orgId, "test@example.com", "unsubscribe");

        var isSuppressedUpper = await service.IsSuppressedAsync(orgId, "TEST@EXAMPLE.COM");
        var isSuppressedWithSpace = await service.IsSuppressedAsync(orgId, " test@example.com ");

        Assert.True(isSuppressedUpper, "Email check should be case-insensitive");
        Assert.True(isSuppressedWithSpace, "Email check should handle leading/trailing whitespace");
    }

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

    [Fact]
    public async Task AddByHash_WithoutTheKey_StillRecordsTheOptOut()
    {
        var options = NewOptions();
        var orgId = Guid.NewGuid();
        var keyedHash = CrmTestIndex.Create().Hash("ospite@example.com");
        var legacyHash = PlainSha256("vecchio@example.com");
        await using (var seed = new AppDbContext(options))
        {
            seed.GuestContacts.Add(EmailContact(orgId, keyedHash));
            await seed.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(options))
        {
            var keyless = Service(context, CrmTestIndex.Create(key: null));
            Assert.False(keyless.CanIndexAddresses);
            await keyless.AddByHashAsync(orgId, keyedHash, "unsubscribe");
            await keyless.AddByHashAsync(orgId, legacyHash, "unsubscribe");
        }

        await using var verify = new AppDbContext(options);
        var hashes = await verify.SuppressionListEntries.Where(e => e.OrgId == orgId).Select(e => e.EmailHash).ToListAsync();
        Assert.Equal(2, hashes.Count);
        Assert.Contains(keyedHash, hashes);
        Assert.Contains(legacyHash, hashes);
        var flagged = await verify.GuestContacts.SingleAsync();
        Assert.True(flagged.IsSuppressed);
        Assert.Equal("unsubscribe", flagged.SuppressedSource);

        var keyed = Service(verify);
        Assert.True(keyed.CanIndexAddresses);
        Assert.True(await keyed.IsSuppressedAsync(orgId, "Ospite@Example.com"));
        Assert.True(await keyed.IsSuppressedAsync(orgId, "vecchio@example.com"));
    }
}
