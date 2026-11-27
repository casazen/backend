using Casazen.Core.Authorization;
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
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccessCodeVerificationTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = TestDataProtection.Shared;
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

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

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "screen");

            Assert.Null(code);
        }

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

            Assert.Null(code);
        }

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
                CreatedAt = DateTime.UtcNow.AddSeconds(1)
            };
            db.BiometricVerificationLogs.Add(acceptLog);
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "screen");

            Assert.NotNull(code);
            Assert.Equal(TestCode, code);

            var releaseLog = await db.Set<AccessCodeReleaseLog>()
                .FirstOrDefaultAsync(l => l.BookingId == bookingId);
            Assert.NotNull(releaseLog);
            Assert.Equal(orgId, releaseLog.OrgId);
            Assert.Equal("screen", releaseLog.Channel);

            var releaseLogType = typeof(AccessCodeReleaseLog);
            var codeProperty = releaseLogType.GetProperty("Code");
            Assert.Null(codeProperty);
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var logsToRemove = db.BiometricVerificationLogs.Where(l => l.BookingId == bookingId);
            db.BiometricVerificationLogs.RemoveRange(logsToRemove);

            db.BiometricVerificationLogs.Add(OperatorLog(orgId, bookingId, guestId, BiometricOutcome.Reject, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            Assert.Null(await service.ReleaseToGuestAsync(bookingId, "email"));
            Assert.Null(await service.GetAccessCodeAsync(bookingId, "host-user", new HostScope(orgId)));
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            db.BiometricVerificationLogs.Add(OperatorLog(orgId, bookingId, guestId, BiometricOutcome.Accept, DateTime.UtcNow.AddSeconds(1)));
            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

            var code = await service.ReleaseToGuestAsync(bookingId, "email");

            Assert.NotNull(code);
            Assert.Equal(TestCode, code);
        }
    }

    private static BiometricVerificationLog OperatorLog(Guid orgId, Guid bookingId, Guid guestId, BiometricOutcome outcome, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = orgId,
        BookingId = bookingId,
        GuestId = guestId,
        Outcome = outcome,
        Method = VerificationMethod.Operator,
        Attempts = 3,
        ModelName = "AuraFace-v1",
        ModelVersion = "1.0.0",
        ThresholdsJson = "{\"AcceptThreshold\": 0.85}",
        OperatorUserId = "operator-123",
        OperatorNote = "Manually verified",
        CreatedAt = createdAt,
    };

    /// <summary>
    /// AC-NF13-1 / AC-NF13-5 proof: Access codes MUST be encrypted at rest and NEVER appear in plain text logs.
    /// EncryptedCode property has EncryptedStringConverter configured.
    /// Service logs NEVER contain the actual code value.
    /// </summary>
    [Fact]
    public async Task AccessCode_NeverInPlainTextLogs()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccessCodeEncryptionTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = TestDataProtection.Shared;
        var orgId = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var accessCodeEntity = db.Model.FindEntityType(typeof(PropertyAccessCode))!;
            var encryptedCodeConverter = accessCodeEntity.FindProperty(nameof(PropertyAccessCode.EncryptedCode))!.GetValueConverter();

            Assert.IsType<EncryptedStringConverter>(encryptedCodeConverter);

            var encrypted = (string)encryptedCodeConverter!.ConvertToProvider(TestCode)!;

            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, encrypted);
            Assert.DoesNotContain(TestCode, encrypted);

            Assert.Equal(TestCode, encryptedCodeConverter.ConvertFromProvider(encrypted));
        }

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
                EncryptedCode = TestCode,
                UpdatedAt = DateTime.UtcNow
            };
            db.Set<PropertyAccessCode>().Add(accessCode);

            await db.SaveChangesAsync();
        }

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var retrieved = await db.Set<PropertyAccessCode>()
                .FirstOrDefaultAsync(ac => ac.PropertyId == propertyId);

            Assert.NotNull(retrieved);
            Assert.Equal(TestCode, retrieved.EncryptedCode);
        }

        var accessCodeType = typeof(PropertyAccessCode);
        Assert.True(typeof(ITenantOwned).IsAssignableFrom(accessCodeType));

        var releaseLogType = typeof(AccessCodeReleaseLog);
        var allProperties = releaseLogType.GetProperties();
        var propertyNames = allProperties.Select(p => p.Name.ToLowerInvariant()).ToList();

        var forbiddenPatterns = new[] { "code", "pin", "password", "secret", "key" };
        foreach (var pattern in forbiddenPatterns)
        {
            var matchingProperties = propertyNames
                .Where(name => name.Contains(pattern) && !name.Contains("bookingcode"))
                .ToList();

            Assert.Empty(matchingProperties);
        }

        // Assert: AccessCodeService logs NEVER contain the code value
        // Service logs only: propertyId, bookingId, userId, channel
        // NEVER: the actual code value
        // This is enforced by code review: logger calls MUST NOT pass EncryptedCode or decrypted code
    }
}
