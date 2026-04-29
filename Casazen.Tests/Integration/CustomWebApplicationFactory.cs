using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Casazen.Tests.Integration;

/// <summary>
/// Custom WebApplicationFactory for integration tests that:
/// - Removes the SQL Server connection string to avoid LocalDB dependency
/// - Forces the app to use InMemoryDatabase (already configured in Program.cs)
/// - Skips Hangfire initialization (requires SQL Server connection string)
/// </summary>
public class CustomWebApplicationFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set environment before configuration
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            // Clear existing configuration sources to ensure clean state
            config.Sources.Clear();

            // Add minimal configuration without connection string
            // This will cause Program.cs to:
            // 1. Use InMemoryDatabase instead of SQL Server (line 27)
            // 2. Skip Hangfire initialization (line 31)
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = string.Empty,
                ["Auth0:Domain"] = "test-domain.auth0.com",
                ["Auth0:Audience"] = "test-audience",
                ["Logging:LogLevel:Default"] = "Warning"
            });
        });
    }
}
