using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-14 (A5-30) over HTTP on PostgreSQL: the Alloggiati Web credentials of a property are write-only. The API sets,
/// replaces and removes them and answers only "configured" and the date; no answer ever carries a value, the database
/// holds them encrypted, and TN-3 applies (another org's property 404, a colleague who does not own it 403).
/// </summary>
public class QuesturaCredentialsPostgresIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string OwnerRole = "PropertyOwner";
    private const string Username = "RM000999";
    private const string Password = "Segreta 2026!";
    private const string WsKey = "WSK-8f41c2d7e9";

    private readonly CasazenWebApplicationFactory _factory;

    public QuesturaCredentialsPostgresIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task Put_ValidCredentials_AnswersOnlyTheStatusAndStoresThemEncrypted()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);

        using (var before = await ReadJsonAsync(await client.GetAsync(Url(property.Id))))
        {
            Assert.False(before.RootElement.GetProperty("configured").GetBoolean());
            Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("configuredAt").ValueKind);
        }

        var put = await client.PutAsJsonAsync(Url(property.Id), new { username = Username, password = Password, wsKey = WsKey });
        var putBody = await put.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        AssertOnlyStatus(putBody);

        var get = await client.GetAsync(Url(property.Id));
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertOnlyStatus(getBody);
        using (var status = JsonDocument.Parse(getBody))
        {
            Assert.True(status.RootElement.GetProperty("configured").GetBoolean());
            var configuredAt = status.RootElement.GetProperty("configuredAt").GetDateTime();
            Assert.InRange(configuredAt, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
        }

        // Encrypted at rest; the application (a future Alloggiati client) reads them back through EF.
        var row = await RawRowAsync(property.Id);
        Assert.DoesNotContain(Username, row);
        Assert.DoesNotContain(Password, row);
        Assert.DoesNotContain(WsKey, row);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.PropertyQuesturaCredentials.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.PropertyId == property.Id);
        Assert.Equal((Username, Password, WsKey, property.OrgId), (stored.Username, stored.Password, stored.WsKey, stored.OrgId));
    }

    [PostgresFact]
    public async Task Put_Again_ReplacesTheValuesAndTheDate_DeleteRemovesThem()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Url(property.Id), new { username = "OLD", password = "old-pass", wsKey = "OLD-KEY" })).StatusCode);
        var firstDate = await ConfiguredAtAsync(client, property.Id);

        var replace = await client.PutAsJsonAsync(Url(property.Id), new { username = $"  {Username}  ", password = Password, wsKey = WsKey });
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        AssertOnlyStatus(await replace.Content.ReadAsStringAsync());
        Assert.True(await ConfiguredAtAsync(client, property.Id) >= firstDate);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.PropertyQuesturaCredentials.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.PropertyId == property.Id).ToListAsync();
            var only = Assert.Single(stored);
            // Username and key trimmed, password kept exactly as typed.
            Assert.Equal((Username, Password, WsKey), (only.Username, only.Password, only.WsKey));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(property.Id))).StatusCode);
        using var after = await ReadJsonAsync(await client.GetAsync(Url(property.Id)));
        Assert.False(after.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(property.Id))).StatusCode);
    }

    [PostgresFact]
    public async Task Put_MissingAndTooLongValues_Returns400AndStoresNothing()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, OwnerRole);

        var response = await client.PutAsJsonAsync(Url(property.Id), new
        {
            username = "  ",
            password = new string('x', PropertyQuesturaCredentials.MaxPasswordLength + 1),
            wsKey = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var errors = problem.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty("Username", out _));
            Assert.True(errors.TryGetProperty("Password", out _));
            Assert.True(errors.TryGetProperty("WsKey", out _));
        }

        using var status = await ReadJsonAsync(await client.GetAsync(Url(property.Id)));
        Assert.False(status.RootElement.GetProperty("configured").GetBoolean());
    }

    [PostgresFact]
    public async Task Credentials_PropertyOfAnotherOrg_Return404AndChangeNothing()
    {
        var owner = NewOwner();
        var outsider = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        await _factory.SeedOrgForOwnerAsync(outsider);
        using var ownerClient = _factory.CreateAuthenticatedClient(owner, OwnerRole);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PutAsJsonAsync(Url(property.Id), new { username = Username, password = Password, wsKey = WsKey })).StatusCode);

        using var client = _factory.CreateAuthenticatedClient(outsider, OwnerRole);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url(property.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Url(property.Id), new { username = "X", password = "Y", wsKey = "Z" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(Url(property.Id))).StatusCode);

        using var status = await ReadJsonAsync(await ownerClient.GetAsync(Url(property.Id)));
        Assert.True(status.RootElement.GetProperty("configured").GetBoolean());
    }

    [PostgresFact]
    public async Task Put_ColleagueOfTheSameOrgWhoDoesNotOwnTheProperty_Returns403AndStoresNothing()
    {
        var owner = NewOwner();
        var colleague = NewOwner();
        var property = await _factory.SeedPropertyAsync(owner);
        await AddUserToOrgAsync(owner, colleague);
        using var client = _factory.CreateAuthenticatedClient(colleague, OwnerRole);

        var response = await client.PutAsJsonAsync(Url(property.Id), new { username = Username, password = Password, wsKey = WsKey });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<IQuesturaCredentialsService>();
        Assert.False((await credentials.GetStatusAsync(property.Id)).Configured);
    }

    private static string Url(Guid propertyId) => $"/api/properties/{propertyId}/questura-credentials";

    /// <summary>A credentials answer: "configured" and the date, never a value nor any other field.</summary>
    private static void AssertOnlyStatus(string body)
    {
        Assert.DoesNotContain(Username, body);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain(WsKey, body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            new[] { "configured", "configuredAt" },
            doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    private static async Task<DateTime> ConfiguredAtAsync(HttpClient client, Guid propertyId)
    {
        using var status = await ReadJsonAsync(await client.GetAsync(Url(propertyId)));
        return status.RootElement.GetProperty("configuredAt").GetDateTime();
    }

    /// <summary>The row as the database stores it (context without converters).</summary>
    private async Task<string> RawRowAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        await using var stored = new AppDbContext(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>());
        var row = await stored.Database.SqlQuery<string>(
            $"""SELECT row_to_json(t)::text AS "Value" FROM "PropertyQuesturaCredentials" AS t WHERE t."PropertyId" = {propertyId}""")
            .SingleAsync();
        Assert.Contains(EncryptedColumns.ProtectedPayloadPrefix, row);
        return row;
    }

    private async Task AddUserToOrgAsync(string ownerId, string userId)
    {
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = userId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = org.Id,
            Role = UserRole.PropertyOwner,
            IsActive = true,
        };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, org.Id, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static string NewOwner() => $"auth0|co14-{Guid.NewGuid():N}";
}
