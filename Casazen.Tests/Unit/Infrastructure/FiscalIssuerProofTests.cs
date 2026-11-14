using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// FIS-02, NF-82, E06/F02/US02/T01: the profile of the issuer of an organization is data of that org and nobody else's. The data rule that costs if
/// it fails: the profile of another org is never returned to a caller, whatever the query, because the table carries the global tenant filter like
/// every <see cref="ITenantOwned"/> entity (TN-2); the single issuer of a booking and the single profile of an org are unique keys of the model.
/// </summary>
public class FiscalIssuerProofTests
{
    /// <summary>
    /// PROOF TEST: data rule (tenant isolation). Two orgs have a profile of their issuer. The caller of org A reads its own only: the one of org B
    /// is neither in the list nor found by its org (the 404 of the endpoints), and a caller with no org matches nothing.
    /// </summary>
    [Fact]
    public async Task FiscalIssuerProfile_OtherOrgRow_Invisible_Test()
    {
        var databaseName = $"fiscal-issuer-profile-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mine = NewProfile(orgA, "Casa Rossi S.r.l.");
        var theirs = NewProfile(orgB, "Casa Bianchi S.r.l.");

        // The system writes both rows (no caller, so no filter), as a job or the seed would.
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.OrgFiscalIssuerProfiles.AddRange(mine, theirs);
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));
        var visible = await asOrgA.OrgFiscalIssuerProfiles.AsNoTracking().ToListAsync();

        var row = Assert.Single(visible);
        Assert.Equal(mine.Id, row.Id);
        Assert.Equal(orgA, row.OrgId);
        Assert.Equal("Casa Rossi S.r.l.", row.LegalName);

        // By org too: the profile of the other org is not there.
        Assert.Null(await asOrgA.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgB));
        Assert.False(await asOrgA.OrgFiscalIssuerProfiles.AsNoTracking().AnyAsync(p => p.Id == theirs.Id));

        // A caller with no org is fail-closed: it matches nothing.
        await using var nobody = NewDb(databaseName, new FixedTenantContext(null));
        Assert.Empty(await nobody.OrgFiscalIssuerProfiles.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// E17/F01/US01/T01, T02: one profile for each organization (no owner) and one for each owner who issues, two partial unique indexes; the issuer rows of a
    /// booking are one for each document (a booking may have the receipt of its owner and the invoice of the agency), no longer one for each booking.
    /// </summary>
    [Fact]
    public void Model_OneProfileForEachOrgAndOwner_OneIssuerRowForEachDocument_UniqueAndTenantOwned()
    {
        using var db = NewDb($"fiscal-issuer-model-{Guid.NewGuid()}", tenant: null);

        var profile = db.Model.FindEntityType(typeof(OrgFiscalIssuerProfile))!;
        var profileIndex = Assert.Single(profile.GetIndexes(), i => i.GetDatabaseName() == OrgFiscalIssuerProfile.OrgIndexName);
        Assert.True(profileIndex.IsUnique);
        Assert.Equal([nameof(OrgFiscalIssuerProfile.OrgId)], profileIndex.Properties.Select(p => p.Name));
        Assert.Equal("\"PropertyOwnerId\" IS NULL", profileIndex.GetFilter());
        var ownerIndex = Assert.Single(profile.GetIndexes(), i => i.GetDatabaseName() == OrgFiscalIssuerProfile.OwnerIndexName);
        Assert.True(ownerIndex.IsUnique);
        Assert.Equal([nameof(OrgFiscalIssuerProfile.PropertyOwnerId)], ownerIndex.Properties.Select(p => p.Name));
        Assert.Equal("\"PropertyOwnerId\" IS NOT NULL", ownerIndex.GetFilter());

        var issuer = db.Model.FindEntityType(typeof(BookingDocumentIssuer))!;
        var documentIndex = Assert.Single(issuer.GetIndexes(), i => i.GetDatabaseName() == BookingDocumentIssuer.DocumentIndexName);
        Assert.True(documentIndex.IsUnique);
        Assert.Equal([nameof(BookingDocumentIssuer.LockedByDocumentId)], documentIndex.Properties.Select(p => p.Name));
        Assert.DoesNotContain(issuer.GetIndexes(), i => i.IsUnique && i.Properties.Any(p => p.Name == nameof(BookingDocumentIssuer.BookingId)));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(issuer.GetForeignKeys()).DeleteBehavior);

        Assert.True(typeof(ITenantOwned).IsAssignableFrom(typeof(OrgFiscalIssuerProfile)));
        Assert.True(typeof(ITenantOwned).IsAssignableFrom(typeof(BookingDocumentIssuer)));
        Assert.DoesNotContain(issuer.GetProperties(), p => p.Name == nameof(BookingDocumentIssuer.LockedByDocumentId) && p.IsForeignKey());
    }

    private static OrgFiscalIssuerProfile NewProfile(Guid orgId, string legalName) => new()
    {
        OrgId = orgId,
        IssuerMode = DocumentIssuerMode.CasaZen,
        DocumentRegime = DocumentRegime.VatOrdinary,
        LegalName = legalName,
        CountryCode = "IT",
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
