using System.Data;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-83, ADR-019-R4 (E06/F01/US02/T02) on a real PostgreSQL database: the fiscal numbers of one org and series are consecutive, never duplicated under
/// parallel issues, and a reservation whose transaction rolls back leaves no gap; a new series starts above the numbers already used, the legacy agency
/// invoices included.
/// </summary>
public class FiscalNumberingPostgresTests
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    /// <summary>PROOF TEST: money and data rule. Twelve parallel issues and one rolled back: numbers 1 to 12, no gap, no duplicate.</summary>
    [PostgresFact]
    public async Task FiscalNumbering_ConcurrentIssue_NoGapsNoDuplicates_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdseq");
        var orgId = Guid.NewGuid();

        // A reservation whose document is not saved: the transaction rolls back and its number is given again.
        await using (var db = Context(database))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var lost = await Service(db).ReserveNextAsync(orgId, "2026");
            Assert.Equal(1, lost.Number);
            await transaction.RollbackAsync();
        }

        var issues = Enumerable.Range(0, 12).Select(_ => IssueAsync(database, orgId, "2026"));
        var numbers = await Task.WhenAll(issues);

        Assert.Equal(Enumerable.Range(1, 12), numbers.Order());
        await using var check = Context(database);
        var stored = await check.FiscalDocuments.Where(d => d.OrgId == orgId).Select(d => d.SequenceNumber!.Value).OrderBy(n => n).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 12), stored);
        Assert.Equal("2026/12", (await check.FiscalDocuments.SingleAsync(d => d.OrgId == orgId && d.SequenceNumber == 12)).Number);
        Assert.Equal(12, (await check.FiscalNumberSequences.SingleAsync(s => s.OrgId == orgId && s.Series == "2026")).LastNumber);
    }

    [PostgresFact]
    public async Task FiscalNumbering_NewSeries_StartsAboveTheLegacyAgencyInvoices_AndRefusesALongSeries()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdseed");
        var orgId = Guid.NewGuid();
        await using (var db = Context(database))
        {
            var org = new OrgEntity
            {
                Id = orgId,
                Name = "Agenzia Numeri",
                Slug = $"fd-seed-{orgId:N}"[..28],
                DisplayName = "Agenzia Numeri",
                ContactEmail = "fd-seed@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Agency,
            };
            var owner = new PropertyOwner { OrgId = orgId, FirstName = "Test", LastName = "Owner", Email = "fd-seed-owner@example.com" };
            db.Orgs.Add(org);
            db.PropertyOwners.Add(owner);
            db.AgencyInvoices.Add(new AgencyInvoice { OrgId = orgId, OwnerId = owner.Id, Series = "2026", InvoiceNumber = "7", Month = 1, Year = 2026 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(8, await IssueAsync(database, orgId, "2026"));

        await using var refused = Context(database);
        await using var transaction = await refused.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service(refused).ReserveNextAsync(orgId, "TROPPO-LUNGA"));
        Assert.Equal(FiscalNumberingErrors.SeriesTooLong, error.Code);
    }

    /// <summary>One issue as the document service does it: a READ COMMITTED transaction, the number, the document, the commit.</summary>
    private static async Task<int> IssueAsync(PostgresTestDatabase database, Guid orgId, string series)
    {
        await using var db = Context(database);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var number = await Service(db).ReserveNextAsync(orgId, series);
        db.FiscalDocuments.Add(new FiscalDocument
        {
            OrgId = orgId,
            Status = FiscalDocumentStatus.Issued,
            Series = number.Series,
            SequenceNumber = number.Number,
            Number = number.Display,
            IssueDate = new DateOnly(2026, 11, 12),
            Currency = "EUR",
            SdiStatus = FiscalDocumentSdiStatuses.ToSend,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return number.Number;
    }

    private static FiscalNumberingService Service(AppDbContext db) =>
        new(db, TimeProvider.System, NullLogger<FiscalNumberingService>.Instance);

    private static AppDbContext Context(PostgresTestDatabase database) => new(database.CreateOptions(), tenantContext: null, Provider);
}
