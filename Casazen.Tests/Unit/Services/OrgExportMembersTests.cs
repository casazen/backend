using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02 (GDPR): the export of the org (<c>GET /api/gdpr/org/export</c>) includes the people who have access: opaque ids, role,
/// status, scope and dates. No names and no emails: the names are on the people page, where whoever manages them already sees
/// them. The export is the holder's (AM-03b, <c>OrgBillingAdmin</c>); here it runs with the whole-org scope of the holder.
/// </summary>
public class OrgExportMembersTests
{
    private readonly OrgInvitationTestKit _kit = new();

    private GdprService Service(AppDbContext db) => new(
        db,
        Mock.Of<IGuestRepository>(),
        eraser: null!,
        Options.Create(new GdprOptions()),
        _kit.Clock,
        NullLogger<GdprService>.Instance);

    [Fact]
    public async Task ExportOrgFiscalDataAsync_IncludesTheMembersWithRoleStatusAndDates_AndNoPersonalData()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|anna", OrgRole.Accountant);
        await _kit.SeedMemberAsync(org.Id, "auth0|bruno", OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);

        await using var db = _kit.NewDb();
        var export = await Service(db).ExportOrgFiscalDataAsync(org.Id, new HostScope(org.Id));

        var json = JsonSerializer.Serialize(export);
        var members = JsonDocument.Parse(json).RootElement.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["auth0|owner", "auth0|anna", "auth0|bruno"], members.Select(m => m.GetProperty("UserId").GetString()));
        Assert.Equal(["Owner", "Accountant", "Collaborator"], members.Select(m => m.GetProperty("Role").GetString()));
        Assert.Equal(["Active", "Active", "Deactivated"], members.Select(m => m.GetProperty("Status").GetString()));
        Assert.Equal("All", members[0].GetProperty("PropertyScope").GetString());
        Assert.NotEqual(JsonValueKind.Null, members[2].GetProperty("DeactivatedAt").ValueKind);

        // Ids, roles and dates only.
        Assert.DoesNotContain("example.com", json);
        Assert.DoesNotContain("Giulia", json);
        Assert.DoesNotContain("Rinaldi", json);
        Assert.DoesNotContain("Email", json);
        Assert.DoesNotContain("FirstName", json);
    }

    [Fact]
    public async Task ExportOrgFiscalDataAsync_TheFiscalDataIsStillThere()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            stored.FiscalCode = "RSSMRA80A01H501U";
            stored.HasPartitaIva = true;
            await db.SaveChangesAsync();
        }

        await using var read = _kit.NewDb();
        var export = await Service(read).ExportOrgFiscalDataAsync(org.Id, new HostScope(org.Id));

        Assert.Equal("RSSMRA80A01H501U", export["fiscalCode"]);
        Assert.True((bool)export["hasPartitaIva"]);
        Assert.True(export.ContainsKey("members"));
        Assert.True(export.ContainsKey("propertyFiscalYears"));
    }

    [Fact]
    public async Task ExportOrgFiscalDataAsync_NeverIncludesThePeopleOfAnotherOrg()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await _kit.SeedMemberAsync(other.Id, "auth0|stranger", OrgRole.Admin);

        await using var db = _kit.NewDb();
        var export = await Service(db).ExportOrgFiscalDataAsync(org.Id, new HostScope(org.Id));

        var json = JsonSerializer.Serialize(export);
        Assert.Contains("auth0|owner-a", json);
        Assert.DoesNotContain("auth0|owner-b", json);
        Assert.DoesNotContain("auth0|stranger", json);
    }
}
