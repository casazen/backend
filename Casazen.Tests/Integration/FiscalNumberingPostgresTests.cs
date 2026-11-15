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
/// NF-83, NF-116, ADR-019-R4 (E06/F01/US02/T02, E17/F01/US01/T01) on a real PostgreSQL database: the fiscal numbers of one org, issuer and series are
/// consecutive, never duplicated under parallel issues, and a reservation whose transaction rolls back leaves no gap; two issuers of one org (the organization
/// and an owner with its own sezionale) have their own numbers in the same series; a new series of the organization starts above the numbers already used,
/// the legacy agency invoices included, an owner's above its own only.
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
        var issuer = await OrganizationProfileAsync(database, orgId);

        // A reservation whose document is not saved: the transaction rolls back and its number is given again.
        await using (var db = Context(database))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var lost = await Service(db).ReserveNextAsync(orgId, issuer, "2026");
            Assert.Equal(1, lost.Number);
            await transaction.RollbackAsync();
        }

        var issues = Enumerable.Range(0, 12).Select(_ => IssueAsync(database, orgId, issuer, "2026"));
        var numbers = await Task.WhenAll(issues);

        Assert.Equal(Enumerable.Range(1, 12), numbers.Order());
        await using var check = Context(database);
        var stored = await check.FiscalDocuments.Where(d => d.OrgId == orgId).Select(d => d.SequenceNumber!.Value).OrderBy(n => n).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 12), stored);
        Assert.Equal("2026/12", (await check.FiscalDocuments.SingleAsync(d => d.OrgId == orgId && d.SequenceNumber == 12)).Number);
        Assert.Equal(12, (await check.FiscalNumberSequences.SingleAsync(s => s.OrgId == orgId && s.IssuerProfileId == issuer && s.Series == "2026")).LastNumber);
    }

    /// <summary>
    /// PROOF TEST: data rule (E17/F01/US01/T01). The organization and an owner who issues with its own sezionale, both in the series 2026, in parallel and with a
    /// rolled-back reservation of the owner: each has its numbers 1 to n with no gap and no duplicate, the two never share a counter, and the database refuses a
    /// number twice for one issuer and series while the same number of another issuer is fine.
    /// </summary>
    [PostgresFact]
    public async Task Numbering_GapFreePerIssuerAndSeries_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdissuer");
        var orgId = Guid.NewGuid();
        var (organization, owner) = await AgencyWithOwnerProfileAsync(database, orgId, legacyInvoiceNumber: null);

        await using (var db = Context(database))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            Assert.Equal(1, (await Service(db).ReserveNextAsync(orgId, owner, "2026")).Number);
            await transaction.RollbackAsync();
        }

        var issues = Enumerable.Range(0, 8).Select(i => IssueAsync(database, orgId, i % 2 == 0 ? organization : owner, "2026")).ToList();
        await Task.WhenAll(issues);

        await using var check = Context(database);
        foreach (var issuer in new[] { organization, owner })
        {
            var numbers = await check.FiscalDocuments
                .Where(d => d.OrgId == orgId && d.IssuerProfileId == issuer && d.Series == "2026")
                .Select(d => d.SequenceNumber!.Value)
                .OrderBy(n => n)
                .ToListAsync();
            Assert.Equal(Enumerable.Range(1, 4), numbers);
            Assert.Equal(4, (await check.FiscalNumberSequences.SingleAsync(s => s.OrgId == orgId && s.IssuerProfileId == issuer && s.Series == "2026")).LastNumber);
        }

        // The database itself: the number 4 again is refused for the owner, while the same 2026/4 of the two issuers lives side by side.
        await using var twice = Context(database);
        twice.FiscalDocuments.Add(Issued(orgId, owner, "2026", 4));
        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => twice.SaveChangesAsync());
        Assert.Contains(FiscalDocument.SeriesNumberIndexName, refused.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task FiscalNumbering_NewSeries_StartsAboveTheLegacyAgencyInvoices_AndRefusesALongSeries()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdseed");
        var orgId = Guid.NewGuid();
        var (organization, owner) = await AgencyWithOwnerProfileAsync(database, orgId, legacyInvoiceNumber: "7");

        // The organization continues above the legacy agency invoice of its series; the owner's series is its own and starts from 1.
        Assert.Equal(8, await IssueAsync(database, orgId, organization, "2026"));
        Assert.Equal(1, await IssueAsync(database, orgId, owner, "2026"));

        await using var refused = Context(database);
        await using var transaction = await refused.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Service(refused).ReserveNextAsync(orgId, organization, "TROPPO-LUNGA"));
        Assert.Equal(FiscalNumberingErrors.SeriesTooLong, error.Code);
    }

    /// <summary>One issue as the document service does it: a READ COMMITTED transaction, the number of the issuer, the document, the commit.</summary>
    private static async Task<int> IssueAsync(PostgresTestDatabase database, Guid orgId, Guid issuerProfileId, string series)
    {
        await using var db = Context(database);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var number = await Service(db).ReserveNextAsync(orgId, issuerProfileId, series);
        db.FiscalDocuments.Add(Issued(orgId, issuerProfileId, number.Series, number.Number));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return number.Number;
    }

    private static FiscalDocument Issued(Guid orgId, Guid issuerProfileId, string series, int number) => new()
    {
        OrgId = orgId,
        IssuerProfileId = issuerProfileId,
        Status = FiscalDocumentStatus.Issued,
        Series = series,
        SequenceNumber = number,
        Number = FiscalNumberFormat.Display(series, number),
        IssueDate = new DateOnly(2026, 11, 12),
        Currency = "EUR",
        SdiStatus = FiscalDocumentSdiStatuses.ToSend,
    };

    /// <summary>The profile of an organization (the table has no foreign key to the organizations).</summary>
    private static async Task<Guid> OrganizationProfileAsync(PostgresTestDatabase database, Guid orgId)
    {
        await using var db = Context(database);
        var profile = new OrgFiscalIssuerProfile { OrgId = orgId, LegalName = "TEST Agenzia Numeri S.r.l." };
        db.OrgFiscalIssuerProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    /// <summary>An agency with its profile, an owner with an issuer profile of its own, and, when given, a legacy agency invoice of the series 2026.</summary>
    private static async Task<(Guid Organization, Guid Owner)> AgencyWithOwnerProfileAsync(PostgresTestDatabase database, Guid orgId, string? legacyInvoiceNumber)
    {
        await using var db = Context(database);
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
        var owner = new PropertyOwner { OrgId = orgId, FirstName = "Test", LastName = "Owner", Email = $"fd-owner-{orgId:N}@example.com" };
        var organization = new OrgFiscalIssuerProfile { OrgId = orgId, LegalName = "TEST Agenzia Numeri S.r.l." };
        var ownerProfile = new OrgFiscalIssuerProfile { OrgId = orgId, PropertyOwnerId = owner.Id, LegalName = "TEST Proprietario Srl" };
        db.Orgs.Add(org);
        db.PropertyOwners.Add(owner);
        db.OrgFiscalIssuerProfiles.AddRange(organization, ownerProfile);
        if (legacyInvoiceNumber is not null)
            db.AgencyInvoices.Add(new AgencyInvoice { OrgId = orgId, OwnerId = owner.Id, Series = "2026", InvoiceNumber = legacyInvoiceNumber, Month = 1, Year = 2026 });
        await db.SaveChangesAsync();
        return (organization.Id, ownerProfile.Id);
    }

    private static FiscalNumberingService Service(AppDbContext db) =>
        new(db, TimeProvider.System, NullLogger<FiscalNumberingService>.Instance);

    private static AppDbContext Context(PostgresTestDatabase database) => new(database.CreateOptions(), tenantContext: null, Provider);
}
