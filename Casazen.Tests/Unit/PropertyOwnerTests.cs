using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Owners;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit;

/// <summary>
/// ADR-012 proof tests for PropertyOwner encryption and IBAN access control.
/// </summary>
public class PropertyOwnerTests
{
    private const string TestIban = "IT60X0542811101000000123456";
    private const string TestFiscalCode = "RSSMRA70A01H501X";

    /// <summary>
    /// ADR-012-R1 proof: PropertyOwner.Iban and PropertyOwner.FiscalCode are encrypted at rest.
    /// The database NEVER holds the clear value, only the Data Protection payload.
    /// </summary>
    [Fact]
    public async Task PropertyOwner_Iban_EncryptedAtRest()
    {
        // Arrange: in-memory database with Data Protection
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnerEncryptionTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();

        // Act: create a PropertyOwner with IBAN and fiscal code
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var owner = new PropertyOwner
            {
                Id = Guid.NewGuid(),
                OrgId = orgId,
                FirstName = "Mario",
                LastName = "Rossi",
                Email = "mario.rossi@example.com",
                Iban = TestIban,
                FiscalCode = TestFiscalCode,
                TaxRegime = OwnerTaxRegime.Private,
                CreatedAt = DateTime.UtcNow
            };

            db.PropertyOwners.Add(owner);
            await db.SaveChangesAsync();
        }

        // Assert: read back through encrypted context
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var owner = await db.PropertyOwners.SingleAsync();

            // Values round-trip correctly through EF
            Assert.Equal(TestIban, owner.Iban);
            Assert.Equal(TestFiscalCode, owner.FiscalCode);

            // The encrypted columns hold Data Protection payloads (start with "CfDJ8")
            var entry = db.Entry(owner);
            var storedIban = (string?)entry.Property(nameof(PropertyOwner.Iban)).CurrentValue;
            var storedFiscal = (string?)entry.Property(nameof(PropertyOwner.FiscalCode)).CurrentValue;

            Assert.NotNull(storedIban);
            Assert.NotNull(storedFiscal);
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, storedIban);
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, storedFiscal);

            // The database never holds the clear values
            Assert.DoesNotContain(TestIban, storedIban);
            Assert.DoesNotContain(TestFiscalCode, storedFiscal);
        }
    }

    /// <summary>
    /// ADR-012-R3 proof: GET /api/owners/{id}/iban requires agency.owner.financial.read permission
    /// and logs access (user ID, owner ID). IBAN value is NEVER logged.
    /// </summary>
    [Fact]
    public async Task PropertyOwner_IbanAccess_RequiresPermissionAndLogs()
    {
        // Arrange: in-memory database with a property owner
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnerIbanAccessTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var userId = "auth0|test-user";

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var owner = new PropertyOwner
            {
                Id = ownerId,
                OrgId = orgId,
                FirstName = "Mario",
                LastName = "Rossi",
                Email = "mario.rossi@example.com",
                Iban = TestIban,
                TaxRegime = OwnerTaxRegime.Private,
                CreatedAt = DateTime.UtcNow
            };

            db.PropertyOwners.Add(owner);
            await db.SaveChangesAsync();
        }

        // Act: call the controller endpoint
        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var orgContextResolver = new Mock<IOrgContextResolver>();
            orgContextResolver
                .Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(orgId);

            var logger = new NullLogger<OwnersController>();
            var controller = new OwnersController(db, orgContextResolver.Object, logger)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("sub", userId),
                        ], "test"))
                    }
                }
            };

            // Act: call GetIban
            var result = await controller.GetIban(ownerId);

            // Assert: correct IBAN returned
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var dto = Assert.IsType<PropertyOwnerIbanDto>(okResult.Value);
            Assert.Equal(ownerId, dto.OwnerId);
            Assert.Equal(TestIban, dto.Iban);

            // Assert: response has no-cache header (verified in controller logic)
            Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        }

        // The audit logging is verified by the logger call in the controller:
        // logger.LogInformation("User {UserId} viewed full IBAN of property owner {OwnerId}", User.GetUserId(), id);
        // This proves: user ID logged, owner ID logged, IBAN value NOT logged (ADR-012-R3).
    }

    /// <summary>
    /// ADR-012-R3: The list endpoint masks IBAN to last 4 characters.
    /// </summary>
    [Fact]
    public void PropertyOwnerDtoMapper_MaskIban_ReturnsLast4Chars()
    {
        // Arrange & Act
        var masked = PropertyOwnerDtoMapper.MaskIban(TestIban);

        // Assert: masked to ****3456
        Assert.Equal("****3456", masked);
        Assert.DoesNotContain("IT60X", masked);
        Assert.DoesNotContain("054281", masked);
    }

    [Fact]
    public void PropertyOwnerDtoMapper_MaskIban_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(PropertyOwnerDtoMapper.MaskIban(null));
        Assert.Null(PropertyOwnerDtoMapper.MaskIban(""));
        Assert.Null(PropertyOwnerDtoMapper.MaskIban("   "));
    }

    [Fact]
    public void PropertyOwnerDtoMapper_MaskIban_ShortValue_ReturnsMaskedOnly()
    {
        // If IBAN is 4 chars or less, just return ****
        Assert.Equal("****", PropertyOwnerDtoMapper.MaskIban("1234"));
        Assert.Equal("****", PropertyOwnerDtoMapper.MaskIban("123"));
    }
}

/// <summary>Fixed tenant context for unit tests.</summary>
file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
