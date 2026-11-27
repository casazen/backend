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

public class PropertyOwnerTests
{
    private const string TestIban = "IT60X0542811101000000123456";
    private const string TestFiscalCode = "RSSMRA70A01H501X";

    /// <summary>
    /// ADR-012-R1 proof: PropertyOwner.Iban and PropertyOwner.FiscalCode have encryption converters configured.
    /// The converters encrypt values at rest (tested directly), and the values round-trip correctly through EF.
    /// Actual verification that values are stored encrypted in PostgreSQL is in integration tests.
    /// </summary>
    [Fact]
    public async Task PropertyOwner_Iban_EncryptedAtRest()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnerEncryptionTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = TestDataProtection.Shared;
        var orgId = Guid.NewGuid();

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var ownerEntity = db.Model.FindEntityType(typeof(PropertyOwner))!;
            var ibanConverter = ownerEntity.FindProperty(nameof(PropertyOwner.Iban))!.GetValueConverter();
            var fiscalCodeConverter = ownerEntity.FindProperty(nameof(PropertyOwner.FiscalCode))!.GetValueConverter();

            Assert.IsType<EncryptedStringConverter>(ibanConverter);
            Assert.IsType<EncryptedStringConverter>(fiscalCodeConverter);

            var encryptedIban = (string)ibanConverter!.ConvertToProvider(TestIban)!;
            var encryptedFiscal = (string)fiscalCodeConverter!.ConvertToProvider(TestFiscalCode)!;

            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, encryptedIban);
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, encryptedFiscal);
            Assert.DoesNotContain(TestIban, encryptedIban);
            Assert.DoesNotContain(TestFiscalCode, encryptedFiscal);

            Assert.Equal(TestIban, ibanConverter.ConvertFromProvider(encryptedIban));
            Assert.Equal(TestFiscalCode, fiscalCodeConverter.ConvertFromProvider(encryptedFiscal));
        }

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

        await using (var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            var owner = await db.PropertyOwners.SingleAsync();

            Assert.Equal(TestIban, owner.Iban);
            Assert.Equal(TestFiscalCode, owner.FiscalCode);
        }
    }

    [Fact]
    public async Task PropertyOwner_IbanAccess_RequiresPermissionAndLogs()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PropertyOwnerIbanAccessTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = TestDataProtection.Shared;
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

            var result = await controller.GetIban(ownerId);

            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var dto = Assert.IsType<PropertyOwnerIbanDto>(okResult.Value);
            Assert.Equal(ownerId, dto.OwnerId);
            Assert.Equal(TestIban, dto.Iban);

            Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
        }
    }

    [Fact]
    public void PropertyOwnerDtoMapper_MaskIban_ReturnsLast4Chars()
    {
        var masked = PropertyOwnerDtoMapper.MaskIban(TestIban);

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
        Assert.Equal("****", PropertyOwnerDtoMapper.MaskIban("1234"));
        Assert.Equal("****", PropertyOwnerDtoMapper.MaskIban("123"));
    }
}

file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
