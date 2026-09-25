using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-01 data migration (<see cref="NormalizeCinCodes"/>) on real PostgreSQL: existing CINs end up exactly as
/// <see cref="CinFormat.Normalize"/> would store them, and CINs that are still not in the official format are
/// kept (their computed status is "invalid"), never deleted.
/// </summary>
public class NormalizeCinCodesPostgresIntegrationTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    private static readonly string?[] StoredCins =
    [
        "IT-058091-C2-7G5FFZDZ",
        "it 015146 a1 2holv2mz",
        "IT048017\u00A0B4\u20132742QNBZ",
        "\tIT027042C2IT3TRNHJ\n",
        "IT058091A1K2XKTJ9H",
        "IT-12345-0123456789",
        "IT058091C2.7G5FFZDZ",
        "  ",
        "-",
        null,
    ];

    [PostgresFact]
    public async Task NormalizeSql_ExistingCins_MatchCinFormatNormalizeAndKeepsInvalidOnes()
    {
        var inputs = StoredCins;
        var ids = await SeedPropertiesAsync(inputs);

        await using (var migrate = NewContext())
            await migrate.Database.ExecuteSqlRawAsync(NormalizeCinCodes.NormalizeSql);

        await using var db = NewContext();
        var stored = await db.Properties.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.CinCode);

        Assert.Equal(ids.Count, stored.Count);
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(CinFormat.Normalize(inputs[i]), stored[ids[i]]);

        Assert.Equal("IT058091C27G5FFZDZ", stored[ids[0]]);
        Assert.Equal("IT123450123456789", stored[ids[5]]);
        Assert.Equal(CinStatus.Invalid, CinFormat.GetStatus(stored[ids[5]]));
        Assert.Equal(CinStatus.Invalid, CinFormat.GetStatus(stored[ids[6]]));
        Assert.Null(stored[ids[7]]);
    }

    private async Task<List<Guid>> SeedPropertiesAsync(IReadOnlyList<string?> cins)
    {
        await using var db = NewContext();
        var org = new Casazen.Core.Entities.Org { Name = "Org CIN", Slug = $"org-cin-{Guid.NewGuid():N}", DisplayName = "Org CIN" };
        db.Orgs.Add(org);

        var properties = cins.Select((cin, index) => new Property
        {
            OwnerId = "auth0|cin-owner",
            OrgId = org.Id,
            Name = $"Casa {index}",
            Address = $"Via Roma {index}",
            City = "Roma",
            CinCode = cin, // raw value: the DbContext does not normalize, like rows saved before CO-01
        }).ToList();
        db.Properties.AddRange(properties);
        await db.SaveChangesAsync();
        return properties.Select(p => p.Id).ToList();
    }
}
