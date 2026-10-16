using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Xunit;
using static Casazen.Tests.Unit.Services.SupplierReadModelsTestSupport;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04e: <c>GET api/supplier/dashboard/overview</c>, the three numbers of the console home (how often the supplier says yes, how long
/// it takes to answer, what comes in) for a period and for the same number of Rome days just before it. The scenario clock is Thursday
/// 8 October 2026, 12:00 in Rome: "this month" is 1-8 October (8 days) and the period before it 23-30 September (8 days).
/// </summary>
public class SupplierOverviewMetricsTests
{
    // ─── The periods ───

    [Fact]
    public async Task Overview_CurrentMonth_IsTheMonthSoFarAndTheSameNumberOfDaysBeforeIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(SupplierKpiPeriod.CurrentMonth, overview.Period);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8)), (overview.Current.From, overview.Current.To));
        Assert.Equal((new DateOnly(2026, 9, 23), new DateOnly(2026, 9, 30)), (overview.Previous.From, overview.Previous.To));
    }

    [Theory]
    [InlineData(SupplierKpiPeriod.Last30Days, "2026-09-09", "2026-10-08", "2026-08-10", "2026-09-08")]
    [InlineData(SupplierKpiPeriod.Last3Months, "2026-07-09", "2026-10-08", "2026-04-08", "2026-07-08")]
    public async Task Overview_TheOtherPeriods_AreTheirDaysAndTheSameNumberBefore(
        SupplierKpiPeriod period, string from, string to, string previousFrom, string previousTo)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, period);

        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), (overview.Current.From, overview.Current.To));
        Assert.Equal((DateOnly.Parse(previousFrom), DateOnly.Parse(previousTo)), (overview.Previous.From, overview.Previous.To));
    }

    [Theory]
    [InlineData(SupplierKpiPeriod.PreviousMonth)]
    [InlineData(SupplierKpiPeriod.CurrentYear)]
    [InlineData((SupplierKpiPeriod)99)]
    public async Task Overview_APeriodItDoesNotOffer_Throws(SupplierKpiPeriod period)
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Kpis.GetOverviewAsync(s.SupplierOrgId, period));
    }

    [Fact]
    public async Task Overview_ASupplierWithNothing_HasZeroCounts_AndNullForWhatDoesNotExist()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        foreach (var period in new[] { overview.Current, overview.Previous })
        {
            Assert.Equal(0, period.Received);
            Assert.Equal(0, period.Accepted);
            Assert.Equal(0, period.Rejected);
            Assert.Null(period.AcceptanceRate);
            Assert.Null(period.AverageResponseMinutes);
            Assert.Equal(0, period.CompletedJobs);
            Assert.Equal(0, period.PaidNetCents);
        }

        Assert.Equal(0, overview.Current.ToCollectCents);
        Assert.Equal(0, overview.Current.ToCollectJobs);
        Assert.False(overview.OnlinePayments);
    }

    // ─── Received, accepted, refused ───

    [Fact]
    public async Task Received_IsCountedByTheDayItArrived_WhateverBecameOfIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-10-02T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Annullato, "2026-10-03T08:00:00Z", updated: "2026-10-03T10:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-10-07T08:00:00Z", updated: "2026-10-07T09:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-09-25T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-09-24T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-09-10T08:00:00Z"); // before the period before

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(3, overview.Current.Received);
        Assert.Equal(2, overview.Previous.Received);
    }

    [Fact]
    public async Task Accepted_IsCountedByTheDayOfTheTake_AndACancellationAfterTheTakeKeepsIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // Arrived in September, taken in October: it is October's.
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-09-29T08:00:00Z", taken: "2026-10-02T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Completato, "2026-10-03T08:00:00Z", taken: "2026-10-03T08:20:00Z", completed: "2026-10-04T10:00:00Z");
        // Taken, then cancelled by the host: the supplier said yes, the rate does not change.
        await s.SeedRequestAsync(
            ServiceRequestStatus.Annullato,
            "2026-10-04T08:00:00Z",
            taken: "2026-10-04T08:05:00Z",
            updated: "2026-10-05T08:00:00Z",
            change: r => r.CancelledAt = Utc("2026-10-05T08:00:00Z"));
        await s.SeedRequestAsync(ServiceRequestStatus.Pagato, "2026-09-24T08:00:00Z", taken: "2026-09-24T08:30:00Z", completed: "2026-09-25T10:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-10-05T08:00:00Z");

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(3, overview.Current.Accepted);
        Assert.Equal(1, overview.Previous.Accepted);
    }

    [Fact]
    public async Task Rejected_IsCountedByTheDayOfTheRefusal()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-09-29T08:00:00Z", updated: "2026-10-02T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-10-03T08:00:00Z", updated: "2026-10-03T08:20:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-09-20T08:00:00Z", updated: "2026-09-25T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Annullato, "2026-10-03T08:00:00Z", updated: "2026-10-03T09:00:00Z"); // not a refusal

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(2, overview.Current.Rejected);
        Assert.Equal(1, overview.Previous.Rejected);
    }

    [Fact]
    public async Task AcceptanceRate_IsTheRequestsTakenOverTheRequestsAnswered_ForBothPeriods()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        for (var day = 1; day <= 4; day++)
            await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, $"2026-10-0{day}T08:00:00Z", taken: $"2026-10-0{day}T08:10:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-10-05T08:00:00Z", updated: "2026-10-05T08:10:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-09-24T08:00:00Z", taken: "2026-09-24T08:10:00Z");
        for (var day = 25; day <= 27; day++)
            await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, $"2026-09-{day}T08:00:00Z", updated: $"2026-09-{day}T08:10:00Z");

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(0.8, overview.Current.AcceptanceRate);
        Assert.Equal(0.25, overview.Previous.AcceptanceRate);
    }

    [Fact]
    public async Task AcceptanceRate_ARequestNobodyAnswered_IsNoAnswer_SoTheRateIsNull_NeverZero()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-10-02T08:00:00Z");
        // Cancelled by CasaZen for no answer: the supplier neither took it nor refused it.
        await s.SeedRequestAsync(
            ServiceRequestStatus.Annullato,
            "2026-10-03T08:00:00Z",
            updated: "2026-10-03T10:00:00Z",
            change: r =>
            {
                r.CancelledAt = Utc("2026-10-03T10:00:00Z");
                r.CancelledBy = ServiceRequestActorParty.System;
                r.CancellationReason = ServiceRequestCancellationReasons.NoResponse;
            });

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(2, overview.Current.Received);
        Assert.Null(overview.Current.AcceptanceRate);
        Assert.Null(overview.Previous.AcceptanceRate);
    }

    // ─── The time to answer ───

    [Fact]
    public async Task AverageResponse_IsTheMeanOfTheTimeToTakeTheRequestsTakenInThePeriod()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-10-02T08:00:00Z", taken: "2026-10-02T08:10:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-10-03T08:00:00Z", taken: "2026-10-03T08:20:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-10-04T08:00:00Z", taken: "2026-10-04T08:25:00Z");
        // Arrived in September, taken in October: its wait is counted in October, as a whole (3 days).
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-09-28T08:00:00Z", taken: "2026-10-01T08:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-09-24T08:00:00Z", taken: "2026-09-24T09:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-09-25T08:00:00Z", taken: "2026-09-25T09:30:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, "2026-10-05T08:00:00Z"); // not answered: no pair
        await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-10-05T08:00:00Z", updated: "2026-10-05T08:01:00Z"); // a refusal is no take

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(1094, overview.Current.AverageResponseMinutes); // (10 + 20 + 25 + 4320) / 4 = 1093.75
        Assert.Equal(75, overview.Previous.AverageResponseMinutes); // (60 + 90) / 2
    }

    [Fact]
    public async Task AverageResponse_SamePairsAsTheHome_SoTheTwoNumbersAgreeWhenTheWindowsAre()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // The home averages the requests taken in the last 90 days; the last 3 months of the overview cover the same requests here.
        foreach (var taken in new[] { "2026-09-10T08:10:00Z", "2026-09-10T08:20:00Z", "2026-09-10T08:25:00Z" })
            await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-09-10T08:00:00Z", taken: taken);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.Last3Months);
        var home = await s.Kpis.GetEarningsSummaryAsync(s.SupplierOrgId);

        Assert.Equal(18, overview.Current.AverageResponseMinutes);
        Assert.Equal(home.AverageResponseMinutes, overview.Current.AverageResponseMinutes);
    }

    // ─── Completed jobs ───

    [Fact]
    public async Task CompletedJobs_AreCountedByTheDayOfTheCompletion_APaidOneIncluded()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.Completato, "2026-09-20T08:00:00Z", taken: "2026-09-20T09:00:00Z", completed: "2026-10-02T10:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Pagato, "2026-09-20T08:00:00Z", taken: "2026-09-20T09:00:00Z", completed: "2026-10-03T10:00:00Z", paid: "2026-10-05T10:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.Completato, "2026-09-20T08:00:00Z", taken: "2026-09-20T09:00:00Z", completed: "2026-09-27T10:00:00Z");
        await s.SeedRequestAsync(ServiceRequestStatus.InCorso, "2026-09-20T08:00:00Z", taken: "2026-09-20T09:00:00Z");

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(2, overview.Current.CompletedJobs);
        Assert.Equal(1, overview.Previous.CompletedJobs);
    }

    // ─── Rome days, summer and winter time ───

    [Theory]
    [InlineData("2026-09-30T21:59:59Z", false)] // 23:59:59 on 30 September in Rome: the period before
    [InlineData("2026-09-30T22:00:00Z", true)] // 00:00:00 on 1 October in Rome: the first second of the month
    [InlineData("2026-10-08T21:59:59Z", true)] // 23:59:59 on 8 October in Rome: the last second of today
    [InlineData("2026-10-08T22:00:00Z", false)] // tomorrow
    public async Task TheDaysAreRomeDays_AtTheEdgesOfThePeriod(string created, bool inTheCurrentPeriod)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, created);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(inTheCurrentPeriod ? 1 : 0, overview.Current.Received);
        Assert.Equal(!inTheCurrentPeriod && created.StartsWith("2026-09", StringComparison.Ordinal) ? 1 : 0, overview.Previous.Received);
    }

    [Theory]
    [InlineData("2026-10-28T22:59:59Z", true)] // 23:59:59 on the 28th in Rome, winter time since the 25th (UTC+1)
    [InlineData("2026-10-28T23:00:00Z", false)] // 00:00 on the 29th
    [InlineData("2026-09-30T21:59:59Z", false)] // 23:59:59 on 30 September in Rome, still summer time (UTC+2)
    [InlineData("2026-09-30T22:00:00Z", true)]
    public async Task TheDaysAreRomeDays_AcrossTheEndOfSummerTime(string created, bool inTheCurrentPeriod)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero)); // 12:00 in Rome, the clocks went back on the 25th
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, created);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 28)), (overview.Current.From, overview.Current.To));
        Assert.Equal(inTheCurrentPeriod ? 1 : 0, overview.Current.Received);
    }

    [Theory]
    [InlineData("2026-04-05T21:59:59Z", true)] // 23:59:59 on 5 April in Rome (summer time since 29 March, UTC+2)
    [InlineData("2026-04-05T22:00:00Z", false)] // 00:00 on 6 April
    [InlineData("2026-03-06T22:59:59Z", false)] // 23:59:59 on 6 March in Rome (winter time, UTC+1): the day before the first day
    [InlineData("2026-03-06T23:00:00Z", true)] // 00:00 on 7 March, the first day of the last 30 days
    public async Task TheDaysAreRomeDays_AcrossTheStartOfSummerTime(string created, bool inTheCurrentPeriod)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 4, 5, 10, 0, 0, TimeSpan.Zero)); // 12:00 in Rome
        await s.SeedRequestAsync(ServiceRequestStatus.Richiesto, created);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.Last30Days);

        Assert.Equal((new DateOnly(2026, 3, 7), new DateOnly(2026, 4, 5)), (overview.Current.From, overview.Current.To));
        Assert.Equal(inTheCurrentPeriod ? 1 : 0, overview.Current.Received);
    }

    [Fact]
    public async Task ThePeriodBeforeTheFirstRequest_HasZeroCountsAndNullRates_NeverAnInventedPercentage()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-10-02T08:00:00Z", taken: "2026-10-02T08:10:00Z");

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(1.0, overview.Current.AcceptanceRate);
        Assert.Equal(10, overview.Current.AverageResponseMinutes);
        Assert.Equal(0, overview.Previous.Received);
        Assert.Null(overview.Previous.AcceptanceRate);
        Assert.Null(overview.Previous.AverageResponseMinutes);
    }

    [Fact]
    public async Task TheRequestsOfAnotherSupplier_AreNeverCounted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.SeedRequestAsync(ServiceRequestStatus.PresoInCarico, "2026-10-02T08:00:00Z", taken: "2026-10-02T08:10:00Z");
        for (var i = 0; i < 6; i++)
        {
            await s.SeedRequestAsync(
                ServiceRequestStatus.Completato,
                "2026-10-02T08:00:00Z",
                taken: "2026-10-02T09:00:00Z",
                completed: "2026-10-03T10:00:00Z",
                supplierOrgId: other,
                change: r => r.FinalAmountCents = 9000);
            await s.SeedRequestAsync(ServiceRequestStatus.Rifiutato, "2026-10-02T08:00:00Z", updated: "2026-10-02T08:05:00Z", supplierOrgId: other);
        }

        var mine = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);
        var theirs = await s.Kpis.GetOverviewAsync(other, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal((1, 1, 0, 0), (mine.Current.Received, mine.Current.Accepted, mine.Current.Rejected, mine.Current.CompletedJobs));
        Assert.Equal(0, mine.Current.ToCollectCents);
        Assert.Equal((12, 6, 6, 6), (theirs.Current.Received, theirs.Current.Accepted, theirs.Current.Rejected, theirs.Current.CompletedJobs));
        Assert.Equal(54000, theirs.Current.ToCollectCents);
    }

    // ─── Money: the payments that were paid ───

    [Fact]
    public async Task OnlinePayments_AreFalseWhileTheFlagIsOff_AndTheMoneyIsZeroEvenWithPaymentsInTheDatabase()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await PaidAsync(s, paidAt: "2026-10-05T10:00:00Z");

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.False(overview.OnlinePayments);
        Assert.Equal((0, 0, 0), (overview.Current.PaidNetCents, overview.Current.PaidGrossCents, overview.Current.CommissionCents));
        Assert.Equal(1, overview.Current.CompletedJobs); // the home talks about the jobs completed
    }

    [Fact]
    public async Task OnlinePayments_AreFalseWhileTheSupplierHasNoPaymentAtAll_EvenWithTheFlagOn()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        await s.SeedCompletedAsync();

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.False(overview.OnlinePayments);
        Assert.Equal(0, overview.Current.PaidNetCents);
    }

    [Fact]
    public async Task Money_IsTheNetOfThePaymentsPaidInEachPeriod_ByTheDayOfThePayment()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        await PaidAsync(s, paidAt: "2026-10-02T10:00:00Z", amountCents: 6000); // 600 commission, 5400 net
        await PaidAsync(s, paidAt: "2026-10-07T10:00:00Z", amountCents: 10000); // 1000 commission, 9000 net
        // Completed in September, paid in October: October's.
        await PaidAsync(s, paidAt: "2026-10-03T10:00:00Z", amountCents: 3000, completed: "2026-09-28T10:00:00Z");
        await PaidAsync(s, paidAt: "2026-09-25T10:00:00Z", amountCents: 20000); // the period before: 2000 commission, 18000 net
        await PaidAsync(s, paidAt: "2026-09-10T10:00:00Z", amountCents: 50000); // before the period before

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.True(overview.OnlinePayments);
        Assert.Equal(5400 + 9000 + 2700, overview.Current.PaidNetCents);
        Assert.Equal(6000 + 10000 + 3000, overview.Current.PaidGrossCents);
        Assert.Equal(600 + 1000 + 300, overview.Current.CommissionCents);
        Assert.Equal(18000, overview.Previous.PaidNetCents);
        Assert.Equal(20000, overview.Previous.PaidGrossCents);
        Assert.Equal(2000, overview.Previous.CommissionCents);
        // Net plus commission is gross, to the cent.
        Assert.Equal(overview.Current.PaidGrossCents, overview.Current.PaidNetCents + overview.Current.CommissionCents);
    }

    [Fact]
    public async Task Money_ARecordedOutsideCasaZenIsPaid_WithNoCommission()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        await PaidAsync(s, paidAt: "2026-10-02T10:00:00Z", amountCents: 6000, channel: ServicePaymentChannel.Offline, commissionPercent: 0m);
        await PaidAsync(s, paidAt: "2026-10-03T10:00:00Z", amountCents: 4000);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(6000 + 3600, overview.Current.PaidNetCents);
        Assert.Equal(400, overview.Current.CommissionCents);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested)]
    [InlineData(ServicePaymentStatus.Processing)]
    [InlineData(ServicePaymentStatus.Failed)]
    [InlineData(ServicePaymentStatus.Canceled)]
    [InlineData(ServicePaymentStatus.NeedsReview)]
    [InlineData(ServicePaymentStatus.Refunded)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded)]
    public async Task Money_OnlyAPaymentThatIsPaidCounts(ServicePaymentStatus status)
    {
        // The payments paid and then refunded are left out until the earnings page defines how a refund moves the figures.
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        var request = await s.SeedCompletedAsync(change: r => r.PaymentMode = ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, status, paidAt: Utc("2026-10-03T10:00:00Z"));

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        // A payment that was withdrawn is no payment: with only that, the supplier has nothing to show about online payments.
        Assert.Equal(status != ServicePaymentStatus.Canceled, overview.OnlinePayments);
        Assert.Equal(0, overview.Current.PaidNetCents);
        Assert.Equal(0, overview.Current.CommissionCents);
    }

    [Fact]
    public async Task Money_NeverIncludesThePaymentsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        var other = await s.AddOtherSupplierAsync();
        await PaidAsync(s, paidAt: "2026-10-02T10:00:00Z", amountCents: 6000);
        await PaidAsync(s, paidAt: "2026-10-02T10:00:00Z", amountCents: 80000, supplierOrgId: other);

        var mine = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);
        var theirs = await s.Kpis.GetOverviewAsync(other, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(5400, mine.Current.PaidNetCents);
        Assert.Equal(72000, theirs.Current.PaidNetCents);
    }

    [Fact]
    public async Task Money_WithoutTheFlagsNorTheReadsOfThePayments_IsZero_NotAnError()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await PaidAsync(s, paidAt: "2026-10-02T10:00:00Z");
        var bare = new SupplierKpiService(s.Db, s.Clock);

        var overview = await bare.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.False(overview.OnlinePayments);
        Assert.Equal(0, overview.Current.PaidNetCents);
    }

    // ─── Still to collect ───

    [Fact]
    public async Task ToCollect_IsTheDeclaredTotalOfAJobPaidOutsideCasaZen_AndTheNetOfOneAskedInsideIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedCompletedAsync(finalAmountCents: 8000); // paid by hand: the gross
        var online = await s.SeedCompletedAsync(finalAmountCents: 10000, mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(online.Id, ServicePaymentStatus.Requested, amountCents: 10000); // 1000 commission, 9000 net
        var inFlight = await s.SeedCompletedAsync(finalAmountCents: 5000, mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(inFlight.Id, ServicePaymentStatus.Processing, amountCents: 5000); // 4500 net, still to collect
        await s.SeedCompletedAsync(finalAmountCents: null); // a job with no amount: a job to collect, nothing to add
        await s.SeedRequestAsync(
            ServiceRequestStatus.Pagato, "2026-10-01T08:00:00Z", taken: "2026-10-01T08:10:00Z", completed: "2026-10-02T08:00:00Z", paid: "2026-10-03T08:00:00Z",
            change: r => r.FinalAmountCents = 99999); // paid: nothing to collect

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(4, overview.Current.ToCollectJobs);
        Assert.Equal(8000 + 9000 + 4500, overview.Current.ToCollectCents);
    }

    [Fact]
    public async Task ToCollect_AnOnlineJobWithNoPaymentYet_IsCountedAtTheDeclaredTotal()
    {
        // Waiting for the host to confirm an amount above the quote: the commission is not computed until the payment exists.
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedCompletedAsync(
            finalAmountCents: 7000, mode: ServiceRequestPaymentMode.Online, change: r => r.FinalAmountNeedsConfirmation = true);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.Equal(1, overview.Current.ToCollectJobs);
        Assert.Equal(7000, overview.Current.ToCollectCents);
    }

    [Fact]
    public async Task ToCollect_IsTheStateNow_SoItIsInTheCurrentPeriodOnly_WhateverThePeriod()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedCompletedAsync(finalAmountCents: 8000); // completed on 6 October

        foreach (var period in SupplierKpiPeriods.OverviewPeriods)
        {
            var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, period);

            Assert.Equal(8000, overview.Current.ToCollectCents);
            Assert.Equal(1, overview.Current.ToCollectJobs);
            Assert.Null(overview.Previous.ToCollectCents);
            Assert.Null(overview.Previous.ToCollectJobs);
        }
    }

    [Fact]
    public async Task ToCollect_WithTheFlagOff_IsStillTheDeclaredTotalOfTheJobsPaidByHand()
    {
        // The money figures of the payments are 0 with the flag off, but the jobs still to be collected are a fact about the requests.
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.SeedCompletedAsync(finalAmountCents: 6500);

        var overview = await s.Kpis.GetOverviewAsync(s.SupplierOrgId, SupplierKpiPeriod.CurrentMonth);

        Assert.False(overview.OnlinePayments);
        Assert.Equal(6500, overview.Current.ToCollectCents);
    }

    // ─── helpers ───

    /// <summary>A job completed on 1 October and paid at <paramref name="paidAt"/>: the request, paid, and its payment record.</summary>
    private static async Task PaidAsync(
        ServiceRequestScenario s,
        string paidAt,
        int amountCents = ServiceRequestScenario.ServicePriceCents,
        string completed = "2026-10-01T10:00:00Z",
        ServicePaymentChannel channel = ServicePaymentChannel.Stripe,
        decimal commissionPercent = 10m,
        Guid? supplierOrgId = null)
    {
        var request = await s.SeedRequestAsync(
            ServiceRequestStatus.Pagato,
            "2026-09-01T08:00:00Z",
            taken: "2026-09-01T08:10:00Z",
            completed: completed,
            paid: paidAt,
            supplierOrgId: supplierOrgId,
            change: r =>
            {
                r.FinalAmountCents = amountCents;
                r.PaymentMode = ServiceRequestPaymentMode.Online;
            });
        await s.AddPaymentAsync(
            request.Id,
            ServicePaymentStatus.Paid,
            amountCents: amountCents,
            commissionPercent: commissionPercent,
            paidAt: Utc(paidAt),
            paidVia: channel,
            supplierOrgId: supplierOrgId);
    }
}
