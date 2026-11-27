using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F01/US01/T02 (NF-25, NF-110): the PDF and CSV of an owner statement are real files built from what is stored, every amount in the currency of
/// the statement, and an export never creates a withholding: generating, recalculating, approving and exporting a statement leaves the withholding
/// log empty and the stored totals as they were (money rule).
/// </summary>
public class StatementExportProofTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    [Fact]
    public async Task StatementExport_NeverCreatesWithholding_Test()
    {
        await using var db = NewDb();
        var (propertyId, ownerId) = await SeedOwnerWithPaidBookingAsync(db);

        var statements = new StatementService(db);
        var statement = await statements.GenerateStatementAsync(ownerId, propertyId, 10, 2026);
        await statements.RecalculateStatementAsync(statement.Id);
        await statements.ApproveStatementAsync(statement.Id);
        var before = await db.OwnerStatements.AsNoTracking().SingleAsync(s => s.Id == statement.Id);

        var export = Service(db);
        var pdf = await export.ExportStatementAsync(statement.Id, OrgId, StatementExportFormat.Pdf, "it");
        var csv = await export.ExportStatementAsync(statement.Id, OrgId, StatementExportFormat.Csv, "en");

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.StartsWith("text/csv", csv.ContentType);
        Assert.Empty(await db.WithholdingLogs.ToListAsync());
        var after = await db.OwnerStatements.AsNoTracking().Include(s => s.Lines).SingleAsync(s => s.Id == statement.Id);
        Assert.Equal(0m, after.WithholdingTax);
        Assert.All(after.Lines, line => Assert.Equal(0m, line.Withholding));
        Assert.Equal(before.TotalRevenue, after.TotalRevenue);
        Assert.Equal(before.NetPayable, after.NetPayable);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task StatementExport_PrintsStoredAmountsInTheStatementCurrency_NeverAnAssumedEuro()
    {
        await using var db = NewDb();
        var (propertyId, ownerId) = await SeedOwnerWithPaidBookingAsync(db);
        var statement = new OwnerStatement
        {
            OrgId = OrgId,
            PropertyOwnerId = ownerId,
            PropertyId = propertyId,
            Month = 9,
            Year = 2026,
            Currency = "CHF",
            Status = StatementStatus.Published,
            TotalRevenue = 1234.56m,
            Commission = 123.46m,
            Expenses = 10m,
            WithholdingTax = 0m,
            NetPayable = 1101.10m,
        };
        statement.Lines.Add(new StatementLine
        {
            OrgId = OrgId,
            StatementId = statement.Id,
            Type = OwnerStatementLineType.OtaRevenue,
            Label = "TEST channel",
            GrossRevenue = 1234.56m,
            Commission = 123.46m,
            CashYear = 2026,
        });
        db.OwnerStatements.Add(statement);
        await db.SaveChangesAsync();

        var csv = await Service(db).ExportStatementAsync(statement.Id, OrgId, StatementExportFormat.Csv, "it");
        var text = Encoding.UTF8.GetString(csv.Content);

        Assert.Contains("Lordo (CHF)", text, StringComparison.Ordinal);
        Assert.Contains("1234,56", text, StringComparison.Ordinal);
        Assert.Contains("1101,10", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EUR", text, StringComparison.Ordinal);
        Assert.DoesNotContain("€", text, StringComparison.Ordinal);
        Assert.Contains("non è una fattura", text, StringComparison.Ordinal);
    }

    /// <summary>The statement is a versioned ledger output: the files carry the version, the ledger totals as stored on it and the hash of its lines.</summary>
    [Fact]
    public async Task StatementExport_PrintsTheLedgerTotalsOfTheCurrentVersion_Test()
    {
        await using var db = NewDb();
        var (propertyId, ownerId) = await SeedOwnerWithPaidBookingAsync(db);
        var statements = new StatementService(db);
        var statement = await statements.GenerateStatementAsync(ownerId, propertyId, 10, 2026);
        await statements.ApproveStatementAsync(statement.Id);
        db.OwnerStatementVersions.Add(new OwnerStatementVersion
        {
            OrgId = OrgId,
            PropertyOwnerId = ownerId,
            PeriodYear = 2026,
            PeriodMonth = 10,
            VersionNumber = 3,
            Status = OwnerStatementVersionStatus.Approved,
            Currency = "CHF",
            CutoffAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc),
            OpeningBalance = 100.25m,
            Revenue = 900.35m,
            ChannelFees = -20.15m,
            Commission = -90.05m,
            Charges = -15.45m,
            Payments = -400.55m,
            Corrections = 0m,
            ClosingBalance = 475.65m,
            EntryCount = 6,
            EntriesSha256 = new string('a', 64),
            CreatedAt = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var export = Service(db);

        var csv = Encoding.UTF8.GetString((await export.ExportStatementAsync(statement.Id, OrgId, StatementExportFormat.Csv, "it")).Content);
        var pdf = await export.ExportStatementAsync(statement.Id, OrgId, StatementExportFormat.Pdf, "en");

        Assert.Contains("versione 3 (CHF)", csv, StringComparison.Ordinal);
        Assert.Contains("100,25", csv, StringComparison.Ordinal);
        Assert.Contains("900,35", csv, StringComparison.Ordinal);
        Assert.Contains("475,65", csv, StringComparison.Ordinal);
        Assert.Contains(new string('a', 64), csv, StringComparison.Ordinal);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Content, 0, 4));
    }

    [Fact]
    public async Task StatementExport_DraftIsRefused_AndAnotherOrgsStatementIsNotFound()
    {
        await using var db = NewDb();
        var (propertyId, ownerId) = await SeedOwnerWithPaidBookingAsync(db);
        var statement = await new StatementService(db).GenerateStatementAsync(ownerId, propertyId, 10, 2026);
        var export = Service(db);

        var draft = await Assert.ThrowsAsync<DomainRuleException>(
            () => export.ExportStatementAsync(statement.Id, OrgId, StatementExportFormat.Pdf, "it"));
        Assert.Equal(StatementExportErrors.StatementNotExportable, draft.Code);

        var foreign = await Assert.ThrowsAsync<NotFoundException>(
            () => export.ExportStatementAsync(statement.Id, Guid.NewGuid(), StatementExportFormat.Pdf, "it"));
        Assert.Equal(StatementExportErrors.StatementNotFound, foreign.Code);
    }

    [Fact]
    public async Task F24Pdf_IsTheStoredRecordOfTheOrg_AndNeverGeneratedOnExport()
    {
        await using var db = NewDb();
        db.F24Records.Add(new F24Record
        {
            OrgId = OrgId,
            ReferenceMonth = 10,
            ReferenceYear = 2026,
            TotalAmount = 42.5m,
            TaxCode = "1919",
            DueDate = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var export = Service(db);

        var file = await export.ExportF24Async(OrgId, 10, 2026, "it");

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(file.Content, 0, 4));
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => export.ExportF24Async(OrgId, 11, 2026, "it"));
        Assert.Equal(StatementExportErrors.F24NotFound, missing.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => export.ExportF24Async(Guid.NewGuid(), 10, 2026, "it"));
        Assert.Single(await db.F24Records.ToListAsync());
    }

    private static StatementExportService Service(AppDbContext db) =>
        new(db, new MigraDocPdfDocumentRenderer(), new ResxDocumentTexts(), NullLogger<StatementExportService>.Instance);

    private static AppDbContext NewDb() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"StatementExport_{Guid.NewGuid()}").Options,
            new ExportTenant(OrgId),
            new EphemeralDataProtectionProvider());

    private static async Task<(Guid PropertyId, Guid OwnerId)> SeedOwnerWithPaidBookingAsync(AppDbContext db)
    {
        var property = new Property
        {
            OrgId = OrgId,
            OwnerId = "test-owner",
            Name = "TEST Casa export",
            Address = "Via di Test 1",
            City = "Roma",
            CreatedAt = DateTime.UtcNow,
        };
        var owner = new PropertyOwner
        {
            OrgId = OrgId,
            FirstName = "Test",
            LastName = "Proprietario",
            Email = "owner.export@example.com",
            TaxRegime = OwnerTaxRegime.Private,
            CreatedAt = DateTime.UtcNow,
        };
        var ownership = new PropertyOwnership
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            PropertyOwnerId = owner.Id,
            SharePercent = 100m,
            OwnershipStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var mandate = new OwnerMandate
        {
            OrgId = OrgId,
            PropertyOwnerId = owner.Id,
            PropertyOwnershipId = ownership.Id,
            CommissionPercent = 20m,
            ValidFrom = new DateOnly(2026, 1, 1),
        };
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = "guest.export@example.com", PhoneNumber = "+39000000000" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 10, 3),
            CheckOutDate = new DateTime(2026, 10, 6),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 450m,
        };
        var payment = new Payment
        {
            OrgId = OrgId,
            BookingId = booking.Id,
            Amount = 450m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            ProcessedAt = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc),
        };

        db.Properties.Add(property);
        db.PropertyOwners.Add(owner);
        db.PropertyOwnerships.Add(ownership);
        db.OwnerMandates.Add(mandate);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return (property.Id, owner.Id);
    }
}

file sealed class ExportTenant(Guid orgId) : ITenantContext
{
    public Guid? OrgId => orgId;

    public bool FilterEnabled => true;
}
