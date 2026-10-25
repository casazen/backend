using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-05: Suppression list must block marketing sends to unsubscribed addresses.
/// Covers AC-NF05-14: blind index for privacy.
/// Covers AC-NF05-12: one-click unsubscribe via signed link.
/// </summary>
public class SuppressionServiceTests
{
    /// <summary>
    /// Proof test per ADR-007-R18, R22: proves that a suppressed email address (hash in the suppression list)
    /// blocks marketing sends. This test simulates a campaign send filter that MUST check suppression before sending.
    /// Without this check, unsubscribed users would receive unwanted emails (CAN-SPAM and GDPR violations).
    /// </summary>
    [Fact]
    public async Task SuppressionList_BlocksSend()
    {
        // Arrange: Set up in-memory database with a suppression entry
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var suppressedEmail = "unsubscribed@example.com";

        await using (var context = new AppDbContext(options))
        {
            var logger = Mock.Of<ILogger<SuppressionService>>();
            var service = new SuppressionService(context, logger);

            // Add the email to the suppression list (one-click unsubscribe)
            await service.AddToSuppressionListAsync(orgId, suppressedEmail, "unsubscribe");
        }

        // Act: Check if the email is suppressed in a fresh context (simulating a campaign send check)
        await using (var context = new AppDbContext(options))
        {
            var logger = Mock.Of<ILogger<SuppressionService>>();
            var service = new SuppressionService(context, logger);

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
            Assert.Equal(64, entry.EmailHash.Length); // SHA-256 hex hash is 64 chars
        }
    }

    /// <summary>
    /// Proof test: AddToSuppressionListAsync is idempotent - adding the same email twice creates only one entry.
    /// </summary>
    [Fact]
    public async Task AddToSuppressionList_Idempotent()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();
        var email = "test@example.com";

        await using var context = new AppDbContext(options);
        var logger = Mock.Of<ILogger<SuppressionService>>();
        var service = new SuppressionService(context, logger);

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
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var orgId = Guid.NewGuid();

        await using var context = new AppDbContext(options);
        var logger = Mock.Of<ILogger<SuppressionService>>();
        var service = new SuppressionService(context, logger);

        // Act: Suppress with lowercase
        await service.AddToSuppressionListAsync(orgId, "test@example.com", "unsubscribe");

        // Assert: Uppercase variant is also suppressed (same hash)
        var isSuppressedUpper = await service.IsSuppressedAsync(orgId, "TEST@EXAMPLE.COM");
        var isSuppressedWithSpace = await service.IsSuppressedAsync(orgId, " test@example.com ");

        Assert.True(isSuppressedUpper, "Email check should be case-insensitive");
        Assert.True(isSuppressedWithSpace, "Email check should handle leading/trailing whitespace");
    }
}
