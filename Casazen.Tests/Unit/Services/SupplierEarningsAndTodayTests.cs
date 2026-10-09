using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04: what the supplier's home shows (<c>GET api/supplier/today</c>): the jobs of the day, the new requests by deadline, the
/// month's earnings from the final amounts of the completed jobs (labelled as an estimate until payments exist), what is still
/// to be collected and the average time to answer; and the checklist of the first steps (<c>GET api/supplier/checklist</c>).
/// Dates are Europe/Rome days: the scenario clock is Thursday 8 October 2026, 12:00 in Rome.
/// </summary>
public class SupplierEarningsAndTodayTests
{
    // ─── Earnings ───

    [Fact]
    public async Task GetEarningsSummaryAsync_NothingYet_IsZeroForTheMonthOfTheClockAndStillAnEstimate()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var summary = await Kpis(s).GetEarningsSummaryAsync(s.SupplierOrgId);

        Assert.Equal(new DateOnly(2026, 10, 1), summary.MonthFrom);
        Assert.Equal(new DateOnly(2026, 10, 31), summary.MonthTo);
        Assert.Equal(0, summary.MonthAmountCents);
        Assert.Equal(0, summary.MonthJobs);
        Assert.Equal(0, summary.ToCollectAmountCents);
        Assert.Equal(0, summary.ToCollectJobs);
        Assert.Null(summary.AverageResponseMinutes);
        Assert.True(summary.IsEstimate);
    }

    [Fact]
    public async Task GetEarningsSummaryAsync_SumsTheFinalAmountsOfTheJobsCompletedOrPaidInTheMonth()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Completato, r => Completed(r, "2026-10-03T09:00:00Z", 8000));
        await s.SeedAsync(ServiceRequestStatus.Pagato, r => Completed(r, "2026-10-05T09:00:00Z", 5000));
        // Completed in September: it is no earning of October, but it is still to be collected.
        await s.SeedAsync(ServiceRequestStatus.Completato, r => Completed(r, "2026-09-20T09:00:00Z", 6000));
        // Completed without a final amount (an older request): a job that adds nothing.
        await s.SeedAsync(ServiceRequestStatus.Completato, r => Completed(r, "2026-10-06T09:00:00Z", null));
        // Not earnings: new, taken, rejected, cancelled, and the job of another supplier.
        await s.SeedAsync(ServiceRequestStatus.Richiesto);
        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);
        await s.SeedAsync(ServiceRequestStatus.Rifiutato);
        await s.SeedAsync(ServiceRequestStatus.Annullato, r => r.FinalAmountCents = 9999);
        await s.SeedAsync(ServiceRequestStatus.Completato, r => Completed(r, "2026-10-03T09:00:00Z", 7777), supplierOrgId: other);

        var summary = await Kpis(s).GetEarningsSummaryAsync(s.SupplierOrgId);

        Assert.Equal(13000, summary.MonthAmountCents);
        Assert.Equal(3, summary.MonthJobs);
        Assert.Equal(14000, summary.ToCollectAmountCents);
        Assert.Equal(3, summary.ToCollectJobs);
    }

    [Theory]
    [InlineData("2026-09-30T21:59:59Z", false)] // 23:59:59 on 30 September in Rome
    [InlineData("2026-09-30T22:00:00Z", true)] // 00:00 on 1 October in Rome
    [InlineData("2026-10-31T22:59:59Z", true)] // 23:59:59 on 31 October in Rome (summer time ended on the 25th: UTC+1)
    [InlineData("2026-10-31T23:00:00Z", false)] // 00:00 on 1 November in Rome
    public async Task GetEarningsSummaryAsync_TheMonthIsMadeOfRomeDays(string completedAt, bool inOctober)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedAsync(ServiceRequestStatus.Pagato, r => Completed(r, completedAt, 4000));

        var summary = await Kpis(s).GetEarningsSummaryAsync(s.SupplierOrgId);

        Assert.Equal(inOctober ? 4000 : 0, summary.MonthAmountCents);
        Assert.Equal(inOctober ? 1 : 0, summary.MonthJobs);
    }

    [Fact]
    public async Task GetEarningsSummaryAsync_AverageResponse_IsTheTimeFromTheRequestToTheTakeOverTheLastNinetyDays()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var now = ServiceRequestScenario.Instant.UtcDateTime;
        foreach (var minutes in new[] { 10, 20, 25 })
            await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => Taken(r, now.AddDays(-5), minutes));
        // Taken 91 days ago: outside the window, it does not weigh on today's average.
        await s.SeedAsync(ServiceRequestStatus.Completato, r => Taken(r, now.AddDays(-91), 600));
        // Never taken: no answer to average.
        await s.SeedAsync(ServiceRequestStatus.Richiesto);

        var summary = await Kpis(s).GetEarningsSummaryAsync(s.SupplierOrgId);

        Assert.Equal(18, summary.AverageResponseMinutes); // (10 + 20 + 25) / 3 = 18.3
        Assert.Equal(90, SupplierEarningsSummary.ResponseWindowDays);
    }

    [Fact]
    public async Task HasAnsweredARequestAsync_OnlyATakenOrRejectedRequestOfTheSupplierCounts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto);
        await s.SeedAsync(ServiceRequestStatus.Annullato); // cancelled before anybody answered
        await s.SeedAsync(ServiceRequestStatus.Pagato, r => Taken(r, ServiceRequestScenario.Instant.UtcDateTime, 5), supplierOrgId: other);
        Assert.False(await Kpis(s).HasAnsweredARequestAsync(s.SupplierOrgId));

        await s.SeedAsync(ServiceRequestStatus.Rifiutato);
        Assert.True(await Kpis(s).HasAnsweredARequestAsync(s.SupplierOrgId));
    }

    [Fact]
    public async Task HasAnsweredARequestAsync_ATakenRequest_Counts()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.TakenAsync();

        Assert.True(await Kpis(s).HasAnsweredARequestAsync(s.SupplierOrgId));
    }

    // ─── Today ───

    [Fact]
    public async Task GetTodayAsync_ListsTheJobsOfTheRomeDayByTimeAndLeavesOutTheOtherDays()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var afternoon = await Job(s, "2026-10-08T12:00:00Z"); // 14:00
        var morning = await Job(s, "2026-10-08T07:00:00Z"); // 09:00
        var justAfterMidnight = await Job(s, "2026-10-07T22:30:00Z"); // 00:30 on the 8th in Rome
        await Job(s, "2026-10-08T22:30:00Z"); // 00:30 on the 9th in Rome
        await Job(s, "2026-10-09T08:00:00Z");
        var done = await Job(s, "2026-10-08T10:00:00Z", ServiceRequestStatus.Completato);

        var today = await Today(s).GetTodayAsync(s.SupplierOrgId);

        Assert.Equal(new DateOnly(2026, 10, 8), today.Date);
        Assert.Equal(new[] { justAfterMidnight, morning, done, afternoon }, today.Jobs.Select(job => job.Id));
        // A job the supplier took shows the property and the notes, as the console does after the take.
        Assert.All(today.Jobs, job =>
        {
            Assert.Equal(ServiceRequestScenario.PropertyName, job.Location.PropertyName);
            Assert.Equal(ServiceRequestScenario.HostNotes, job.Notes);
        });
    }

    [Fact]
    public async Task GetTodayAsync_NewRequestsComeByDeadline_WithoutThePropertyNorTheNotes_AndAreCappedWithTheTotalGiven()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var minutes = 12 - i; // the last created is the first to expire
            ids.Add((await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(minutes))).Id);
        }

        var today = await Today(s).GetTodayAsync(s.SupplierOrgId);

        Assert.Equal(12, today.NewRequestsTotal);
        Assert.Equal(10, today.NewRequests.Count);
        Assert.Equal(Enumerable.Reverse(ids).Take(10), today.NewRequests.Select(request => request.Id));
        Assert.All(today.NewRequests, request =>
        {
            Assert.Null(request.Location.PropertyName);
            Assert.Null(request.Notes);
            Assert.Equal(ServiceRequestScenario.Comune, request.Location.City);
        });
    }

    [Fact]
    public async Task GetTodayAsync_ASupplierWithNothing_HasEmptyListsAndNoAverage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var today = await Today(s).GetTodayAsync(s.SupplierOrgId);

        Assert.Empty(today.Jobs);
        Assert.Empty(today.NewRequests);
        Assert.Equal(0, today.NewRequestsTotal);
        Assert.Null(today.Earnings.AverageResponseMinutes);
    }

    [Fact]
    public async Task GetTodayAsync_NeverShowsTheRequestsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedAsync(ServiceRequestStatus.Richiesto, supplierOrgId: other);
        await s.SeedAsync(
            ServiceRequestStatus.PresoInCarico,
            r =>
            {
                r.TakenAt = r.CreatedAt;
                r.ScheduledStartUtc = Utc("2026-10-08T07:00:00Z");
                r.ScheduledEndUtc = Utc("2026-10-08T09:00:00Z");
            },
            supplierOrgId: other);

        var today = await Today(s).GetTodayAsync(s.SupplierOrgId);

        Assert.Empty(today.Jobs);
        Assert.Empty(today.NewRequests);
    }

    // ─── Checklist ───

    [Fact]
    public async Task GetChecklistAsync_ASupplierWithoutAProfile_IsNull()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var suppliers = new Mock<ISupplierService>();
        suppliers.Setup(x => x.GetProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((SupplierProfile?)null);

        Assert.Null(await Today(s, suppliers.Object).GetChecklistAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetChecklistAsync_ASupplierThatDidEverything_SaysSoAndLeavesThePaymentsEmpty()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.TakenAsync();
        var suppliers = Suppliers(s, SupplierStatus.Active, completion: 100, slug: "pulizie-roma");

        var checklist = await Today(s, suppliers).GetChecklistAsync(s.SupplierOrgId);

        Assert.NotNull(checklist);
        Assert.Equal(100, checklist.ProfileCompletionPercent);
        Assert.True(checklist.ProfileComplete);
        Assert.Equal(1, checklist.ActiveServices);
        Assert.True(checklist.HoursConfigured);
        Assert.Equal(ServiceRequestScenario.Instant.UtcDateTime, checklist.HoursConfiguredAt);
        Assert.True(checklist.ShowcasePublished);
        Assert.True(checklist.FirstRequestAnswered);
        // The payments of the suppliers are not available yet: not a "no", so no value.
        Assert.Null(checklist.PaymentsActive);
    }

    [Fact]
    public async Task GetChecklistAsync_ASupplierAtTheStart_SaysWhatIsMissing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var fresh = await s.AddOtherSupplierAsync();
        var suppliers = new Mock<ISupplierService>();
        suppliers
            .Setup(x => x.GetProfileAsync(fresh, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierProfile { OrgId = fresh, Status = SupplierStatus.Pending });
        suppliers.Setup(x => x.GetDashboardStatsAsync(fresh, It.IsAny<CancellationToken>())).ReturnsAsync(Dashboard(40));

        var checklist = await Today(s, suppliers.Object).GetChecklistAsync(fresh);

        Assert.NotNull(checklist);
        Assert.Equal(40, checklist.ProfileCompletionPercent);
        Assert.False(checklist.ProfileComplete);
        Assert.Equal(0, checklist.ActiveServices);
        Assert.False(checklist.HoursConfigured);
        Assert.Null(checklist.HoursConfiguredAt);
        Assert.False(checklist.ShowcasePublished);
        Assert.False(checklist.FirstRequestAnswered);
    }

    [Theory]
    [InlineData(SupplierStatus.Active, "pulizie-roma", true)]
    [InlineData(SupplierStatus.Active, null, false)]
    [InlineData(SupplierStatus.Active, "", false)]
    [InlineData(SupplierStatus.Pending, "pulizie-roma", false)]
    [InlineData(SupplierStatus.Suspended, "pulizie-roma", false)]
    public async Task GetChecklistAsync_TheShowcaseIsPublishedOnlyForAnActiveSupplierWithItsAddress(SupplierStatus status, string? slug, bool published)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var checklist = await Today(s, Suppliers(s, status, completion: 70, slug)).GetChecklistAsync(s.SupplierOrgId);

        Assert.Equal(published, checklist!.ShowcasePublished);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    public async Task GetChecklistAsync_TheProfileIsCompleteAtOneHundredPercent(int completion, bool complete)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var checklist = await Today(s, Suppliers(s, SupplierStatus.Active, completion, "x")).GetChecklistAsync(s.SupplierOrgId);

        Assert.Equal(complete, checklist!.ProfileComplete);
    }

    [Fact]
    public async Task GetChecklistAsync_OnlyThePublishedServicesCount()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft);
        await s.AddListingAsync("In pausa", status: SupplierServiceListingStatus.Paused);
        await s.AddListingAsync("Tolto", deletedAt: ServiceRequestScenario.Instant.UtcDateTime);
        await s.AddListingAsync("Secondo servizio");
        var other = await s.AddOtherSupplierAsync();
        await s.AddListingAsync("Del collega", supplierOrgId: other);

        var checklist = await Today(s, Suppliers(s, SupplierStatus.Active, 80, "x")).GetChecklistAsync(s.SupplierOrgId);

        Assert.Equal(2, checklist!.ActiveServices); // the one of the scenario and "Secondo servizio"
    }

    // ─── helpers ───

    private static SupplierKpiService Kpis(ServiceRequestScenario s) => new(s.Db, s.Clock);

    private static SupplierTodayService Today(ServiceRequestScenario s, ISupplierService? suppliers = null) =>
        new(s.Reader, Kpis(s), suppliers ?? Suppliers(s, SupplierStatus.Active, 100, "x"), s.Catalog, s.Agenda, s.Clock);

    private static ISupplierService Suppliers(ServiceRequestScenario s, SupplierStatus status, int completion, string? slug)
    {
        var suppliers = new Mock<ISupplierService>();
        suppliers
            .Setup(x => x.GetProfileAsync(s.SupplierOrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierProfile { OrgId = s.SupplierOrgId, Status = status, ShowcaseSlug = slug });
        suppliers.Setup(x => x.GetDashboardStatsAsync(s.SupplierOrgId, It.IsAny<CancellationToken>())).ReturnsAsync(Dashboard(completion));
        return suppliers.Object;
    }

    private static SupplierDashboard Dashboard(int completion) =>
        new(completion, "Active", 0, "None", null, null, null, "None", ServiceRequestScenario.Instant.UtcDateTime);

    /// <summary>A job of the supplier at <paramref name="startUtc"/>, two hours long, taken (or in <paramref name="status"/>).</summary>
    private static async Task<Guid> Job(ServiceRequestScenario s, string startUtc, ServiceRequestStatus status = ServiceRequestStatus.PresoInCarico)
    {
        var start = Utc(startUtc);
        var request = await s.SeedAsync(
            status,
            r =>
            {
                r.TakenAt = r.CreatedAt;
                r.ScheduledStartUtc = start;
                r.ScheduledEndUtc = start.AddHours(2);
                if (status == ServiceRequestStatus.Completato)
                    r.CompletedAt = start.AddHours(2);
            });
        return request.Id;
    }

    private static void Completed(ServiceRequest request, string completedAtUtc, int? finalAmountCents)
    {
        request.TakenAt = request.CreatedAt;
        request.CompletedAt = Utc(completedAtUtc);
        request.FinalAmountCents = finalAmountCents;
    }

    private static void Taken(ServiceRequest request, DateTime createdAt, int minutesToTake)
    {
        request.CreatedAt = createdAt;
        request.UpdatedAt = createdAt;
        request.TakenAt = createdAt.AddMinutes(minutesToTake);
    }

    private static DateTime Utc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal);
}
