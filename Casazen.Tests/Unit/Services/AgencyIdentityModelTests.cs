using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class AgencyIdentityModelTests
{
    [Fact]
    public void AgencyIdentity_MigrationIsDiscoverableAndMatchesTheModel()
    {
        // Metadata checks only: no database connection is opened.
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_check;Username=postgres;Password=unused")
            .Options);

        Assert.Contains("20261116230000_AddAgencyIdentity", db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
