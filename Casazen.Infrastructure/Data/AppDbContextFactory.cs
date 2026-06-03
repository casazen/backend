using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Data;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Must match UserSecretsId in Casazen.Web.csproj.</summary>
    private const string UserSecretsId = "casazen-backend-local";

    public AppDbContext CreateDbContext(string[] args) =>
        new(CreateOptions(BuildConfiguration()));

    private static DbContextOptions<AppDbContext> CreateOptions(IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration)
            ?? throw new InvalidOperationException(
                "No database connection string found. Run scripts/setup-supabase.ps1 or set secrets/supabase.local.env, then scripts/migrate.ps1.");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(
            connectionString,
            npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"));
        return optionsBuilder.Options;
    }

    private static IConfiguration BuildConfiguration()
    {
        var webPath = ResolveWebProjectPath();

        return new ConfigurationBuilder()
            .SetBasePath(webPath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets(UserSecretsId)
            .AddEnvironmentVariables()
            .Build();
    }

    private static string? ResolveConnectionString(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")))
        {
            return Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        }

        var target = Environment.GetEnvironmentVariable("CASAZEN_MIGRATION_TARGET") ?? "test";
        var named = target.Equals("prod", StringComparison.OrdinalIgnoreCase)
            ? "SupabaseProd"
            : "SupabaseTest";

        return configuration.GetConnectionString(named)
            ?? configuration.GetConnectionString("DefaultConnection");
    }

    private static string ResolveWebProjectPath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "..", "Casazen.Web"),
            Path.Combine(Directory.GetCurrentDirectory(), "Casazen.Web"),
        };

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(Path.Combine(full, "Casazen.Web.csproj")))
            {
                return full;
            }
        }

        throw new InvalidOperationException(
            "Could not locate Casazen.Web for design-time configuration. Run EF commands from the backend repo root.");
    }
}
