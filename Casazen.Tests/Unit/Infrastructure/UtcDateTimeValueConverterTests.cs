using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// FD-06: every DateTime/DateTime? column of the model goes through <see cref="UtcDateTimeValueConverter"/>.
/// </summary>
public class UtcDateTimeValueConverterTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"utc-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public void Model_AllDateTimeProperties_UseUtcConverter()
    {
        using var db = NewDb();

        var dateTimeProperties = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(dateTimeProperties);
        var missing = dateTimeProperties
            .Where(p => p.GetValueConverter() is not UtcDateTimeValueConverter)
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}")
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void ConvertToProvider_UnspecifiedKind_ReturnsSameWallClockAsUtc()
    {
        var converter = new UtcDateTimeValueConverter();

        var stored = (DateTime)converter.ConvertToProvider(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified))!;

        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), stored);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
    }

    [Fact]
    public void ConvertFromProvider_AnyKind_ReturnsUtcKind()
    {
        var converter = new UtcDateTimeValueConverter();

        var read = (DateTime)converter.ConvertFromProvider(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Unspecified))!;

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), read);
    }

    [Fact]
    public async Task SaveChanges_UnspecifiedKind_ReadsBackAsUtc()
    {
        var dbName = $"utc-{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        var id = Guid.NewGuid();
        await using (var db = new AppDbContext(options))
        {
            db.TouristTaxRates.Add(new TouristTaxRate
            {
                Id = id,
                City = "Milano",
                EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                EffectiveTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Unspecified),
            });
            await db.SaveChangesAsync();
        }

        await using var read = new AppDbContext(options);
        var rate = await read.TouristTaxRates.AsNoTracking().SingleAsync(r => r.Id == id);

        Assert.Equal(DateTimeKind.Utc, rate.EffectiveFrom.Kind);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), rate.EffectiveFrom);
        Assert.Equal(DateTimeKind.Utc, rate.EffectiveTo!.Value.Kind);
    }
}
