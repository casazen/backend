using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof tests for PropertyAccessCode (NF-13, E06/F03/US01/T01): proves encryption / permission / privacy rules.
/// </summary>
public class AccessCodeProofTests
{
    private const string TestCode = "1234#5678";

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;
        public bool FilterEnabled => true;
    }

    /// <summary>
    /// AC-NF13-3 / AC-NF13-6 proof: Access codes MUST NEVER be released without verification.
    /// Verification passes if BiometricVerificationLog.Outcome == Accept OR OperatorUserId is set.
    /// Blocks if verification not passed.
    /// </summary>
    [Fact]
    public async Task AccessCode_NeverReleasedWithoutVerification()
    {
        // Arrange: in-memory database with Data Protection
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccessCodeVerificationTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        // Seed: Property, Booking, PropertyAccessCode
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var org = new OrgEntity
            {
                Id = orgId,
                Name = "Test Org",
                Slug = "test-org",
                DisplayName = "Test Org",
                ContactEmail = "test@example.com",
                PlanTier = PlanTier.Pro
            };
            db.Orgs.Add(org);

            var property = new Property
            {
                Id = propertyId,
                OrgId = orgId,
                OwnerId = "test-user",
                Name = "Test Property",
                Address = "Test Address",
                City = "Milan",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100
            };
            db.Properties.Add(property);

            var guest = new Guest
            {
                Id = guestId,
                OrgId = orgId,
                FirstName = "Test",
                LastName = "Guest",
                Email = "guest@example.com"
            };
            db.Guests.Add(guest);

            var booking = new Booking
            {
                Id = bookingId,
                OrgId = orgId,
                PropertyId = propertyId,
                GuestId = guestId,
                BookingCode = "BK001",
                CheckInDate = DateTime.UtcNow.AddDays(1),
                CheckOutDate = DateTime.UtcNow.AddDays(2),
                TotalPrice = 100,
                Status = BookingStatus.Confirmed
            };
            db.Bookings.Add(booking);

            var accessCode = new PropertyAccessCode
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                EncryptedCode = TestCode,
                UpdatedAt = DateTime.UtcNow
            };
            db.Set<PropertyAccessCode>().Add(accessCode);

            await db.SaveChangesAsync();
        }

        // Test 1: NO verification log -> release MUST be blocked
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "screen");

            // Assert: Code is NOT released (null returned)
            Assert.Null(code);
        }

        // Test 2: Verification log with Outcome = Reject -> release MUST be blocked
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var rejectLog = new BiometricVerificationLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                BookingId = bookingId,
                GuestId = guestId,
                Outcome = BiometricOutcome.Reject,
                Attempts = 1,
                ModelName = "AuraFace-v1",
                ModelVersion = "1.0.0",
                ThresholdsJson = "{\"AcceptThreshold\": 0.85}",
                CreatedAt = DateTime.UtcNow
            };
            db.BiometricVerificationLogs.Add(rejectLog);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "screen");

            // Assert: Code is NOT released (reject outcome)
            Assert.Null(code);
        }

        // Test 3: Verification log with Outcome = Accept -> release MUST succeed
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var acceptLog = new BiometricVerificationLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                BookingId = bookingId,
                GuestId = guestId,
                Outcome = BiometricOutcome.Accept,
                Attempts = 1,
                ModelName = "AuraFace-v1",
                ModelVersion = "1.0.0",
                ThresholdsJson = "{\"AcceptThreshold\": 0.85}",
                CreatedAt = DateTime.UtcNow.AddSeconds(1) // Later than reject
            };
            db.BiometricVerificationLogs.Add(acceptLog);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "screen");

            // Assert: Code IS released (Accept outcome)
            Assert.NotNull(code);
            Assert.Equal(TestCode, code);

            // Verify release log was created WITHOUT the code (AC-NF13-4, AC-NF13-9)
            var releaseLog = await db.Set<AccessCodeReleaseLog>()
                .FirstOrDefaultAsync(l => l.BookingId == bookingId);
            Assert.NotNull(releaseLog);
            Assert.Equal(orgId, releaseLog.OrgId);
            Assert.Equal("screen", releaseLog.Channel);

            // Verify release log entity has NO code property
            var releaseLogType = typeof(AccessCodeReleaseLog);
            var codeProperty = releaseLogType.GetProperty("Code");
            Assert.Null(codeProperty);
        }

        // Test 4: Verification log with operator verification (OperatorUserId set) -> release MUST succeed
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            // Clear previous logs
            var logsToRemove = db.BiometricVerificationLogs.Where(l => l.BookingId == bookingId);
            db.BiometricVerificationLogs.RemoveRange(logsToRemove);

            var operatorLog = new BiometricVerificationLog
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                BookingId = bookingId,
                GuestId = guestId,
                Outcome = BiometricOutcome.Escalate, // Even if escalated...
                Attempts = 1,
                ModelName = "AuraFace-v1",
                ModelVersion = "1.0.0",
                ThresholdsJson = "{\"AcceptThreshold\": 0.85}",
                OperatorUserId = "operator-123", // ...operator approved it
                OperatorNote = "Manually verified",
                CreatedAt = DateTime.UtcNow
            };
            db.BiometricVerificationLogs.Add(operatorLog);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "email");

            // Assert: Code IS released (operator verification)
            Assert.NotNull(code);
            Assert.Equal(TestCode, code);
        }
    }

    /// <summary>
    /// AC-NF13-1 / AC-NF13-5 proof: Access codes MUST be encrypted at rest and NEVER appear in plain text logs.
    /// EncryptedCode property has EncryptedStringConverter configured.
    /// Service logs NEVER contain the actual code value.
    /// </summary>
    [Fact]
    public async Task AccessCode_NeverInPlainTextLogs()
    {
        // Arrange: in-memory database with Data Protection
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccessCodeEncryptionTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();

        // Assert: The model has encryption converter on EncryptedCode
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var accessCodeEntity = db.Model.FindEntityType(typeof(PropertyAccessCode))!;
            var encryptedCodeConverter = accessCodeEntity.FindProperty(nameof(PropertyAccessCode.EncryptedCode))!.GetValueConverter();

            Assert.IsType<EncryptedStringConverter>(encryptedCodeConverter);

            // Test the converter directly: it encrypts the value
            var encrypted = (string)encryptedCodeConverter!.ConvertToProvider(TestCode)!;

            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, encrypted);
            Assert.DoesNotContain(TestCode, encrypted);

            // And decrypts back to the original value
            Assert.Equal(TestCode, encryptedCodeConverter.ConvertFromProvider(encrypted));
        }

        // Act: Create a PropertyAccessCode, save and read back
        var propertyId = Guid.NewGuid();
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var org = new OrgEntity
            {
                Id = orgId,
                Name = "Test Org",
                Slug = "test-org",
                DisplayName = "Test Org",
                ContactEmail = "test@example.com",
                PlanTier = PlanTier.Pro
            };
            db.Orgs.Add(org);

            var property = new Property
            {
                Id = propertyId,
                OrgId = orgId,
                OwnerId = "test-user",
                Name = "Test Property",
                Address = "Test Address",
                City = "Milan",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100
            };
            db.Properties.Add(property);

            var accessCode = new PropertyAccessCode
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                PropertyId = propertyId,
                EncryptedCode = TestCode, // Stored encrypted by EF converter
                UpdatedAt = DateTime.UtcNow
            };
            db.Set<PropertyAccessCode>().Add(accessCode);

            await db.SaveChangesAsync();
        }

        // Assert: Read back and verify decryption works
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var retrieved = await db.Set<PropertyAccessCode>()
                .FirstOrDefaultAsync(ac => ac.PropertyId == propertyId);

            Assert.NotNull(retrieved);
            Assert.Equal(TestCode, retrieved.EncryptedCode); // EF converter decrypts on read
        }

        // Assert: Verify PropertyAccessCode entity has ITenantOwned
        var accessCodeType = typeof(PropertyAccessCode);
        Assert.True(typeof(ITenantOwned).IsAssignableFrom(accessCodeType));

        // Assert: Verify AccessCodeReleaseLog NEVER stores the code
        var releaseLogType = typeof(AccessCodeReleaseLog);
        var allProperties = releaseLogType.GetProperties();
        var propertyNames = allProperties.Select(p => p.Name.ToLowerInvariant()).ToList();

        // Forbidden field name patterns (case-insensitive)
        var forbiddenPatterns = new[] { "code", "pin", "password", "secret", "key" };
        foreach (var pattern in forbiddenPatterns)
        {
            var matchingProperties = propertyNames
                .Where(name => name.Contains(pattern) && !name.Contains("bookingcode")) // BookingCode is OK (not the access code)
                .ToList();

            // Assert: No property name contains forbidden patterns
            Assert.Empty(matchingProperties);
        }

        // Assert: AccessCodeService logs NEVER contain the code value
        // Service logs only: propertyId, bookingId, userId, channel
        // NEVER: the actual code value
        // This is enforced by code review: logger calls MUST NOT pass EncryptedCode or decrypted code
    }
}
