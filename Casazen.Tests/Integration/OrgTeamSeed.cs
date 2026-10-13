using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// The seed data of the model (the contexts, the roles and their permissions) for the HTTP tests of the org team that run on the
/// in-memory fallback of the host. On PostgreSQL the migrations seed it. One copy for the tests of AM-02b: it writes only the
/// <b>columns</b> of the seed rows (<c>FindProperty</c>) and never the navigation lists a seed row also carries, because those are
/// objects shared with the model itself, and attaching them would change the seed of every other host of the process («another
/// instance with the same key is already being tracked»).
/// </summary>
internal static class OrgTeamSeed
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureRolesAsync(CasazenWebApplicationFactory factory)
    {
        if (factory.UsesPostgreSql)
            return;

        await Gate.WaitAsync();
        try
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Roles.AnyAsync())
                return;

            var model = ((IInfrastructure<IServiceProvider>)db).Instance.GetRequiredService<IDesignTimeModel>().Model;
            foreach (var entityType in model.GetEntityTypes().Where(e => e.ClrType.Name is "AppContext" or "Role" or "RolePermission"))
            {
                foreach (var row in entityType.GetSeedData())
                {
                    var entity = Activator.CreateInstance(entityType.ClrType)!;
                    foreach (var (name, value) in row.Where(column => entityType.FindProperty(column.Key) is not null))
                        entityType.ClrType.GetProperty(name)!.SetValue(entity, value);
                    db.Add(entity);
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
