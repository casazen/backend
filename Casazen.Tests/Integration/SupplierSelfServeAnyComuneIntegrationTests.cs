using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Supplier self-serve registration in any comune of the official ISTAT list (default, <c>Suppliers:AnyComune</c>): no pilot
/// configured, the comune only has to be an active one of the list. The pilot-restricted behaviour is in
/// <see cref="SupplierRegistrationIntegrationTests"/>.
/// </summary>
public class SupplierSelfServeAnyComuneIntegrationTests(SupplierSelfServeAnyComuneIntegrationTests.AnyComuneFactory factory)
    : IClassFixture<SupplierSelfServeAnyComuneIntegrationTests.AnyComuneFactory>
{
    // Milano in the official sample of the list (ISTAT 015146, cadastral F205): not a pilot, not configured anywhere.
    private const string ComuneIstat = "015146";
    private const string ComuneCadastral = "F205";

    [PostgresFact]
    public async Task RegistrationOptions_NoPilotConfigured_SelfServeIsEnabledOnceTheListIsImported()
    {
        using var anonymous = factory.CreateClient();

        var body = await (await anonymous.GetAsync("/api/suppliers/registration-options")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("selfServeEnabled").GetBoolean());
        Assert.Empty(body.GetProperty("pilotComuni").EnumerateArray());
    }

    [PostgresFact]
    public async Task Register_AnonymousSelfServeInAnyListedComune_Returns201AndStoresTheIstatCode()
    {
        var email = $"any-{Guid.NewGuid():N}@test.com";
        using var anonymous = factory.CreateClient();

        var response = await RegisterAsync(anonymous, email, ComuneIstat);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(p => p.Email == email);
        Assert.Equal($"[\"{ComuneIstat}\"]", profile.ComuniJson);
        Assert.Equal($"[\"{ComuneIstat}\"]", profile.ComuneIstatCodesJson);
    }

    [PostgresFact]
    public async Task Register_AnyComuneWithCadastralCode_IsStoredByItsIstatCode()
    {
        var email = $"any-cad-{Guid.NewGuid():N}@test.com";
        using var anonymous = factory.CreateClient();

        var response = await RegisterAsync(anonymous, email, ComuneCadastral);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.SupplierProfiles.SingleAsync(p => p.Email == email);
        Assert.Equal($"[\"{ComuneIstat}\"]", profile.ComuniJson);
    }

    [PostgresFact]
    public async Task Register_ComuneNotInTheOfficialList_Returns422AndCreatesNothing()
    {
        var email = $"unknown-{Guid.NewGuid():N}@test.com";
        using var anonymous = factory.CreateClient();

        var response = await RegisterAsync(anonymous, email, "999999");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SupplierProfiles.AnyAsync(p => p.Email == email));
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string comuneCode) =>
        client.PostAsJsonAsync("/api/suppliers/register", new
        {
            email,
            legalName = "Pulizie Test Srl",
            phone = "+39 06 123456",
            comuneCode,
        });

    /// <summary>Default settings: no <c>Suppliers:PilotComuni</c>, <c>Suppliers:AnyComune</c> not set (open).</summary>
    public class AnyComuneFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>()));
        }
    }
}
