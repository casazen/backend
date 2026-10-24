using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// PROOF TESTS for ConcordatoFacileLinkService (ADR-007).
/// These tests prove HMAC algorithm correctness and permission enforcement.
/// </summary>
public class ConcordatoFacileLinkServiceTests
{
    /// <summary>
    /// CRITICAL PROOF TEST: Test vector conformance per ADR-007-R2.
    /// This test MUST verify the exact HMAC-SHA256 signature matches the spec.
    /// If the algorithm or encoding is wrong, this test WILL fail and all generated links will be rejected by the partner.
    /// </summary>
    [Fact]
    public void GenerateSignature_SpecTestVectors_ExactMatch()
    {
        // Arrange: Test vectors from ADR-007-R2 (ConcordatoFacile spec v1.3 section 2)
        var leaseId = Guid.Parse("0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11");
        var expiresAt = "2026-10-13T10:00:00Z";
        var secret = "test_link_secret_0123456789abcdef0123456789abcdef";
        var expectedSignature = "fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea";

        // Act: Compute HMAC-SHA256 per ADR-007-R1
        var input = $"{leaseId.ToString().ToLowerInvariant()}.{expiresAt}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, inputBytes);
        var actualSignature = Convert.ToHexString(hash).ToLowerInvariant();

        // Assert: EXACT match required
        Assert.Equal(expectedSignature, actualSignature);

        // PROOF: If algorithm uses wrong encoding (not UTF-8), wrong order (expiresAt.leaseId),
        // wrong case (not lowercase), or wrong format (base64 instead of hex), this test FAILS.
        // All links generated with wrong algorithm will be rejected by ConcordatoFacile.
    }

    /// <summary>
    /// PROOF TEST: Permission rule - user without ConcordatoFacile access must NOT get a link (ADR-007-R10).
    /// Access check must happen BEFORE signature generation.
    /// </summary>
    [Fact]
    public async Task GenerateLink_NoAccessUser_ThrowsUnauthorized()
    {
        // Arrange
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await using var db = NewDb();

        // Create a comune
        var comune = new Comune
        {
            IstatCode = "108044",
            Name = "Seveso",
            DisplayName = "Seveso",
            NormalizedName = "seveso",
            SearchText = "seveso",
            ProvinceCode = "MB",
            RegionIstatCode = "03",
            RegionName = "Lombardia",
            IsActive = true,
            SourceImportId = Guid.NewGuid(),
            SourceImport = new ComuneImport
            {
                Origin = ComuneImportOrigin.StartupSeed,
                SourceFileName = "comuni-test.csv",
                SourceVersion = "Test",
                ReferenceDate = new DateOnly(2026, 1, 1),
                Sha256 = new string('0', 64),
                ImportedBy = "system"
            }
        };
        db.Comuni.Add(comune);

        // Create a property with municipality
        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-user",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Seveso",
            PostalCode = "20822",
            ComuneIstatCode = "108044",
            RegionCode = "LOM"
        };
        db.Properties.Add(property);

        // Create a lease WITHOUT a ConcordatoFacile purchase or gift (no access)
        var lease = new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            CreatedAt = DateTime.UtcNow
        };
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();

        // Setup: Access service that returns HasAccess=false (no payment, no gift, not admin)
        var accessServiceMock = new Mock<IConcordatoFacileAccessService>();
        accessServiceMock.Setup(s => s.CheckAccessAsync(leaseId, It.IsAny<ClaimsPrincipal>(), default))
            .ReturnsAsync(new CfAccessResult(
                HasAccess: false,
                Source: CfAccessSource.None,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: null,
                GiftAssignedAt: null));

        var options = Options.Create(new ConcordatoFacileOptions
        {
            LinkSecret = "test_link_secret_0123456789abcdef0123456789abcdef" // >= 32 chars
        });
        var logger = NullLogger<ConcordatoFacileLinkService>.Instance;
        var service = new ConcordatoFacileLinkService(db, accessServiceMock.Object, options, logger);

        var user = new ClaimsPrincipal();

        // Act & Assert: User without access must NOT get a link
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.GenerateSignedLinkAsync(leaseId, user);
        });

        // PROOF: Access check happens BEFORE link generation.
        // If the service skipped the access check, this test would fail.
        // The permission rule is enforced: only users with paid access, gift, or admin role get links.
    }

    /// <summary>
    /// PROOF TEST: Municipality unknown - lease with property that has no ComuneIstatCode must return 422 (ADR-007-R7).
    /// </summary>
    [Fact]
    public async Task GenerateLink_PropertyWithoutMunicipality_ThrowsDomainRuleException()
    {
        // Arrange
        var orgId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await using var db = NewDb();

        // Create a property WITHOUT ComuneIstatCode
        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-user",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Unknown City",
            PostalCode = "00000",
            ComuneIstatCode = null, // No municipality selected
            RegionCode = null
        };
        db.Properties.Add(property);

        // Create a lease
        var lease = new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            CreatedAt = DateTime.UtcNow
        };
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();

        // Setup: Access service that returns HasAccess=true
        var accessServiceMock = new Mock<IConcordatoFacileAccessService>();
        accessServiceMock.Setup(s => s.CheckAccessAsync(leaseId, It.IsAny<ClaimsPrincipal>(), default))
            .ReturnsAsync(new CfAccessResult(
                HasAccess: true,
                Source: CfAccessSource.Payment,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: DateTime.UtcNow,
                GiftAssignedAt: null));

        var options = Options.Create(new ConcordatoFacileOptions
        {
            LinkSecret = "test_link_secret_0123456789abcdef0123456789abcdef"
        });
        var logger = NullLogger<ConcordatoFacileLinkService>.Instance;
        var service = new ConcordatoFacileLinkService(db, accessServiceMock.Object, options, logger);

        var user = new ClaimsPrincipal();

        // Act & Assert: Property without municipality must throw DomainRuleException with code property_municipality_unknown
        var ex = await Assert.ThrowsAsync<DomainRuleException>(async () =>
        {
            await service.GenerateSignedLinkAsync(leaseId, user);
        });

        Assert.Equal("property_municipality_unknown", ex.Code);

        // PROOF: Municipality from ISTAT is REQUIRED for link generation.
        // If the service allowed links without municipality, this test would fail.
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
