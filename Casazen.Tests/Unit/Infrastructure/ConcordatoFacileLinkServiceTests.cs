using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
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
    private const string VectorSecret = "test_link_secret_0123456789abcdef0123456789abcdef";
    private const string VectorLeaseId = "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11";
    private const string VectorSignature = "fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea";

    /// <summary>
    /// CRITICAL PROOF TEST: Test vector conformance per ADR-007-R2.
    /// This test MUST verify the exact HMAC-SHA256 signature matches the spec.
    /// If the algorithm or encoding is wrong, this test WILL fail and all generated links will be rejected by the partner.
    /// </summary>
    [Fact]
    public void GenerateSignature_SpecTestVectors_ExactMatch()
    {
        // Arrange: Test vectors from ADR-007-R2 (ConcordatoFacile spec v1.3 section 2)
        var leaseId = Guid.Parse(VectorLeaseId);
        var expiresAt = "2026-10-13T10:00:00Z";
        var secret = VectorSecret;
        var expectedSignature = VectorSignature;

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
    /// CRITICAL PROOF TEST on the real service (CF-BE, closes CF-DOC-FU9): the data of the public vector of the specification (v1.3 §2:
    /// the lease, the secret, an expiry of 2026-10-13T10:00:00Z, which is the clock at 09:45:00Z plus the 15 minutes of the link) go through
    /// <see cref="ConcordatoFacileLinkService"/> and come out as the exact address, with the exact signature of the vector. The test above
    /// recomputes the signature in the test; this one cannot pass if the service signs another message, with another key, in another
    /// format, or builds another address. The comune is one with a space and an apostrophe to prove the encoding of the query too: the
    /// signature covers the decoded values, the address carries them encoded (<c>HttpUtility.UrlEncode</c>: lower-case hexadecimal, a space
    /// is a plus).
    /// </summary>
    [Fact]
    public async Task GenerateLink_PublicVectorOfTheSpecification_ProducesTheExactAddressAndSignature()
    {
        var orgId = Guid.NewGuid();
        var leaseId = Guid.Parse(VectorLeaseId);
        await using var db = NewDb();
        await SeedLeaseAsync(db, orgId, leaseId, comune: SeedComune("035033", "Reggio nell'Emilia", "RE"));

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 45, 0, TimeSpan.Zero));
        var service = new ConcordatoFacileLinkService(
            db,
            AccessGranted(leaseId),
            PrivacyAccepted(),
            Options.Create(new ConcordatoFacileOptions { LinkSecret = VectorSecret }),
            NullLogger<ConcordatoFacileLinkService>.Instance,
            clock);

        var link = await service.GenerateSignedLinkAsync(leaseId, new ClaimsPrincipal());

        Assert.Equal(
            "https://app.concordatofacile.it/?partner=casazen"
            + $"&leaseId={VectorLeaseId}"
            + "&expiresAt=2026-10-13T10%3a00%3a00Z"
            + $"&signature={VectorSignature}"
            + "&comune=Reggio+nell%27Emilia"
            + "&provincia=RE",
            link.Url);

        // The audit line has the lease and the time, never the address or the signature.
        var recorded = await db.LeaseEvents.SingleAsync(e => e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated);
        Assert.Equal(leaseId, recorded.LeaseContractId);
        Assert.Equal(new DateTime(2026, 10, 13, 9, 45, 0, DateTimeKind.Utc), recorded.OccurredAt);
        Assert.Null(recorded.Payload);
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

        await using var db = NewDb();
        await SeedLeaseAsync(db, orgId, leaseId, comune: SeedComune("108044", "Seveso", "MB"));

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
            LinkSecret = VectorSecret // >= 32 chars
        });
        var logger = NullLogger<ConcordatoFacileLinkService>.Instance;
        var service = new ConcordatoFacileLinkService(db, accessServiceMock.Object, PrivacyAccepted(), options, logger);

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

        await using var db = NewDb();

        // A property WITHOUT ComuneIstatCode (no municipality selected)
        await SeedLeaseAsync(db, orgId, leaseId, comune: null);

        var options = Options.Create(new ConcordatoFacileOptions
        {
            LinkSecret = VectorSecret
        });
        var logger = NullLogger<ConcordatoFacileLinkService>.Instance;
        var service = new ConcordatoFacileLinkService(db, AccessGranted(leaseId), PrivacyAccepted(), options, logger);

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

    private static IConcordatoFacileAccessService AccessGranted(Guid leaseId)
    {
        var accessServiceMock = new Mock<IConcordatoFacileAccessService>();
        accessServiceMock.Setup(s => s.CheckAccessAsync(leaseId, It.IsAny<ClaimsPrincipal>(), default))
            .ReturnsAsync(new CfAccessResult(
                HasAccess: true,
                Source: CfAccessSource.Payment,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: DateTime.UtcNow,
                GiftAssignedAt: null));
        return accessServiceMock.Object;
    }

    /// <summary>The privacy gate is proven in <see cref="ConcordatoFacilePrivacyTests"/>: here the notice is accepted.</summary>
    private static IConcordatoFacilePrivacyService PrivacyAccepted() => Mock.Of<IConcordatoFacilePrivacyService>();

    private static Comune SeedComune(string istatCode, string name, string provinceCode) => new()
    {
        IstatCode = istatCode,
        Name = name,
        DisplayName = name,
        NormalizedName = name.ToLowerInvariant(),
        SearchText = name.ToLowerInvariant(),
        ProvinceCode = provinceCode,
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

    private static async Task SeedLeaseAsync(AppDbContext db, Guid orgId, Guid leaseId, Comune? comune)
    {
        var propertyId = Guid.NewGuid();
        if (comune is not null)
            db.Comuni.Add(comune);

        db.Properties.Add(new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-user",
            Name = "Test Property",
            Address = "Via Test 1",
            City = comune?.Name ?? "Unknown City",
            PostalCode = "20822",
            ComuneIstatCode = comune?.IstatCode,
            RegionCode = comune is null ? null : "LOM"
        });
        db.LeaseContracts.Add(new LeaseContract
        {
            Id = leaseId,
            OrgId = orgId,
            PropertyId = propertyId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
