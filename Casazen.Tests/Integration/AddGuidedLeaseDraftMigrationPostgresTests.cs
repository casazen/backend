using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// LR-12 on real PostgreSQL, on the schema as it is now (a copy of the migrated template): the columns that the
/// <c>AddGuidedLeaseDraft</c> migration added to <c>LeaseContracts</c> and <c>Parties</c> have the lengths and the types of the model.
/// How the migration moved the drafts that existed (each one to step 5), its revert and the defaults it gave to an old writer ran
/// when the release that carried it went to production: they are not replayed any more.
/// </summary>
public class AddGuidedLeaseDraftMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_NewColumnsHaveTheLengthsAndTypesOfTheModel()
    {
        await using var db = _database!.CreateContext();
        var fingerprint = await db.Database
            .SqlQuery<int>($"""SELECT character_maximum_length AS "Value" FROM information_schema.columns WHERE table_name = 'LeaseContracts' AND column_name = 'ReviewFingerprint'""")
            .SingleAsync();
        var residence = await db.Database
            .SqlQuery<string>($"""SELECT data_type::text AS "Value" FROM information_schema.columns WHERE table_name = 'Parties' AND column_name = 'Residence'""")
            .SingleAsync();
        var draft = await db.Database
            .SqlQuery<string>($"""SELECT data_type::text AS "Value" FROM information_schema.columns WHERE table_name = 'LeaseContracts' AND column_name = 'DraftData'""")
            .SingleAsync();
        Assert.Equal(64, fingerprint);
        Assert.Equal("text", residence);
        Assert.Equal("text", draft);
    }
}
