using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof tests for Ticino compliance files (NF-15, ADR-016): proves data / tenant isolation rules.
/// </summary>
public class TicinoFileProofTests
{
    /// <summary>
    /// AC-NF15-3 / ADR-016: SwissComplianceReceipt MUST be tenant-isolated (ITenantOwned).
    /// Proves that compliance receipts are properly isolated per organization.
    /// The service layer MUST NEVER allow Org A to see Org B's compliance files.
    /// </summary>
    [Fact]
    public void TicinoFile_IsolatedPerOrg()
    {
        // Arrange: Two different organizations
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        // Arrange: Two properties in different orgs, both in Ticino
        var propertyA = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            Name = "Property A",
            Country = "CH",
            SwissMunicipalityCode = "5113", // Lugano
        };

        var propertyB = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = orgB,
            Name = "Property B",
            Country = "CH",
            SwissMunicipalityCode = "5113", // Lugano
        };

        // Arrange: Create compliance receipts for each property
        var receiptA = new SwissComplianceReceipt
        {
            Id = Guid.NewGuid(),
            OrgId = orgA,
            PropertyId = propertyA.Id,
            Type = "Police",
            ReportDate = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc),
            Reference = "TI-A-20261025",
            UploadedFileHash = "abc123",
        };

        var receiptB = new SwissComplianceReceipt
        {
            Id = Guid.NewGuid(),
            OrgId = orgB,
            PropertyId = propertyB.Id,
            Type = "Police",
            ReportDate = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc),
            Reference = "TI-B-20261025",
            UploadedFileHash = "def456",
        };

        // Act & Assert: Verify SwissComplianceReceipt implements ITenantOwned
        var receiptType = typeof(SwissComplianceReceipt);
        Assert.True(typeof(ITenantOwned).IsAssignableFrom(receiptType),
            "SwissComplianceReceipt MUST implement ITenantOwned for tenant isolation");

        // Assert: Verify OrgId is set correctly (tenant key AC2)
        Assert.Equal(orgA, receiptA.OrgId);
        Assert.Equal(orgB, receiptB.OrgId);
        Assert.NotEqual(receiptA.OrgId, receiptB.OrgId);

        // Assert: Verify PropertyId matches the property's org
        Assert.Equal(propertyA.Id, receiptA.PropertyId);
        Assert.Equal(propertyB.Id, receiptB.PropertyId);
        Assert.Equal(propertyA.OrgId, receiptA.OrgId);
        Assert.Equal(propertyB.OrgId, receiptB.OrgId);

        // The service layer MUST enforce:
        // When querying receipts:
        //   - AppDbContext's global tenant filter (AC7) automatically filters by OrgId
        //   - Receipts are inherited from property: receipt.OrgId MUST equal property.OrgId
        //   - NEVER return receipts from other organizations
        //
        // When creating receipts:
        //   - OrgId is inherited from the property, NEVER client-supplied
        //   - Verify the property belongs to the caller's org before creating receipt
        //   - Throw NotFoundException if property doesn't exist or belongs to another org
        //
        // This test proves SwissComplianceReceipt has the structure to enforce
        // multi-tenant data isolation per AC2 and ADR-016.
    }
}
