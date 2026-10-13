using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.OrgTeam;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// AM-02 on a real PostgreSQL database: what the in-memory tests cannot prove about the table of the invitations. The
/// unique hash of the token, the partial unique index (one pending invitation per org and email, any number of closed
/// ones), the cascade from the org, the list column of the areas and the tenant filter.
/// </summary>
public class OrgInvitationsSchemaPostgresTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 30, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = _database.CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext(ITenantContext? tenant = null) =>
        tenant is null ? _database!.CreateContext() : new AppDbContext(_database!.CreateOptions(), tenant);

    [PostgresFact]
    public async Task TokenHash_TwoInvitationsWithTheSameHash_AreRejectedByTheDatabase()
    {
        var org = await SeedOrgAsync();
        var hash = OrgInvitationTokens.Hash(OrgInvitationTokens.Generate());
        await using var db = NewContext();
        db.OrgInvitations.Add(Invitation(org, "anna@example.com", hash));
        await db.SaveChangesAsync();

        db.OrgInvitations.Add(Invitation(org, "marco@example.com", hash));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_OrgInvitations_TokenHash", postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task PartialUniqueIndex_TwoPendingInvitationsOfTheSameEmailInTheSameOrg_AreRejected()
    {
        var org = await SeedOrgAsync();
        await using var db = NewContext();
        db.OrgInvitations.Add(Invitation(org, "anna@example.com"));
        await db.SaveChangesAsync();

        db.OrgInvitations.Add(Invitation(org, "anna@example.com"));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_OrgInvitations_OrgId_Email_Pending", postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task PartialUniqueIndex_ClosedInvitationsOfTheSameEmail_DoNotBlockANewPendingOne()
    {
        var org = await SeedOrgAsync();
        await using var db = NewContext();
        foreach (var status in new[] { OrgInvitationStatus.Accepted, OrgInvitationStatus.Revoked, OrgInvitationStatus.Expired, OrgInvitationStatus.Revoked })
        {
            var closed = Invitation(org, "anna@example.com");
            closed.Status = status;
            closed.ClosedAt = Now;
            db.OrgInvitations.Add(closed);
        }

        db.OrgInvitations.Add(Invitation(org, "anna@example.com"));
        await db.SaveChangesAsync();

        Assert.Equal(5, await db.OrgInvitations.IgnoreQueryFilters().CountAsync(i => i.Email == "anna@example.com"));
    }

    [PostgresFact]
    public async Task PartialUniqueIndex_TheSameEmailPendingInTwoOrgs_IsFine()
    {
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        await using var db = NewContext();
        db.OrgInvitations.Add(Invitation(orgA, "anna@example.com"));
        db.OrgInvitations.Add(Invitation(orgB, "anna@example.com"));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.OrgInvitations.IgnoreQueryFilters().CountAsync());
    }

    [PostgresFact]
    public async Task Row_AreasAndEveryField_AreStoredAndReadBack()
    {
        var org = await SeedOrgAsync();
        var invitation = Invitation(org, "anna@example.com");
        invitation.Areas = ["short-rent", "long-rent"];
        invitation.Role = OrgRole.PropertyManager;
        invitation.PropertyScope = PropertyScope.Selected;
        invitation.Language = "en";
        invitation.ReminderSentAt = Now;
        await using (var db = NewContext())
        {
            db.OrgInvitations.Add(invitation);
            await db.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var stored = await verify.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitation.Id);

        Assert.Equal(["short-rent", "long-rent"], stored.Areas);
        Assert.Equal(OrgRole.PropertyManager, stored.Role);
        Assert.Equal(PropertyScope.Selected, stored.PropertyScope);
        Assert.Equal("en", stored.Language);
        Assert.Equal(OrgInvitationStatus.Pending, stored.Status);
        Assert.Equal(Now, stored.ReminderSentAt);
        Assert.Equal(invitation.TokenHash, stored.TokenHash);
    }

    [PostgresFact]
    public async Task ForeignKey_DeletingTheOrg_TakesItsInvitationsWithIt()
    {
        var org = await SeedOrgAsync();
        await using (var db = NewContext())
        {
            db.OrgInvitations.Add(Invitation(org, "anna@example.com"));
            await db.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM \"Orgs\" WHERE \"Id\" = '{org}'";
        await delete.ExecuteNonQueryAsync();

        await using var verify = NewContext();
        Assert.Empty(await verify.OrgInvitations.IgnoreQueryFilters().ToListAsync());
    }

    [PostgresFact]
    public async Task TenantFilter_Invitations_AreScopedToTheCallersOrgAndFailClosed()
    {
        var orgA = await SeedOrgAsync();
        var orgB = await SeedOrgAsync();
        await using (var seed = NewContext())
        {
            seed.OrgInvitations.Add(Invitation(orgA, "a@example.com"));
            seed.OrgInvitations.Add(Invitation(orgB, "b@example.com"));
            await seed.SaveChangesAsync();
        }

        await using var asA = NewContext(new FixedTenantContext(orgA, filterEnabled: true));
        await using var asB = NewContext(new FixedTenantContext(orgB, filterEnabled: true));
        await using var noOrg = NewContext(new FixedTenantContext(null, filterEnabled: true));
        await using var system = NewContext(new FixedTenantContext(null, filterEnabled: false));

        Assert.Equal(["a@example.com"], await asA.OrgInvitations.Select(i => i.Email).ToListAsync());
        Assert.Equal(["b@example.com"], await asB.OrgInvitations.Select(i => i.Email).ToListAsync());
        Assert.Empty(await noOrg.OrgInvitations.ToListAsync());
        Assert.Equal(2, await system.OrgInvitations.CountAsync());
        Assert.Equal(2, await asA.OrgInvitations.IgnoreQueryFilters().CountAsync());
    }

    private async Task<Guid> SeedOrgAsync()
    {
        await using var db = NewContext();
        var org = new OrgEntity
        {
            Name = "Org AM-02",
            Slug = $"am02-{Guid.NewGuid():N}",
            DisplayName = "Org AM-02",
            ContactEmail = "am02@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private static OrgInvitation Invitation(Guid orgId, string email, string? tokenHash = null) => new()
    {
        OrgId = orgId,
        Email = email,
        Name = "Anna Leone",
        Role = OrgRole.Collaborator,
        Areas = ["short-rent"],
        TokenHash = tokenHash ?? OrgInvitationTokens.Hash(OrgInvitationTokens.Generate()),
        Status = OrgInvitationStatus.Pending,
        ExpiresAt = Now.AddDays(7),
        InvitedByUserId = "auth0|owner",
        Language = "it",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private sealed class FixedTenantContext(Guid? orgId, bool filterEnabled) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled { get; } = filterEnabled;
    }
}
