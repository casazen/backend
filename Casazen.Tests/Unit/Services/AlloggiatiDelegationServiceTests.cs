using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The data and privacy rule of the delegation of the Alloggiati transmission (E03/F01/US05/T02, AW-R02, ADR-015-R10): CasaZen may
/// transmit for an org only while the delegation is granted and not revoked, and the activity lines of the grant and of the revocation
/// carry ids and the code of the version, never a personal datum.
/// </summary>
public class AlloggiatiDelegationServiceTests
{
    private const string Admin = "auth0|delegation-admin";
    private const string Version = "2026-11-v1";

    [Fact]
    public async Task AlloggiatiDelegation_RevokedIsNotValid_Test()
    {
        var databaseName = Guid.NewGuid().ToString();
        var org = new OrgEntity
        {
            Name = "Agenzia Rossi TEST",
            DisplayName = "Agenzia Rossi TEST",
            Slug = $"agenzia-{Guid.NewGuid():N}",
            ContactEmail = "mario.rossi@example.com",
        };
        await using (var seed = OrgTeamTestData.NewDb(databaseName))
        {
            seed.Orgs.Add(org);
            await seed.SaveChangesAsync();
        }

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));
        await using (var db = OrgTeamTestData.NewDb(databaseName))
        {
            var service = new AlloggiatiDelegationService(db, new ActivityLog(db, clock), clock);

            // Before the grant: not valid.
            var before = await service.GetAsync(org.Id);
            Assert.False(before.Valid);
            Assert.Null(before.GrantedAt);

            // Granted: valid, with who, when and which version of the text.
            var granted = await service.GrantAsync(org.Id, Admin, Version);
            Assert.True(granted.Valid);
            Assert.Equal((clock.GetUtcNow().UtcDateTime, Admin, Version), (granted.GrantedAt!.Value, granted.GrantedByUserId, granted.TextVersion));

            // The same version again changes nothing; a version that is not a code is refused.
            await service.GrantAsync(org.Id, Admin, Version);
            var invalid = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.GrantAsync(org.Id, Admin, "Mario Rossi <mario.rossi@example.com>"));
            Assert.Equal("alloggiati_delegation_version_invalid", invalid.Code);

            // Revoked: not valid any more, the grant stays as it was; revoking again changes nothing.
            clock.Advance(TimeSpan.FromHours(1));
            var revoked = await service.RevokeAsync(org.Id, Admin);
            Assert.False(revoked.Valid);
            Assert.Equal<DateTime?>(clock.GetUtcNow().UtcDateTime, revoked.RevokedAt);
            Assert.Equal(Version, revoked.TextVersion);
            await service.RevokeAsync(org.Id, Admin);
            Assert.False((await service.GetAsync(org.Id)).Valid);
        }

        // One line for the grant and one for the revocation: the org as subject, the account as actor, the version code; nothing else.
        await using var verify = OrgTeamTestData.NewDb(databaseName);
        var lines = await verify.OrgActivityEntries.AsNoTracking().Where(e => e.OrgId == org.Id).OrderBy(e => e.When).ToListAsync();
        Assert.Equal(
            new[] { OrgActivityType.AlloggiatiDelegationGranted, OrgActivityType.AlloggiatiDelegationRevoked },
            lines.Select(line => line.Type));
        Assert.All(lines, line => Assert.Equal((org.Id.ToString(), Admin), (line.SubjectId, line.ActorUserId)));
        Assert.Equal(new Dictionary<string, string> { [OrgActivityDetailKeys.DelegationVersion] = Version }, OrgActivityDetails.Parse(lines[0].DetailsJson));
        Assert.Empty(OrgActivityDetails.Parse(lines[1].DetailsJson));
        Assert.All(lines, line =>
        {
            var written = $"{line.SubjectId} {line.ActorUserId} {line.DetailsJson}";
            Assert.DoesNotContain("Rossi", written, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@", written, StringComparison.Ordinal);
        });
    }
}
