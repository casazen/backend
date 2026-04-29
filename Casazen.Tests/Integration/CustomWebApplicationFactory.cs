using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        // Set environment to Test
        builder.UseEnvironment("Test");

        // Override configuration to remove connection string
        builder.ConfigureAppConfiguration((context, config) =>
        {
            // Add override configuration with highest priority to nullify connection string
            // This ensures Hangfire is not initialized and InMemoryDatabase is used
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = null
            }!);
        });

        // Also override services to ensure DbContext uses InMemory
        builder.ConfigureServices(services =>
        {
            // Remove existing DbContext registration
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));

            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            // Add InMemory DbContext
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid());
            });
        });
    }
}
