using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-03b on a real PostgreSQL database, what EF InMemory cannot prove. (1) The export of the org narrows its property sections by
/// the scope of the caller in the SQL the code produces, with the foreign keys enforced. (2) The audience of a notification reads
/// the grant of the person in charge in the same statement and tells nobody who lost the property. (3) The access service takes the
/// responsibility away with the access, in the same transaction, and a person put in charge while the access is being taken away
/// is never left in charge without it (the appointment and the revocation take the same lock of the org's people). (4) Removing a
/// member releases what it was in charge of. The same rules, without a server, are in <c>OrgExportScopeTests</c>,
/// <c>HostNotificationAudienceReachTests</c> and <c>PropertyResponsibilityReleaseTests</c>.
/// </summary>
public class OrgScopeHardeningPostgresTests : IAsyncLifetime
{
    private const string OwnerId = "auth0|owner";
    private const string Anna = "auth0|anna";

    private PostgresTestDatabase? _database;
    private OrgInvitationTestKit _kit = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
        _kit = new OrgInvitationTestKit(() => _database.CreateContext());
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private OrgPropertyAccessService Access(AppDbContext db) =>
        new(db, _kit.Cache.Object, NullLogger<OrgPropertyAccessService>.Instance, _kit.Clock);

    private async Task<Guid> AddPropertyAsync(Guid orgId, string name, string creator = OwnerId, string? taxpayer = null)
    {
        await using var db = _database!.CreateContext();
        var property = HostScopeScenario.NewProperty(orgId, creator, name);
        property.TaxpayerFiscalCode = taxpayer;
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task<string?> ResponsibleAsync(Guid propertyId)
    {
        await using var db = _database!.CreateContext();
        return (await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId)).ResponsibleUserId;
    }

    // --- The audience on real SQL --------------------------------------------------------------------

    private async Task<List<string>> ToldAsync(Guid orgId, Guid propertyId, string? responsible, string creator)
    {
        await using var db = _database!.CreateContext();
        var told = await HostNotificationAudience.UsersToTell(db, orgId, propertyId, responsible, creator).Select(u => u.Id).ToListAsync();
        return told.Order(StringComparer.Ordinal).ToList();
    }

    [PostgresFact]
    public async Task Audience_ACollaboratorWhoLostThePropertyButIsStillNamed_IsNotTold_AndIsAgainOnceGivenItBack()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        var property = await AddPropertyAsync(org.Id, "Trullo");
        var other = await AddPropertyAsync(org.Id, "Casa Bianca");
        await using (var db = _database!.CreateContext())
        {
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [property]);
            await Access(db).SetResponsibleAsync(org.Id, property, Anna);
        }

        Assert.Equal([Anna, OwnerId], await ToldAsync(org.Id, property, Anna, OwnerId));

        // The access is taken away; the name is written back by hand (what a write on another instance could leave).
        await using (var db = _database.CreateContext())
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [other]);
        await using (var db = _database.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Properties\" SET \"ResponsibleUserId\" = {Anna} WHERE \"Id\" = {property}");
        }

        Assert.Equal([OwnerId], await ToldAsync(org.Id, property, Anna, OwnerId));

        await using (var db = _database.CreateContext())
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [property, other]);
        Assert.Equal([Anna, OwnerId], await ToldAsync(org.Id, property, Anna, OwnerId));
    }

    [PostgresFact]
    public async Task Audience_AnAccountInNoTeam_IsStillToldAboutThePropertyItCreated()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _database!.CreateContext())
        {
            // An owner of before the team: an account of the org with no member row.
            db.Users.Add(new User
            {
                Id = "auth0|legacy",
                Email = "legacy@example.com",
                FirstName = "Prima",
                LastName = "Del Team",
                OrgId = org.Id,
                Role = UserRole.PropertyOwner,
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var property = await AddPropertyAsync(org.Id, "Masseria", creator: "auth0|legacy");

        Assert.Equal(["auth0|legacy", OwnerId], await ToldAsync(org.Id, property, responsible: null, creator: "auth0|legacy"));
    }

    // --- The responsibility goes with the access ---------------------------------------------------------

    [PostgresFact]
    public async Task SetAsync_TakingAPropertyAway_ReleasesThePersonInChargeOfThatPropertyOnly()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        var first = await AddPropertyAsync(org.Id, "Primo");
        var second = await AddPropertyAsync(org.Id, "Secondo");
        await using (var db = _database!.CreateContext())
        {
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [first, second]);
            await Access(db).SetResponsibleAsync(org.Id, first, Anna);
            await Access(db).SetResponsibleAsync(org.Id, second, Anna);
        }

        await using (var db = _database.CreateContext())
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [second]);

        Assert.Null(await ResponsibleAsync(first));
        Assert.Equal(Anna, await ResponsibleAsync(second));
    }

    [PostgresFact]
    public async Task RemoveAsync_ReleasesEveryPropertyTheMemberWasInChargeOf()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.PropertyManager);
        var first = await AddPropertyAsync(org.Id, "Primo");
        var second = await AddPropertyAsync(org.Id, "Secondo");
        await using (var db = _database!.CreateContext())
        {
            await Access(db).SetResponsibleAsync(org.Id, first, Anna);
            await Access(db).SetResponsibleAsync(org.Id, second, Anna);
        }

        await using (var db = _database.CreateContext())
            await _kit.Membership(db).RemoveAsync(Anna);

        Assert.Null(await ResponsibleAsync(first));
        Assert.Null(await ResponsibleAsync(second));
    }

    [PostgresFact]
    public async Task Race_APersonPutInChargeWhileTheAccessIsTakenAway_IsNeverLeftInChargeWithoutIt()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        var kept = await AddPropertyAsync(org.Id, "Sempre sua");

        for (var round = 0; round < 8; round++)
        {
            var contested = await AddPropertyAsync(org.Id, $"Contesa {round}");
            await using (var db = _database!.CreateContext())
                await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [kept, contested]);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var appointment = Task.Run(async () =>
            {
                await gate.Task;
                await using var db = _database!.CreateContext();
                try
                {
                    await Access(db).SetResponsibleAsync(org.Id, contested, Anna);
                    return true;
                }
                catch (DomainRuleException)
                {
                    // The access was already gone: the rule refuses it, which is the right answer.
                    return false;
                }
            });
            var revocation = Task.Run(async () =>
            {
                await gate.Task;
                await using var db = _database!.CreateContext();
                await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [kept]);
            });
            gate.SetResult();

            await Task.WhenAll(appointment, revocation);

            // Whatever the order, the access is gone, and so is the name: either the revocation ran second and released it, or the
            // appointment ran second and was refused.
            await using var verify = _database!.CreateContext();
            Assert.False(await verify.PropertyMemberAccesses.IgnoreQueryFilters().AnyAsync(a => a.UserId == Anna && a.PropertyId == contested));
            Assert.Null(await ResponsibleAsync(contested));
        }
    }

    // --- The export of the org on real SQL ---------------------------------------------------------------

    [PostgresFact]
    public async Task Export_AScopeNarrowerThanTheOrg_HoldsOnlyTheSectionsOfTheProperties_OnRealSql()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|foreign-owner");
        await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        var granted = await AddPropertyAsync(org.Id, "Trullo", taxpayer: "RSSMRA80A01H501U");
        var hidden = await AddPropertyAsync(org.Id, "Casa Bianca", taxpayer: "VRDLGU75B12F205X");
        var foreign = await AddPropertyAsync(other.Id, "Villa Altrove", creator: "auth0|foreign-owner", taxpayer: "BNCLRA85C45L219Z");
        await using (var db = _database!.CreateContext())
        {
            foreach (var id in new[] { granted, hidden, foreign })
            {
                var orgOfProperty = id == foreign ? other.Id : org.Id;
                db.PropertyFiscalYears.Add(new PropertyFiscalYear
                {
                    OrgId = orgOfProperty,
                    PropertyId = id,
                    TaxYear = 2026,
                    Regime = StrFiscalRegime.CedolareSecca21,
                });
            }

            db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Anna, PropertyId = granted });
            await db.SaveChangesAsync();
        }

        async Task<JsonElement> ExportAsync(HostScope scope)
        {
            await using var db = _database.CreateContext();
            var service = new GdprService(
                db, Mock.Of<IGuestRepository>(), eraser: null!, Options.Create(new GdprOptions()), _kit.Clock, NullLogger<GdprService>.Instance);
            return JsonDocument.Parse(JsonSerializer.Serialize(await service.ExportOrgFiscalDataAsync(org.Id, scope))).RootElement;
        }

        static HashSet<Guid> Properties(JsonElement export, string section) =>
            export.GetProperty(section).EnumerateArray().Select(row => row.GetProperty("PropertyId").GetGuid()).ToHashSet();

        var whole = await ExportAsync(new HostScope(org.Id));
        var collaborator = await ExportAsync(new HostScope(org.Id, GrantedToUserId: Anna));
        var creator = await ExportAsync(new HostScope(org.Id, OwnerId: OwnerId));

        Assert.True(Properties(whole, "propertyFiscalYears").SetEquals([granted, hidden]));
        Assert.True(Properties(whole, "propertyTaxpayers").SetEquals([granted, hidden]));
        Assert.True(Properties(collaborator, "propertyFiscalYears").SetEquals([granted]));
        Assert.True(Properties(collaborator, "propertyTaxpayers").SetEquals([granted]));
        Assert.True(Properties(creator, "propertyFiscalYears").SetEquals([granted, hidden]));
        Assert.DoesNotContain("BNCLRA85C45L219Z", whole.GetRawText());
        Assert.DoesNotContain("VRDLGU75B12F205X", collaborator.GetRawText());
    }

    // --- The generic save --------------------------------------------------------------------------------

    [PostgresFact]
    public async Task UpdateAsync_FromACopyReadBeforeTheAccessWasTakenAway_DoesNotPutTheOldPersonBack()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (_, member) = await _kit.SeedMemberAsync(org.Id, Anna, OrgRole.Collaborator);
        var property = await AddPropertyAsync(org.Id, "Trullo");
        await using (var db = _database!.CreateContext())
        {
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, [property]);
            await Access(db).SetResponsibleAsync(org.Id, property, Anna);
        }

        await using var staleCopy = _database.CreateContext();
        var copy = await staleCopy.Properties.SingleAsync(p => p.Id == property);
        await using (var db = _database.CreateContext())
            await Access(db).SetAsync(org.Id, member.Id, OwnerId, PropertyScope.Selected, []);

        copy.Description = "Nuova descrizione";
        await new PropertyRepository(staleCopy).UpdateAsync(copy);

        await using var check = _database.CreateContext();
        var stored = await check.Properties.AsNoTracking().SingleAsync(p => p.Id == property);
        Assert.Equal("Nuova descrizione", stored.Description);
        Assert.Null(stored.ResponsibleUserId);
    }
}
