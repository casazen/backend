using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.SupplierReadModelsTestSupport;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04e: <c>GET api/supplier/today?days=</c> lists the jobs from today to today plus <c>days</c> minus one in one call ("today and
/// the next days" of the home); the rest of the answer is what it was. Scenario clock: Thursday 8 October 2026, 12:00 in Rome.
/// </summary>
public class SupplierTodayDaysTests
{
    [Fact]
    public async Task OneDay_IsToday_AsItAlwaysWas()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var today = await JobAsync(s, "2026-10-08T08:00:00Z");
        await JobAsync(s, "2026-10-09T08:00:00Z");

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, 1);
        var byDefault = await Today(s).GetTodayAsync(s.SupplierOrgId);

        Assert.Equal(new[] { today }, result.Jobs.Select(job => job.Id));
        Assert.Equal(1, result.JobsTotal);
        Assert.Equal(1, result.Days);
        Assert.Equal(new DateOnly(2026, 10, 8), result.LastDate);
        Assert.Equal(result.Jobs.Select(job => job.Id), byDefault.Jobs.Select(job => job.Id));
        Assert.Equal(1, byDefault.Days);
    }

    [Fact]
    public async Task SeveralDays_ListTheJobsOfEachOfThem_TheFirstInTimeFirst()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var inTwoDays = await JobAsync(s, "2026-10-10T07:00:00Z"); // Saturday 09:00
        var tomorrowLate = await JobAsync(s, "2026-10-09T13:00:00Z"); // Friday 15:00
        var today = await JobAsync(s, "2026-10-08T12:00:00Z"); // Thursday 14:00
        var tomorrowEarly = await JobAsync(s, "2026-10-09T07:00:00Z"); // Friday 09:00
        await JobAsync(s, "2026-10-11T07:00:00Z"); // Sunday: the fourth day
        await JobAsync(s, "2026-10-07T07:00:00Z"); // yesterday

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, 3);

        Assert.Equal(new[] { today, tomorrowEarly, tomorrowLate, inTwoDays }, result.Jobs.Select(job => job.Id));
        Assert.Equal(3, result.Days);
        Assert.Equal(new DateOnly(2026, 10, 8), result.Date);
        Assert.Equal(new DateOnly(2026, 10, 10), result.LastDate);
    }

    [Fact]
    public async Task ASevenDayWeek_EndsOnTheSeventhDay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var lastDay = await JobAsync(s, "2026-10-14T08:00:00Z"); // Wednesday
        await JobAsync(s, "2026-10-15T08:00:00Z"); // the eighth day

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, SupplierTodayLimits.MaxDays);

        Assert.Equal(new[] { lastDay }, result.Jobs.Select(job => job.Id));
        Assert.Equal(new DateOnly(2026, 10, 14), result.LastDate);
        Assert.Equal(7, SupplierTodayLimits.MaxDays);
    }

    [Theory]
    [InlineData("2026-10-10T21:59:59Z", true)] // 23:59:59 on Saturday in Rome: the last second of the last day (summer time, UTC+2)
    [InlineData("2026-10-10T22:00:00Z", false)] // 00:00 on Sunday
    [InlineData("2026-10-07T21:59:59Z", false)] // 23:59:59 on Wednesday: yesterday
    [InlineData("2026-10-07T22:00:00Z", true)] // 00:00 on Thursday: today
    public async Task Days_AreRomeDays_AtTheEdgesOfTheLastAndTheFirstOne(string start, bool listed)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await JobAsync(s, start);

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, 3);

        Assert.Equal(listed ? 1 : 0, result.Jobs.Count);
    }

    [Fact]
    public async Task Days_AreRomeDays_AlsoWhenTheClocksGoBackInTheMiddleOfThem()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero)); // Saturday 24 October, 12:00 in Rome
        await JobAsync(s, "2026-10-25T00:30:00Z"); // 02:30 on Sunday the 25th, the first of the two passes (summer time)
        await JobAsync(s, "2026-10-26T22:59:59Z"); // 23:59:59 on Monday the 26th in Rome, winter time (UTC+1): the third day
        await JobAsync(s, "2026-10-26T23:00:00Z"); // 00:00 on Tuesday the 27th: the fourth

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, 3);

        Assert.Equal(2, result.Jobs.Count);
        Assert.Equal(new DateOnly(2026, 10, 26), result.LastDate);
    }

    [Fact]
    public async Task Jobs_OfTheOtherStatuses_AreNotListed_AndTheNewRequestsAreAsTheyWere()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await JobAsync(s, "2026-10-09T08:00:00Z");
        var inProgress = await JobAsync(s, "2026-10-09T09:00:00Z", ServiceRequestStatus.InCorso);
        var done = await JobAsync(s, "2026-10-09T10:00:00Z", ServiceRequestStatus.Completato);
        await JobAsync(s, "2026-10-09T11:00:00Z", ServiceRequestStatus.Richiesto); // not a job yet
        await JobAsync(s, "2026-10-09T12:00:00Z", ServiceRequestStatus.Annullato);
        await JobAsync(s, "2026-10-09T13:00:00Z", ServiceRequestStatus.Rifiutato);
        var request = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = Now.AddMinutes(30));

        var week = await Today(s).GetTodayAsync(s.SupplierOrgId, 7);
        var tomorrow = await Today(s).GetTodayAsync(s.SupplierOrgId, 1);

        Assert.Equal(new[] { taken, inProgress, done }, week.Jobs.Select(job => job.Id));
        Assert.Empty(tomorrow.Jobs);
        // The new requests do not depend on the days: the same two, the same total, whatever the number of days.
        Assert.Equal(2, week.NewRequestsTotal);
        Assert.Equal(week.NewRequests.Select(item => item.Id), tomorrow.NewRequests.Select(item => item.Id));
        Assert.Contains(request.Id, week.NewRequests.Select(item => item.Id));
        Assert.Equal(tomorrow.NewRequestsTotal, week.NewRequestsTotal);
    }

    [Fact]
    public async Task Jobs_WithNoDayOfTheirOwn_AreNeverListed()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // A long-rent job with no time of its own and no stay: the day is to agree with the host.
        await s.SeedAsync(
            ServiceRequestStatus.PresoInCarico,
            r =>
            {
                r.TakenAt = r.CreatedAt;
                r.RentalContext = ServiceRequestRentalContext.LongRent;
                r.BookingId = null;
            });

        Assert.Empty((await Today(s).GetTodayAsync(s.SupplierOrgId, 7)).Jobs);
    }

    [Fact]
    public async Task Jobs_OfAShortRentStayWithNoTimeOfTheirOwn_AreOnTheCheckOutDayOfTheStay()
    {
        // The turnover of the stay of the scenario: out on 12 October, the fifth day.
        using var s = await ServiceRequestScenario.CreateAsync();
        var turnover = (await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt)).Id;

        Assert.Equal(new[] { turnover }, (await Today(s).GetTodayAsync(s.SupplierOrgId, 7)).Jobs.Select(job => job.Id));
        Assert.Empty((await Today(s).GetTodayAsync(s.SupplierOrgId, 4)).Jobs);
    }

    [Fact]
    public async Task Jobs_AreCappedAtFifty_TheFirstInTime_AndTheTotalSaysHowManyThereAre()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        for (var i = 0; i < 55; i++)
            await JobAsync(s, Now.AddDays(1).AddMinutes(i).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, 7);
        var jobs = result.Jobs;

        Assert.Equal(50, jobs.Count);
        Assert.Equal(55, result.JobsTotal);
        Assert.Equal(jobs.OrderBy(job => job.Schedule.StartUtc).Select(job => job.Id), jobs.Select(job => job.Id));
        // The 50 kept are the first 50 in time: the five that are left out are the latest ones.
        Assert.Equal(Now.AddDays(1).AddMinutes(49), jobs[^1].Schedule.StartUtc);
    }

    [Fact]
    public async Task Jobs_NeverIncludeTheJobsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var mine = await JobAsync(s, "2026-10-09T08:00:00Z");
        await JobAsync(s, "2026-10-09T08:00:00Z", supplierOrgId: other);

        var result = await Today(s).GetTodayAsync(s.SupplierOrgId, 7);

        Assert.Equal(new[] { mine }, result.Jobs.Select(job => job.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public async Task ADayCountOutsideOneToSeven_IsRefused(int days)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Today(s).GetTodayAsync(s.SupplierOrgId, days));
    }

    [Fact]
    public async Task Jobs_EachOneCarriesItsPhase()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await JobAsync(s, "2026-10-09T08:00:00Z");
        await JobAsync(s, "2026-10-09T09:00:00Z", ServiceRequestStatus.InCorso);

        var jobs = (await Today(s).GetTodayAsync(s.SupplierOrgId, 3)).Jobs;

        Assert.Equal(new[] { "scheduled", "in_progress" }, jobs.Select(job => job.Phase));
    }

    // ─── helpers ───

    private static SupplierTodayService Today(ServiceRequestScenario s) =>
        new(s.Reader, s.Kpis, Mock.Of<ISupplierService>(), s.Catalog, s.Agenda, s.Clock);

    /// <summary>A job of the supplier that starts at <paramref name="startUtc"/>, two hours long, in <paramref name="status"/>.</summary>
    private static async Task<Guid> JobAsync(
        ServiceRequestScenario s,
        string startUtc,
        ServiceRequestStatus status = ServiceRequestStatus.PresoInCarico,
        Guid? supplierOrgId = null)
    {
        var start = Utc(startUtc);
        var request = await s.SeedAsync(
            status,
            r =>
            {
                r.TakenAt = status == ServiceRequestStatus.Richiesto ? null : r.CreatedAt;
                r.ScheduledStartUtc = start;
                r.ScheduledEndUtc = start.AddHours(2);
                if (status == ServiceRequestStatus.Completato)
                    r.CompletedAt = start.AddHours(2);
                if (status == ServiceRequestStatus.Annullato)
                    r.CancelledAt = r.CreatedAt;
            },
            supplierOrgId);
        return request.Id;
    }
}
