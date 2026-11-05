using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proofs of the lock code status, <c>GET api/properties/access-codes</c> (NF-50, E03/F01/US03/T01): the host sees which of its
/// properties have a code, and never the code (privacy, ADR-013); it sees only the properties its <see cref="HostScope"/> reaches
/// (permission, AM-03).
/// </summary>
public class AccessCodeStatusProofTests
{
    private const string TestCode = "4821#9035";

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }

    /// <summary>
    /// Privacy rule: the status carries no code and no ciphertext, and the query does not read the encrypted column, so nothing is
    /// decrypted. The statement the service runs is read as the SQL the Npgsql provider writes for it (no database needed): it reads the
    /// date of the code and the column of the code is not in it. The answer is read from a database that holds a code: neither the
    /// code nor a payload is in what goes out, and the rows have no member that could hold one.
    /// </summary>
    [Fact]
    public async Task AccessCodeStatus_NeverReturnsTheCode_Test()
    {
        var orgId = Guid.NewGuid();

        await using (var npgsql = NewNpgsqlContext(orgId))
        {
            var service = new AccessCodeService(npgsql, NullLogger<AccessCodeService>.Instance);
            var scope = new HostScope(orgId);

            var sql = service.StatusRows(scope, 0, 50).ToQueryString();

            Assert.Contains("\"PropertyAccessCodes\"", sql);
            Assert.Contains("\"UpdatedAt\"", sql);
            Assert.DoesNotContain("EncryptedCode", sql);

            // The count of the page does not touch the table of the codes at all.
            Assert.DoesNotContain("PropertyAccessCodes", service.ReachableProperties(scope).ToQueryString());
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccessCodeStatusPrivacy_{Guid.NewGuid()}")
            .Options;
        var dataProtection = new EphemeralDataProtectionProvider();
        var withCode = NewProperty(orgId, "Trullo");
        var withoutCode = NewProperty(orgId, "Alba");
        var setAt = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

        await using (var writer = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection))
        {
            writer.Orgs.Add(NewOrg(orgId));
            writer.Properties.AddRange(withCode, withoutCode);
            writer.PropertyAccessCodes.Add(NewCode(orgId, withCode.Id, setAt));
            await writer.SaveChangesAsync();
        }

        await using var reader = new AppDbContext(options, new FixedTenantContext(orgId), dataProtection);
        var page = await new AccessCodeService(reader, NullLogger<AccessCodeService>.Instance).GetStatusAsync(new HostScope(orgId), 1, 50);

        Assert.Equal(2, page.TotalCount);
        var trullo = Assert.Single(page.Items, item => item.PropertyId == withCode.Id);
        Assert.True(trullo.AccessCodeSet);
        Assert.Equal(setAt, trullo.UpdatedAt);
        var alba = Assert.Single(page.Items, item => item.PropertyId == withoutCode.Id);
        Assert.False(alba.AccessCodeSet);
        Assert.Null(alba.UpdatedAt);

        // What goes out, the page and the rows the endpoint sends, has neither the code nor a payload of the encryption.
        var sent = page.Items.Select(AccessCodeStatusDto.From).ToList();
        foreach (var json in new[] { JsonSerializer.Serialize(page, JsonSerializerOptions.Web), JsonSerializer.Serialize(sent, JsonSerializerOptions.Web) })
        {
            Assert.DoesNotContain(TestCode, json);
            Assert.DoesNotContain(EncryptedColumns.ProtectedPayloadPrefix, json);
        }

        // And there is no member that could hold one: the only texts of a row are the name and the city of the property.
        foreach (var type in new[] { typeof(AccessCodeStatusItem), typeof(AccessCodeStatusDto) })
        {
            var texts = type.GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order().ToList();
            Assert.Equal(new[] { "City", "Name" }, texts);
        }
    }

    /// <summary>
    /// Permission rule: rows and counts include only the properties the <see cref="HostScope"/> of the caller reaches, in SQL. A
    /// collaborator «Solo alcuni» gets the property it was given and not the other one of its org, whatever codes they have; the whole
    /// org is the org's properties; another org's property is in no scope of this one.
    /// </summary>
    [Fact]
    public async Task AccessCodeStatus_HostScopeNarrowsToReachableProperties_Test()
    {
        await using var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var grantedSetAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        var hiddenSetAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var foreignSetAt = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

        // A third property of the org that has no code, and a code for each of the other three: every property but that one has one.
        var withoutCode = HostScopeScenario.NewProperty(world.OrgId, world.OwnerUserId, "Alba");
        db.Properties.Add(withoutCode);
        db.PropertyAccessCodes.AddRange(
            NewCode(world.OrgId, world.Granted.Id, grantedSetAt),
            NewCode(world.OrgId, world.Hidden.Id, hiddenSetAt),
            NewCode(world.OtherOrgId, world.OtherOrgProperty.Id, foreignSetAt));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new AccessCodeService(db, NullLogger<AccessCodeService>.Instance);

        // The collaborator «Solo alcuni»: the property it was given, with its own date, and the count says one.
        var restricted = await service.GetStatusAsync(world.Restricted, 1, 50);
        var given = Assert.Single(restricted.Items);
        Assert.Equal(world.Granted.Id, given.PropertyId);
        Assert.True(given.AccessCodeSet);
        Assert.Equal(grantedSetAt, given.UpdatedAt);
        Assert.Equal(1, restricted.TotalCount);

        // A collaborator that was given nothing gets nothing: the scope is never widened to the org.
        var nothing = await service.GetStatusAsync(new HostScope(world.OrgId, GrantedToUserId: "auth0|nessuno"), 1, 50);
        Assert.Empty(nothing.Items);
        Assert.Equal(0, nothing.TotalCount);

        // The whole org: its three properties by name, Alba with no code and no date, never the property of the other org.
        var orgWide = await service.GetStatusAsync(world.OrgWide, 1, 50);
        Assert.Equal(new[] { "Alba", "Casa Bianca", "Trullo" }, orgWide.Items.Select(item => item.Name));
        Assert.Equal(3, orgWide.TotalCount);
        Assert.Equal(new[] { false, true, true }, orgWide.Items.Select(item => item.AccessCodeSet));
        Assert.Equal(new DateTime?[] { null, hiddenSetAt, grantedSetAt }, orgWide.Items.Select(item => item.UpdatedAt));
        Assert.DoesNotContain(orgWide.Items, item => item.PropertyId == world.OtherOrgProperty.Id);

        // The total counts the properties of the scope also when the page is full and the count has to be asked.
        var first = await service.GetStatusAsync(world.OrgWide, 1, 2);
        Assert.Equal(new[] { "Alba", "Casa Bianca" }, first.Items.Select(item => item.Name));
        Assert.Equal(3, first.TotalCount);
        var second = await service.GetStatusAsync(world.OrgWide, 2, 2);
        Assert.Equal(new[] { "Trullo" }, second.Items.Select(item => item.Name));
        Assert.Equal(3, second.TotalCount);

        // The other org sees its own property and none of this one's.
        var other = await service.GetStatusAsync(new HostScope(world.OtherOrgId), 1, 50);
        var foreign = Assert.Single(other.Items);
        Assert.Equal(world.OtherOrgProperty.Id, foreign.PropertyId);
        Assert.Equal(foreignSetAt, foreign.UpdatedAt);
        Assert.Equal(1, other.TotalCount);

        // The narrowing is part of the statement (an EXISTS on the grants of the collaborator), not a filter on rows already loaded.
        await using var npgsql = NewNpgsqlContext(world.OrgId);
        var sqlService = new AccessCodeService(npgsql, NullLogger<AccessCodeService>.Instance);
        Assert.Contains("\"PropertyMemberAccesses\"", sqlService.StatusRows(world.Restricted, 0, 50).ToQueryString());
        Assert.Contains("\"PropertyMemberAccesses\"", sqlService.ReachableProperties(world.Restricted).ToQueryString());
        Assert.DoesNotContain("PropertyMemberAccesses", sqlService.StatusRows(world.OrgWide, 0, 50).ToQueryString());
    }

    private static AppDbContext NewNpgsqlContext(Guid orgId) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options,
            new FixedTenantContext(orgId),
            new EphemeralDataProtectionProvider());

    private static PropertyAccessCode NewCode(Guid orgId, Guid propertyId, DateTime updatedAt) => new()
    {
        OrgId = orgId,
        PropertyId = propertyId,
        EncryptedCode = TestCode,
        UpdatedAt = updatedAt,
    };

    private static OrgEntity NewOrg(Guid orgId) => new()
    {
        Id = orgId,
        Name = "Test Org",
        Slug = $"test-org-{orgId:N}",
        DisplayName = "Test Org",
        ContactEmail = "test@example.com",
        PlanTier = PlanTier.Pro,
    };

    private static Property NewProperty(Guid orgId, string name) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = orgId,
        OwnerId = "test-user",
        Name = name,
        Address = "Test Address",
        City = "Milan",
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 100,
    };
}
