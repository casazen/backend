using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SR-03b: the payment list a page at a time. The pages are the pieces of the list that is not paged (same payments, same order,
/// newest first and the id as the tie-break), a payment is never on two pages nor on none, the total is always told, and the scope
/// is the caller's whatever the page.
/// </summary>
public class PaymentListPagingTests
{
    private static DateTime At(int month, int day, int hour = 10) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The world of the scope tests (one payment per stay on each of two properties and on another org's, plus the ones those
    /// tests add) with 25 more payments on the first property, one every hour on the 14th and the last six at the same instant.
    /// </summary>
    private static async Task<(AppDbContext Db, HostScopeWorld World, int Mine)> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        var booking = await db.Bookings.FirstAsync(b => b.PropertyId == world.Granted.Id);
        var created = Enumerable.Range(0, 25).Select(i => new Payment
        {
            OrgId = world.OrgId,
            BookingId = booking.Id,
            Amount = 10m + i,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = $"tx-{Guid.NewGuid():N}",
            // The last six share an instant: only the id tells them apart.
            CreatedAt = i < 19 ? At(10, 14, i) : At(10, 14, 23),
            UpdatedAt = At(10, 14, 23),
            ProcessedAt = At(10, 14, 12),
        });
        db.Payments.AddRange(created);
        await db.SaveChangesAsync();
        var mine = await db.Payments.CountAsync(p => p.OrgId == world.OrgId && p.Booking.PropertyId == world.Granted.Id);
        return (db, world, mine);
    }

    [Fact]
    public async Task Pages_AreThePiecesOfTheUnpagedList_SameOrder_NeverRepeatingNorSkipping()
    {
        var (db, world, _) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);
        var everything = await service.ListAsync(world.OrgWide, new PaymentListCriteria());

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), page, 10);
            Assert.Equal(everything.Count, result.TotalCount);
            Assert.Equal((page, 10), (result.Page, result.PageSize));
            seen.AddRange(result.Items.Select(p => p.Id));
        }

        Assert.Equal(everything.Select(p => p.Id), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.True(everything.Count > 25);
        // Newest first.
        Assert.Equal(everything.Select(p => p.CreatedAt).OrderByDescending(d => d), everything.Select(p => p.CreatedAt));
    }

    [Fact]
    public async Task TheLastDayTheCalendarHas_IsNoUpperBound_NotAnError()
    {
        var (db, world, _) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);

        var everything = await service.ListAsync(world.OrgWide, new PaymentListCriteria());
        var untilTheEnd = await service.ListAsync(world.OrgWide, new PaymentListCriteria(To: DateOnly.MaxValue));
        var page = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(From: new DateOnly(2000, 1, 1), To: DateOnly.MaxValue), 1, 10);

        Assert.Equal(everything.Select(p => p.Id), untilTheEnd.Select(p => p.Id));
        Assert.Equal(everything.Count, page.TotalCount);
    }

    [Fact]
    public async Task Pages_PaymentsCreatedAtTheSameInstant_AreTiedByTheirId_TheSameOnEveryPage()
    {
        var (db, world, _) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);

        var all = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), 1, 100);
        var tied = all.Items.Where(p => p.CreatedAt == At(10, 14, 23)).Select(p => p.Id).ToList();

        Assert.Equal(6, tied.Count);
        Assert.Equal(tied.Order(), tied);
        // Cut in pages of 4 the ties still come in the same order.
        var cut = new List<Guid>();
        for (var page = 1; page <= 10; page++)
            cut.AddRange((await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), page, 4)).Items.Select(p => p.Id));
        Assert.Equal(all.Items.Select(p => p.Id), cut);
    }

    [Fact]
    public async Task Pages_TheLastPageCountsItself_APagePastTheEndIsEmptyAndStillSaysHowManyThereAre()
    {
        var (db, world, _) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);
        var total = (await service.ListAsync(world.OrgWide, new PaymentListCriteria())).Count;
        var lastPage = (total + 9) / 10;

        var last = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), lastPage, 10);
        var past = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), lastPage + 5, 10);
        var first = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), 1, 10);

        Assert.Equal((total - ((lastPage - 1) * 10), total), (last.Items.Count, last.TotalCount));
        Assert.Empty(past.Items);
        Assert.Equal(total, past.TotalCount);
        Assert.Equal((10, total), (first.Items.Count, first.TotalCount));
    }

    [Fact]
    public async Task Pages_TheNumbersAreBroughtWithinTheLimits()
    {
        var (db, world, _) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);

        var low = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), -3, 0);
        var high = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), int.MaxValue, 100_000);
        var big = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(), 1, 100_000);

        Assert.Equal((1, 1), (low.Page, low.PageSize));
        Assert.Single(low.Items);
        Assert.Equal((PaymentListPage.MaxPage, PaymentListPage.MaxPageSize), (high.Page, high.PageSize));
        Assert.Empty(high.Items);
        Assert.Equal(PaymentListPage.MaxPageSize, big.PageSize);
        Assert.Equal(low.TotalCount, big.TotalCount);
    }

    [Fact]
    public async Task Pages_TheCriteriaApplyBeforeThePage_AndAPropertyOfThePageIsTheOnlyOneCounted()
    {
        var (db, world, mine) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);

        var ofProperty = await service.ListPageAsync(world.OrgWide, new PaymentListCriteria(PropertyId: world.Granted.Id), 2, 10);
        var onTheDay = await service.ListPageAsync(
            world.OrgWide, new PaymentListCriteria(From: new DateOnly(2026, 10, 14), To: new DateOnly(2026, 10, 14)), 1, 100);

        Assert.Equal(mine, ofProperty.TotalCount);
        Assert.All(ofProperty.Items, p => Assert.Equal(world.Granted.Id, p.PropertyId));
        Assert.Equal(25, onTheDay.TotalCount);
        Assert.All(onTheDay.Items, p => Assert.Equal(At(10, 14, 12), p.ProcessedAt));
    }

    [Fact]
    public async Task Pages_ACollaboratorLimitedToSomePropertiesGetsItsPropertysPaymentsOnly_WhateverThePage()
    {
        var (db, world, mine) = await SeedAsync();
        await using var _ = db;
        var service = new PaymentListService(db);

        var seen = new List<PaymentListItem>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await service.ListPageAsync(world.Restricted, new PaymentListCriteria(), page, 10);
            Assert.Equal(mine, result.TotalCount);
            seen.AddRange(result.Items);
        }

        Assert.Equal(mine, seen.Count);
        Assert.All(seen, p => Assert.Equal(world.Granted.Id, p.PropertyId));
        // The other property by id: nothing, and a total of nothing.
        var hidden = await service.ListPageAsync(world.Restricted, new PaymentListCriteria(PropertyId: world.Hidden.Id), 1, 10);
        Assert.Empty(hidden.Items);
        Assert.Equal(0, hidden.TotalCount);
    }
}
