using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit;

/// <summary>
/// ADR-018 proof tests for withholding tax calculation and F24 generation.
/// Tests money rules: correct withholding base, 21% rate, VAT exemption, F24 totals.
/// </summary>
public class WithholdingProofTests
{
    /// <summary>
    /// ADR-018 proof: Withholding calculation with known inputs produces correct 21% amount.
    /// Tests multiple scenarios:
    /// - Basic calculation: gross revenue only
    /// - With flat-rate cleaning
    /// - With non-deductible commission
    /// - VAT holder exemption (zero withholding)
    /// </summary>
    [Fact]
    public async Task WithholdingCalculation_ExampleCases()
    {
        // Arrange: in-memory database
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"WithholdingCalcTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        // Scenario 1: Private owner, gross revenue 1000, deductible commission
        // Expected withholding: 1000 * 0.21 = 210.00
        var result1 = await SetupAndCalculateWithholding(
            db, orgId,
            taxRegime: OwnerTaxRegime.Private,
            grossRevenue: 1000.00m,
            commission: 150.00m,
            commissionTreatment: CommissionTreatment.Deductible,
            cleaningFlatRate: null,
            linenFlatRate: null);

        Assert.Equal(1000.00m, result1!.GrossBase);
        Assert.Equal(210.00m, result1.WithholdingAmount);

        // Scenario 2: Private owner, gross revenue 1000, cleaning flat rate 50, deductible commission
        // Expected withholding: (1000 + 50) * 0.21 = 220.50
        var result2 = await SetupAndCalculateWithholding(
            db, orgId,
            taxRegime: OwnerTaxRegime.Private,
            grossRevenue: 1000.00m,
            commission: 150.00m,
            commissionTreatment: CommissionTreatment.Deductible,
            cleaningFlatRate: 50.00m,
            linenFlatRate: null);

        Assert.Equal(1050.00m, result2!.GrossBase);
        Assert.Equal(220.50m, result2.WithholdingAmount);

        // Scenario 3: Private owner, gross revenue 1000, commission 150 not deductible
        // Expected withholding: (1000 + 150) * 0.21 = 241.50
        var result3 = await SetupAndCalculateWithholding(
            db, orgId,
            taxRegime: OwnerTaxRegime.Private,
            grossRevenue: 1000.00m,
            commission: 150.00m,
            commissionTreatment: CommissionTreatment.NotDeductible,
            cleaningFlatRate: null,
            linenFlatRate: null);

        Assert.Equal(1150.00m, result3!.GrossBase);
        Assert.Equal(241.50m, result3.WithholdingAmount);

        // Scenario 4: VAT holder, any amounts
        // Expected withholding: 0.00 (ADR-018-R5)
        var result4 = await SetupAndCalculateWithholding(
            db, orgId,
            taxRegime: OwnerTaxRegime.VatHolder,
            grossRevenue: 1000.00m,
            commission: 150.00m,
            commissionTreatment: CommissionTreatment.Deductible,
            cleaningFlatRate: 50.00m,
            linenFlatRate: null);

        // VAT holder should have no withholding logs created
        Assert.Null(result4);
    }

    /// <summary>
    /// ADR-018-R8 proof: F24 total matches sum of withholding log amounts.
    /// Tests that the F24 generation correctly aggregates all withholding logs for a period.
    /// </summary>
    [Fact]
    public async Task F24Total_EqualsWithholdingLogSum()
    {
        // Arrange: in-memory database
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"F24TotalTest_{Guid.NewGuid()}")
            .Options;

        var dataProtection = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();

        await using var db = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);

        // Create multiple withholding logs for the same period
        var log1 = new WithholdingLog
        {
            OrgId = orgId,
            OwnerId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            Month = 10,
            Year = 2026,
            CashYear = 2026,
            StayYear = 2026,
            GrossBase = 1000.00m,
            WithholdingAmount = 210.00m
        };

        var log2 = new WithholdingLog
        {
            OrgId = orgId,
            OwnerId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            Month = 10,
            Year = 2026,
            CashYear = 2026,
            StayYear = 2026,
            GrossBase = 1500.00m,
            WithholdingAmount = 315.00m
        };

        var log3 = new WithholdingLog
        {
            OrgId = orgId,
            OwnerId = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            BookingId = Guid.NewGuid(),
            Month = 10,
            Year = 2026,
            CashYear = 2026,
            StayYear = 2026,
            GrossBase = 800.00m,
            WithholdingAmount = 168.00m
        };

        db.WithholdingLogs.AddRange(log1, log2, log3);
        await db.SaveChangesAsync();

        // Act: Generate F24
        var withholdingService = new WithholdingService(db);
        var f24 = await withholdingService.GenerateF24Async(orgId, 10, 2026);

        // Assert: F24 total equals sum of withholding amounts
        var expectedTotal = 210.00m + 315.00m + 168.00m; // 693.00
        Assert.Equal(expectedTotal, f24.TotalAmount);
        Assert.Equal("1919", f24.TaxCode);
        Assert.Equal(10, f24.ReferenceMonth);
        Assert.Equal(2026, f24.ReferenceYear);

        // Assert: Due date is 16th of following month (November 16, 2026)
        var expectedDueDate = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expectedDueDate, f24.DueDate);
    }

    /// <summary>
    /// Helper method to set up test data and calculate withholding for a single booking.
    /// Returns the created WithholdingLog or null if no withholding was calculated (VAT holder).
    /// </summary>
    private static async Task<WithholdingLog?> SetupAndCalculateWithholding(
        AppDbContext db,
        Guid orgId,
        OwnerTaxRegime taxRegime,
        decimal grossRevenue,
        decimal commission,
        CommissionTreatment commissionTreatment,
        decimal? cleaningFlatRate,
        decimal? linenFlatRate)
    {
        var propertyId = Guid.NewGuid();
        var propertyOwnerId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var guestId = Guid.NewGuid();

        // Create test entities
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
            Id = propertyOwnerId,
            OrgId = orgId,
            FirstName = "Test",
            LastName = "Owner",
            Email = "test@example.com",
            TaxRegime = taxRegime,
            CreatedAt = DateTime.UtcNow
        };

        var ownership = new PropertyOwnership
        {
            OrgId = orgId,
            PropertyId = propertyId,
            PropertyOwnerId = propertyOwnerId,
            SharePercent = 100.00m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-1)
        };

        var mandate = new OwnerMandate
        {
            OrgId = orgId,
            PropertyOwnershipId = ownership.Id,
            CommissionPercent = 15.00m,
            CleaningFlatRate = cleaningFlatRate,
            LinenFlatRate = linenFlatRate,
            WithholdingTreatment = commissionTreatment,
            MandatePdfUrl = "https://example.com/mandate.pdf",
            EffectiveFrom = DateTime.UtcNow.AddMonths(-1)
        };

        var guest = new Guest
        {
            Id = guestId,
            OrgId = orgId,
            FirstName = "Test",
            LastName = "Guest",
            Email = "guest@example.com",
            PhoneNumber = "+39123456789"
        };

        var booking = new Booking
        {
            Id = bookingId,
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 10, 1),
            CheckOutDate = new DateTime(2026, 10, 5),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = grossRevenue
        };

        var statement = new OwnerStatement
        {
            OrgId = orgId,
            PropertyOwnerId = propertyOwnerId,
            PropertyId = propertyId,
            Month = 10,
            Year = 2026,
            Status = StatementStatus.Draft,
            OwnershipPercent = 100.00m
        };

        var line = new StatementLine
        {
            OrgId = orgId,
            StatementId = statement.Id,
            Type = OwnerStatementLineType.Booking,
            BookingId = bookingId,
            GrossRevenue = grossRevenue,
            Commission = commission,
            CashYear = 2026,
            StayYear = 2026
        };

        db.Properties.Add(property);
        db.PropertyOwners.Add(owner);
        db.PropertyOwnerships.Add(ownership);
        db.OwnerMandates.Add(mandate);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.OwnerStatements.Add(statement);
        db.StatementLines.Add(line);
        await db.SaveChangesAsync();

        // Act: Calculate withholding
        var withholdingService = new WithholdingService(db);
        var logs = await withholdingService.CalculateWithholdingAsync(statement.Id);

        // Return the first log or null
        return logs.FirstOrDefault();
    }
}

/// <summary>Fixed tenant context for unit tests.</summary>
file sealed class FixedTenantContext(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;
    public bool FilterEnabled => true;
}
