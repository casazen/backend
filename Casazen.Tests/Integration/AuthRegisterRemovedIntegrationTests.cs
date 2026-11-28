using System.Net;
using System.Text;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class AuthRegisterRemovedIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Email = "pl08-ghost@example.com";

    private readonly CasazenWebApplicationFactory _factory;

    public AuthRegisterRemovedIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_AnonymousRequest_Returns404AndCreatesNoUser()
    {
        using var client = _factory.CreateClient();
        using var content = new StringContent(
            $$"""{ "email": "{{Email}}", "firstName": "Mario", "lastName": "Rossi", "password": "secret" }""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/auth/register", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == Email));
    }
}
