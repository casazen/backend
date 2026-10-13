using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>The supplier's list of, and answers about, the free-looking events of its calendar: isolation and stable codes.</summary>
public class SupplierFreeEventServiceTests
{
    private static readonly Guid SupplierA = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    private static readonly Guid SupplierB = Guid.Parse("b2222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task ListPendingAsync_TwoSuppliers_EachSeesOnlyItsOwnNextDateFirst()
    {
        await using var db = CreateDb();
        await SeedAsync(db, SupplierA, "late", new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(db, SupplierA, "soon", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(db, SupplierA, "done", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), SupplierFreeEventDecision.Busy);
        await SeedAsync(db, SupplierB, "other", new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        var service = CreateService(db);

        var items = await service.ListPendingAsync(SupplierA);

        Assert.Equal(["soon", "late"], items.Select(i => i.Summary));
        Assert.Equal(2, await service.CountPendingAsync(SupplierA));
        Assert.Equal(1, await service.CountPendingAsync(SupplierB));
    }

    [Theory]
    [InlineData("Available", SupplierFreeEventDecision.Available)]
    [InlineData("available", SupplierFreeEventDecision.Available)]
    [InlineData(" Busy ", SupplierFreeEventDecision.Busy)]
    public async Task DecideAsync_ValidAnswer_IsStoredWithItsTimeAndSaysWhetherToFree(string answer, SupplierFreeEventDecision expected)
    {
        await using var db = CreateDb();
        var id = await SeedAsync(db, SupplierA, "e", DateTime.UtcNow);

        var frees = await CreateService(db).DecideAsync(SupplierA, id, answer);

        Assert.Equal(expected == SupplierFreeEventDecision.Available, frees);
        var row = await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync();
        Assert.Equal(expected, row.Decision);
        Assert.NotNull(row.DecidedAt);
    }

    [Fact]
    public async Task DecideAsync_EventOfAnotherSupplier_ReturnsNullAndChangesNothing()
    {
        await using var db = CreateDb();
        var id = await SeedAsync(db, SupplierB, "theirs", DateTime.UtcNow);

        var result = await CreateService(db).DecideAsync(SupplierA, id, "Available");

        Assert.Null(result);
        Assert.Equal(SupplierFreeEventDecision.Pending, (await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync()).Decision);
    }

    [Fact]
    public async Task DecideAsync_UnknownId_ReturnsNull()
    {
        await using var db = CreateDb();

        Assert.Null(await CreateService(db).DecideAsync(SupplierA, Guid.NewGuid(), "Busy"));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("1")]
    [InlineData("Maybe")]
    [InlineData("")]
    [InlineData(null)]
    public async Task DecideAsync_InvalidAnswer_ThrowsStableCodeAndChangesNothing(string? answer)
    {
        await using var db = CreateDb();
        var id = await SeedAsync(db, SupplierA, "e", DateTime.UtcNow);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db).DecideAsync(SupplierA, id, answer));

        Assert.Equal(SupplierFreeEventErrorCodes.InvalidDecision, ex.Code);
        Assert.Equal(SupplierFreeEventDecision.Pending, (await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync()).Decision);
    }

    [Fact]
    public async Task DecideAsync_AlreadyAnswered_ThrowsConflictAndKeepsTheFirstAnswer()
    {
        await using var db = CreateDb();
        var id = await SeedAsync(db, SupplierA, "e", DateTime.UtcNow, SupplierFreeEventDecision.Busy);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => CreateService(db).DecideAsync(SupplierA, id, "Available"));

        Assert.Equal(SupplierFreeEventErrorCodes.AlreadyDecided, ex.Code);
        Assert.Equal(SupplierFreeEventDecision.Busy, (await db.SupplierFreeCalendarEvents.AsNoTracking().SingleAsync()).Decision);
    }

    [Fact]
    public void ErrorCodes_HaveAMessageKeyInBothLanguages()
    {
        var root = FindRepoRoot();
        foreach (var file in new[] { "SharedResources.resx", "SharedResources.en.resx" })
        {
            var text = File.ReadAllText(Path.Combine(root, "Casazen.Web", "Resources", file));
            Assert.Contains($"name=\"{SupplierFreeEventErrorCodes.InvalidDecisionMessageKey}\"", text, StringComparison.Ordinal);
            Assert.Contains($"name=\"{SupplierFreeEventErrorCodes.AlreadyDecidedMessageKey}\"", text, StringComparison.Ordinal);
            Assert.Contains("name=\"FreeEventNotFound\"", text, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Casazen.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Casazen.sln not found");
    }

    private static SupplierFreeEventService CreateService(AppDbContext db) =>
        new(db, NullLogger<SupplierFreeEventService>.Instance);

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedAsync(
        AppDbContext db, Guid orgId, string summary, DateTime next, SupplierFreeEventDecision decision = SupplierFreeEventDecision.Pending)
    {
        var row = new SupplierFreeCalendarEvent
        {
            OrgId = orgId,
            DecisionKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            UidHash = new string('a', 64),
            Summary = summary,
            IsAllDay = true,
            NextStartUtc = next,
            NextEndUtc = next.AddDays(1),
            Decision = decision,
        };
        db.SupplierFreeCalendarEvents.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return row.Id;
    }
}
