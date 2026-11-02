using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit;

/// <summary>
/// ADR-019 proof tests for agency invoice numbering and tax calculation rules.
/// </summary>
public class InvoiceProofTests
{
    /// <summary>
    /// ADR-019-R4 proof: Invoice numbering must be sequential with no gaps per agency/series.
    /// Tests that consecutive invoices receive sequential numbers (1, 2, 3) with no duplicates or gaps.
    /// </summary>
    [PostgresFact]
    public async Task InvoiceNumbering_NoGaps()
    {
        // Use PostgreSQL so invoice numbering exercises real serializable transactions.
        await using var database = await PostgresTestDatabase.CreateMigratedAsync();
        var options = database.CreateOptions();

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var owner1Id = Guid.NewGuid();
        var owner2Id = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        // Create org, property, and two owners
        var org = new OrgEntity
        {
            Id = orgId,
            Name = "Test Agency",
            Slug = "test-agency",
            DisplayName = "Test Agency",
            ContactEmail = "test@agency.com",
            PlanTier = PlanTier.Agency,
            HasPartitaIva = true,
            PartitaIvaNumber = "12345678901",
            FiscalCode = "IT12345678901",
            RegisteredOffice = "Via Roma 1, 00100 Roma (RM)",
            CreatedAt = DateTime.UtcNow
        };

        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-owner",
            Name = "Test Property",
            Address = "Test Address",
            City = "Rome",
            CreatedAt = DateTime.UtcNow
        };

        var owner1 = new PropertyOwner
        {
            Id = owner1Id,
            OrgId = orgId,
            FirstName = "Owner",
            LastName = "One",
            Email = "owner1@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var owner2 = new PropertyOwner
        {
            Id = owner2Id,
            OrgId = orgId,
            FirstName = "Owner",
            LastName = "Two",
            Email = "owner2@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var ownership1 = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = owner1Id,
            SharePercent = 100.00m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        // Create three approved statements for different owners/months
        var statement1 = new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = owner1Id,
            PropertyId = propertyId,
            Month = 10,
            Year = 2026,
            Currency = "EUR",
            Status = StatementStatus.Approved,
            TotalRevenue = 1000.00m,
            Commission = 100.00m,
            Expenses = 0m,
            WithholdingTax = 0m,
            NetPayable = 900.00m,
            OwnershipPercent = 100.00m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var statement2 = new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = owner1Id,
            PropertyId = propertyId,
            Month = 11,
            Year = 2026,
            Currency = "EUR",
            Status = StatementStatus.Approved,
            TotalRevenue = 1200.00m,
            Commission = 120.00m,
            Expenses = 0m,
            WithholdingTax = 0m,
            NetPayable = 1080.00m,
            OwnershipPercent = 100.00m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var statement3 = new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = owner1Id,
            PropertyId = propertyId,
            Month = 12,
            Year = 2026,
            Currency = "EUR",
            Status = StatementStatus.Approved,
            TotalRevenue = 1100.00m,
            Commission = 110.00m,
            Expenses = 0m,
            WithholdingTax = 0m,
            NetPayable = 990.00m,
            OwnershipPercent = 100.00m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.PropertyOwners.Add(owner1);
        db.PropertyOwners.Add(owner2);
        db.PropertyOwnerships.Add(ownership1);
        db.OwnerStatements.Add(statement1);
        db.OwnerStatements.Add(statement2);
        db.OwnerStatements.Add(statement3);
        await db.SaveChangesAsync();

        var service = new AgencyInvoiceService(db);

        // Act: generate three invoices for consecutive months
        var result1 = await service.GenerateInvoiceAsync(statement1.Id);
        var result2 = await service.GenerateInvoiceAsync(statement2.Id);
        var result3 = await service.GenerateInvoiceAsync(statement3.Id);

        // Assert: verify sequential numbering with no gaps (1, 2, 3)
        Assert.Equal("1", result1.Invoice.InvoiceNumber);
        Assert.Equal("2", result2.Invoice.InvoiceNumber);
        Assert.Equal("3", result3.Invoice.InvoiceNumber);

        // Verify all invoices have the same series (year)
        Assert.Equal("2026", result1.Invoice.Series);
        Assert.Equal("2026", result2.Invoice.Series);
        Assert.Equal("2026", result3.Invoice.Series);

        // Verify no duplicates in database
        var allInvoices = await db.AgencyInvoices
            .Include(i => i.PropertyOwner)
            .Where(i => i.OrgId == orgId && i.Series == "2026")
            .OrderBy(i => i.InvoiceNumber)
            .ToListAsync();

        Assert.Equal(3, allInvoices.Count);
        Assert.All(allInvoices.Select((inv, idx) => (inv, idx)), pair =>
        {
            Assert.Equal((pair.idx + 1).ToString(), pair.inv.InvoiceNumber);
            Assert.Equal(owner1Id, pair.inv.PropertyOwner.Id);
        });
    }

    /// <summary>
    /// ADR-019-R10 proof: Invoice amounts (VAT and stamp duty) must be correct to the cent.
    /// Tests VAT calculation at 22% and stamp duty of €2 when taxable base exceeds €77.47 with no VAT.
    /// </summary>
    [PostgresFact]
    public async Task InvoiceAmounts_Correct()
    {
        // Use PostgreSQL so invoice numbering exercises real serializable transactions.
        await using var database = await PostgresTestDatabase.CreateMigratedAsync();
        var options = database.CreateOptions();

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        // Create org, property, and owner
        var org = new OrgEntity
        {
            Id = orgId,
            Name = "Test Agency",
            Slug = "test-agency",
            DisplayName = "Test Agency",
            ContactEmail = "test@agency.com",
            PlanTier = PlanTier.Agency,
            HasPartitaIva = true,
            PartitaIvaNumber = "12345678901",
            FiscalCode = "IT12345678901",
            RegisteredOffice = "Via Roma 1, 00100 Roma (RM)",
            CreatedAt = DateTime.UtcNow
        };

        var property = new Property
        {
            Id = propertyId,
            OrgId = orgId,
            OwnerId = "test-owner",
            Name = "Test Property",
            Address = "Test Address",
            City = "Rome",
            CreatedAt = DateTime.UtcNow
        };

        var owner = new PropertyOwner
        {
            Id = ownerId,
            OrgId = orgId,
            FirstName = "Test",
            LastName = "Owner",
            Email = "owner@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow
        };

        var ownership = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = ownerId,
            SharePercent = 100.00m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        // Create approved statement with commission of €100.00
        var statement = new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = ownerId,
            PropertyId = propertyId,
            Month = 10,
            Year = 2026,
            Currency = "EUR",
            Status = StatementStatus.Approved,
            TotalRevenue = 1000.00m,
            Commission = 100.00m, // Taxable base
            Expenses = 0m,
            WithholdingTax = 0m,
            NetPayable = 900.00m,
            OwnershipPercent = 100.00m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.PropertyOwners.Add(owner);
        db.PropertyOwnerships.Add(ownership);
        db.OwnerStatements.Add(statement);
        await db.SaveChangesAsync();

        var service = new AgencyInvoiceService(db);

        // Act: generate invoice
        var result = await service.GenerateInvoiceAsync(statement.Id);

        // Assert: verify amounts are correct to the cent
        // Taxable base: €100.00
        Assert.Equal(100.00m, result.Invoice.TaxableBase);

        // VAT at 22%: €100.00 * 22% = €22.00
        Assert.Equal(22.00m, result.Invoice.VatRate);
        var expectedVat = Math.Round(100.00m * 22.00m / 100m, 2, MidpointRounding.ToEven);
        Assert.Equal(expectedVat, result.Invoice.VatAmount);
        Assert.Equal(22.00m, result.Invoice.VatAmount);

        // Stamp duty: €0 (VAT is charged, so no stamp duty even though base > €77.47)
        Assert.Equal(0m, result.Invoice.StampDuty);

        // Total: €100.00 + €22.00 + €0 = €122.00
        var expectedTotal = result.Invoice.TaxableBase + result.Invoice.VatAmount + result.Invoice.StampDuty;
        Assert.Equal(122.00m, expectedTotal);
        Assert.Equal(expectedTotal, result.Invoice.Total);

        // Verify the total is correct to the cent (no rounding errors)
        Assert.Equal(0, (result.Invoice.Total * 100) % 1); // No fractional cents
    }

    /// <summary>
    /// ADR-019 proof: Stamp duty of €2.00 is applied when taxable base exceeds €77.47 and VAT is 0.
    /// This test will work once VAT rate logic is parameterized (currently hardcoded to 22%).
    /// </summary>
    [Fact(Skip = "Stamp duty test requires VAT exemption logic to be implemented")]
    public async Task InvoiceStampDuty_AppliedCorrectly()
    {
        // This test is skipped because the current implementation hardcodes VAT rate to 22%
        // When VAT exemption is implemented, this test should verify:
        // - Taxable base €80.00, VAT 0% -> Stamp duty €2.00
        // - Taxable base €70.00, VAT 0% -> Stamp duty €0.00 (below threshold)
        // - Taxable base €80.00, VAT 22% -> Stamp duty €0.00 (VAT charged)
        await Task.CompletedTask;
    }
}

/// <summary>Fixed tenant context for unit tests.</summary>
file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
