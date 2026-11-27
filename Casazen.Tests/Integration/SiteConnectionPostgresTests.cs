using System.Buffers.Text;
using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteConnections;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// S022 (PLG-BE-CONNECT, site integration plan §10.4, AC 3 and 4) on real PostgreSQL, what the in-memory database of the unit tests cannot show: the code is
/// spent by one conditional update, so of several exchanges of one code at the same moment, each on its own context and connection as on several instances
/// of the API, exactly one gets a credential; two codes of one site exchanged at the same moment leave one active connection (the filtered unique index
/// and the retry of the loser); and the migration's index refuses a second active connection of the same org and site.
/// </summary>
public class SiteConnectionPostgresTests
{
    private const string Site = "https://www.quokka360.com";

    [PostgresFact]
    public async Task ExchangeAsync_SeveralRequestsWithOneCodeAtTheSameMoment_ExactlyOneGetsACredential()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("siteconn");
        var orgId = await SeedOrgAsync(database);
        var (verifier, challenge) = Pkce();
        var code = await AuthorizeAsync(database, orgId, challenge);

        async Task<SiteConnectionGrant?> ExchangeAsync()
        {
            await using var db = database.CreateContext();
            return await Service(db).ExchangeAsync(code, verifier, Site);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ExchangeAsync()));

        var grant = Assert.Single(results, result => result is not null);
        await using var check = database.CreateContext();
        var connection = Assert.Single(await check.SiteConnections.AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync());
        Assert.Equal(grant!.ConnectionId, connection.Id);
        Assert.NotNull((await check.SiteConnectionCodes.AsNoTracking().SingleAsync(c => c.OrgId == orgId)).UsedAt);
    }

    [PostgresFact]
    public async Task ExchangeAsync_TwoCodesOfOneSiteAtTheSameMoment_LeaveOneActiveConnection()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("siteconn");
        var orgId = await SeedOrgAsync(database);
        var first = Pkce();
        var second = Pkce();
        var firstCode = await AuthorizeAsync(database, orgId, first.Challenge);
        var secondCode = await AuthorizeAsync(database, orgId, second.Challenge);

        async Task<SiteConnectionGrant?> ExchangeAsync(string code, string verifier)
        {
            await using var db = database.CreateContext();
            return await Service(db).ExchangeAsync(code, verifier, Site);
        }

        var results = await Task.WhenAll(ExchangeAsync(firstCode, first.Verifier), ExchangeAsync(secondCode, second.Verifier));

        Assert.All(results, result => Assert.NotNull(result));
        await using var check = database.CreateContext();
        var rows = await check.SiteConnections.AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync();
        Assert.Equal(2, rows.Count);
        var active = Assert.Single(rows, row => row.RevokedAt is null);
        Assert.Contains(active.Id, results.Select(result => result!.ConnectionId));
        var stillOpens = await Service(check).AuthenticateAsync(results.Single(result => result!.ConnectionId == active.Id)!.AccessToken);
        Assert.Equal(new SiteConnectionPrincipal(active.Id, orgId), stillOpens);
    }

    [PostgresFact]
    public async Task Index_ASecondActiveConnectionOfTheSameOrgAndSite_IsRefused_ARevokedOneDoesNotCount()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("siteconn");
        var orgId = await SeedOrgAsync(database);
        await using var db = database.CreateContext();
        db.SiteConnections.Add(Row(orgId, revokedAt: DateTime.UtcNow));
        db.SiteConnections.Add(Row(orgId));
        await db.SaveChangesAsync();

        db.SiteConnections.Add(Row(orgId));
        var conflict = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(conflict.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal(SiteConnection.ActiveSiteIndexName, postgres.ConstraintName);
    }

    private static SiteConnectionService Service(Casazen.Infrastructure.Data.AppDbContext db) =>
        new(db, TimeProvider.System, NullLogger<SiteConnectionService>.Instance);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, SiteConnectionTokens.S256(verifier));
    }

    private static async Task<string> AuthorizeAsync(PostgresTestDatabase database, Guid orgId, string challenge)
    {
        await using var db = database.CreateContext();
        var authorization = await Service(db).AuthorizeAsync(
            orgId, "auth0|billing-admin", Site, Site + "/wp-admin/options-general.php?page=casazen", "state-1", challenge, SiteConnectionRules.CodeChallengeMethod);
        Assert.NotNull(authorization);
        return QueryHelpers.ParseQuery(new Uri(authorization.RedirectUrl).Query)[SiteConnectionRules.CodeQueryParameter].ToString();
    }

    private static SiteConnection Row(Guid orgId, DateTime? revokedAt = null) => new()
    {
        OrgId = orgId,
        SiteUrl = Site,
        TokenHash = SiteConnectionTokens.Hash(SiteConnectionTokens.NewCredential()),
        CreatedByUserId = "auth0|billing-admin",
        CreatedAt = DateTime.UtcNow,
        RevokedAt = revokedAt,
    };

    private static async Task<Guid> SeedOrgAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var org = new OrgEntity
        {
            Name = "Quokka360",
            Slug = "quokka-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Quokka360",
            ContactEmail = "info@quokka.example",
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }
}
