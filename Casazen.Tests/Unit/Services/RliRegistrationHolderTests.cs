using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03 (S5): the RLI delega is the landlord, so it is given by the holder of the org (its owner or an administrator), not
/// by whoever created the property. A property manager of the team creates properties too and must not be able to authorize
/// a filing in the name of the landlord; an account in no org team keeps the rule of before the team.
/// </summary>
public class RliRegistrationHolderTests
{
    private const string OwnerId = "auth0|titolare";
    private const string ManagerId = "auth0|gestore";

    private sealed record Fixture(AppDbContext Db, Guid OrgId, Guid LeaseId);

    private static async Task<Fixture> SeedAsync(string propertyCreatorId, bool withTeam = true)
    {
        var db = NewDb();
        var org = AddOrg(db);
        AddUser(db, OwnerId, org.Id, UserRole.PropertyOwner);
        AddUser(db, ManagerId, org.Id);
        AddUser(db, "auth0|senza-team", org.Id, UserRole.PropertyOwner);
        if (withTeam)
        {
            AddMember(db, OwnerId, org.Id, OrgRole.Owner);
            AddMember(db, ManagerId, org.Id, OrgRole.PropertyManager);
        }

        var property = HostScopeScenario.NewProperty(org.Id, propertyCreatorId, "Trullo");
        db.Properties.Add(property);
        var lease = new LeaseContract
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            Status = LeaseStatus.Signed,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = HostScopeScenario.Day(2027, 1, 1),
            EndDate = HostScopeScenario.Day(2030, 12, 31),
            MonthlyRent = 800m,
            SignedPdfStoragePath = null,
        };
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();
        return new Fixture(db, org.Id, lease.Id);
    }

    private static RliRegistrationService NewService(AppDbContext db)
    {
        var provider = new Mock<ILeaseRegistrationProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.RliProvider)).Returns(true);
        return new RliRegistrationService(
            db,
            provider.Object,
            flags.Object,
            Mock.Of<IFileStorage>(),
            Mock.Of<IApeComplianceService>(),
            Options.Create(new RliOptions()),
            new OrgHolderService(db),
            NullLogger<RliRegistrationService>.Instance);
    }

    private static readonly RegistrationAuthorizationRequest Delega = new(new RliOptions().TosVersion, true);

    [Fact]
    public async Task SubmitToProviderAsync_TheOwnerOfTheOrgWhoDidNotCreateTheProperty_PassesTheGate()
    {
        var f = await SeedAsync(propertyCreatorId: ManagerId);
        await using var _ = f.Db;

        // The gate is passed: the next rule of the filing is the one that answers (there is no signed contract to send).
        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(f.Db).SubmitToProviderAsync(f.LeaseId, OwnerId, Delega));

        Assert.Equal(RliRegistrationErrorCodes.SignedPdfMissing, error.Code);
    }

    [Fact]
    public async Task SubmitToProviderAsync_ThePropertyManagerWhoCreatedTheProperty_IsNotTheLandlord()
    {
        var f = await SeedAsync(propertyCreatorId: ManagerId);
        await using var _ = f.Db;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => NewService(f.Db).SubmitToProviderAsync(f.LeaseId, ManagerId, Delega));
    }

    [Fact]
    public async Task SubmitToProviderAsync_AnAccountInNoTeam_IsTheLandlordOfThePropertiesItCreated()
    {
        var f = await SeedAsync(propertyCreatorId: "auth0|senza-team", withTeam: false);
        await using var _ = f.Db;

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(f.Db).SubmitToProviderAsync(f.LeaseId, "auth0|senza-team", Delega));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => NewService(f.Db).SubmitToProviderAsync(f.LeaseId, "auth0|somebody-else", Delega));

        Assert.Equal(RliRegistrationErrorCodes.SignedPdfMissing, error.Code);
    }
}
