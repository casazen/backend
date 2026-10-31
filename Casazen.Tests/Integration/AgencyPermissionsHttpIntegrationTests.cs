using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DEB-01 over the real pipeline: in an org on the agency plan the plan is no longer enough. A collaborator gets 403 on the full IBAN of an
/// owner and on the wire batches, the owner reads both, and the other roles reach what <c>OrgRoleCatalog.AgencyPermissionsOf</c> gives
/// them. The members are added through the real service (the seeded roles: PostgreSQL in CI, the seed of the model on the in-memory
/// fallback), with no role in their token.
/// </summary>
public class AgencyPermissionsHttpIntegrationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Iban = "IT60X0542811101000000123456";

    private sealed record Agency(string OwnerId, Guid OrgId, Guid PropertyOwnerId, Guid BatchId);

    [Fact]
    public async Task Collaborator_Gets403OnTheFullIbanAndOnTheWireBatches_TheOwnerGets200()
    {
        var agency = await SeedAgencyAsync();
        var collaborator = await OrgTeamHttp.AddMemberAsync(factory, agency.OrgId, OrgRole.Collaborator, ["short-rent"]);
        using var asCollaborator = factory.CreateAuthenticatedClient(collaborator);
        using var asOwner = factory.CreateAuthenticatedClient(agency.OwnerId, roles: "PropertyOwner");

        Assert.Equal(HttpStatusCode.Forbidden, (await asCollaborator.GetAsync($"/api/owners/{agency.PropertyOwnerId}/iban")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asCollaborator.GetAsync($"/api/wire-transfers/batch/{agency.BatchId}/file")).StatusCode);
        var createBatch = await asCollaborator.PostAsJsonAsync(
            "/api/wire-transfers/batch", new { statementIds = new[] { Guid.NewGuid() }, debitAccount = Iban });
        Assert.Equal(HttpStatusCode.Forbidden, createBatch.StatusCode);

        var iban = await asOwner.GetAsync($"/api/owners/{agency.PropertyOwnerId}/iban");
        Assert.Equal(HttpStatusCode.OK, iban.StatusCode);
        Assert.Equal(Iban, (await iban.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("iban").GetString());
        var file = await asOwner.GetAsync($"/api/wire-transfers/batch/{agency.BatchId}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("<Document/>", await file.Content.ReadAsStringAsync());
    }

    /// <summary>The rest of the matrix: the administrator holds everything, the property manager the owner list only, the accountant nothing.</summary>
    [Theory]
    [InlineData(OrgRole.Admin, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData(OrgRole.PropertyManager, HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData(OrgRole.Accountant, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public async Task TeamRole_ReachesWhatItsAgencyPermissionsAllow(
        OrgRole role,
        HttpStatusCode ownerList,
        HttpStatusCode fullIban,
        HttpStatusCode wireFile,
        HttpStatusCode statements)
    {
        var agency = await SeedAgencyAsync();
        var member = await OrgTeamHttp.AddMemberAsync(factory, agency.OrgId, role, ["short-rent"]);
        using var client = factory.CreateAuthenticatedClient(member);

        Assert.Equal(ownerList, (await client.GetAsync("/api/owners")).StatusCode);
        Assert.Equal(fullIban, (await client.GetAsync($"/api/owners/{agency.PropertyOwnerId}/iban")).StatusCode);
        Assert.Equal(wireFile, (await client.GetAsync($"/api/wire-transfers/batch/{agency.BatchId}/file")).StatusCode);
        Assert.Equal(statements, (await client.GetAsync("/api/statements")).StatusCode);
    }

    /// <summary>An org on the agency plan with its owner (member row and memberships), one property owner with an IBAN and one wire batch.</summary>
    private async Task<Agency> SeedAgencyAsync()
    {
        var (ownerId, orgId) = await OrgTeamHttp.SeedOwnerWithTeamRowsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.PlanTier = PlanTier.Agency;
        var propertyOwner = new PropertyOwner
        {
            OrgId = orgId,
            FirstName = "Maria",
            LastName = "Proprietaria",
            Email = $"{Guid.NewGuid():N}@example.com",
            Iban = Iban,
            CreatedAt = DateTime.UtcNow,
        };
        var batch = new WireBatch
        {
            OrgId = orgId,
            Currency = "EUR",
            DebitAccount = Iban,
            TotalAmount = 100m,
            TransferCount = 1,
            XmlContent = "<Document/>",
        };
        db.PropertyOwners.Add(propertyOwner);
        db.WireBatches.Add(batch);
        await db.SaveChangesAsync();

        return new Agency(ownerId, orgId, propertyOwner.Id, batch.Id);
    }
}
