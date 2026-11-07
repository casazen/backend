using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The owner portfolio of an agency (E10/F02/US02), run on the PostgreSQL provider with all three kinds of scope and no server (see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL, and the
/// readiness, the import, the preview and the acceptance read through the reach of the caller (<c>InScope</c> on the periods, a sub-query of the
/// reachable properties) and across orgs (the tenant filter ignored): this fails here, before CI, when one of those has no translation.
/// </summary>
public class OwnerOnboardingNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Casazen.Tests.Unit.FakeTimeProvider Clock = new(HostScopeScenario.Now);

    public static TheoryData<string> Scopes => ["collaborator", "account-in-no-team", "whole-org"];

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    private static OwnerOnboardingService Onboarding(AppDbContext db) =>
        new(db, new ActivityLog(db, Clock), Clock, NullLogger<OwnerOnboardingService>.Instance);

    private static OwnerInvitationService Invitations(AppDbContext db)
    {
        var queue = new Mock<IEmailQueue>();
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.test" }));
        return new OwnerInvitationService(
            db, queue.Object, NullLogger<OwnerInvitationService>.Instance, links, new ActivityLog(db, Clock), Onboarding(db), Mock.Of<IAuth0ManagementService>(), Clock);
    }

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task Readiness_AndTheListOfTheInvitations_TranslateToSql(string scopeKind)
    {
        var db = NpgsqlTranslationProbe.NewContext();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Onboarding(db).GetReadinessAsync(ScopeOf(scopeKind), CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Invitations(db).ListAsync(ScopeOf(scopeKind), CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Invitations(db).PreviewBatchAsync(ScopeOf(scopeKind), null, CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Invitations(db).PreviewBatchAsync(ScopeOf(scopeKind), [Guid.NewGuid()], CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task TheImportOfTheOwners_TranslatesToSql_ForTheCheckAndForThePeriods(string scopeKind)
    {
        // The property of the file is found (the probe answers its row), so the queries that follow it are compiled too: the owners inside the
        // reach, the keys the caller cannot see, the owners shared outside the reach, the periods and the mandates of the property.
        static DataTable? Rows(string sql)
        {
            if (!Regex.IsMatch(sql.TrimStart(), @"^SELECT\s+\w+\.""Id"",\s*\w+\.""CinCode""\s+FROM ""Properties""", RegexOptions.CultureInvariant))
                return null;

            var table = new DataTable();
            table.Columns.Add("Id", typeof(Guid));
            table.Columns.Add("CinCode", typeof(string));
            table.Rows.Add(PropertyId, "IT058091C27G5FFZDZ");
            return table;
        }

        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, rows: Rows);
        var csv = "propertyId,cinCode,ownerKey,ownerId,legalName,fiscalCode,vatNumber,sharePercent,ownershipStart\n"
                  + $"{PropertyId},,A-1,,Anna Verdi,RSSMRA85T10A562S,,100,2026-01-01\n"
                  + $",IT058091C27G5FFZDZ,B-1,{Guid.NewGuid()},Bruno Neri,,01234567897,50,2026-01-01";

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => Onboarding(db).ImportAsync(ScopeOf(scopeKind), "auth0|admin", "owners.csv", Encoding.UTF8.GetBytes(csv), dryRun: true, CancellationToken.None));

        // The probe swallows what is not a translation failure, so say that it went as far as the queries of the owners and of the periods.
        Assert.Contains(statements, s => s.Contains("FROM \"PropertyOwners\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"PropertyOwnerships\"", StringComparison.Ordinal));
    }

    [Fact]
    public void TheQueriesOfTheAcceptance_TranslateToSql_AcrossOrgs()
    {
        var db = NpgsqlTranslationProbe.NewContext();
        var now = HostScopeScenario.Now.UtcDateTime;

        var claims = OwnerInvitationService.ClaimRows(db, Guid.NewGuid(), now).ToQueryString();
        var grantable = OwnerInvitationService.GrantableOwnerships(db, OrgId, Guid.NewGuid(), now).ToQueryString();

        // The tenant filter of the caller (who has no org) is ignored, the soft-delete filter of the properties is not, and the org is named.
        Assert.Contains("\"IsDeleted\"", claims, StringComparison.Ordinal);
        Assert.Contains("\"OwnerUserId\"", claims, StringComparison.Ordinal);
        Assert.Contains("\"OrgId\" = @", grantable, StringComparison.Ordinal);
        Assert.Contains("\"PropertyOwnerId\" = @", grantable, StringComparison.Ordinal);
    }
}
