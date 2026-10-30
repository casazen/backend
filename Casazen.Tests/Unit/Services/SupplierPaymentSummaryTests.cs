using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Casazen.Tests.Unit.Services.SupplierReadModelsTestSupport;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04e: the <c>payment</c> summary of a completed job in the console (where the payment stands, the split gross/commission/net, what
/// the supplier may do) and that <c>canRequest</c> and <c>canRecordOffline</c> are the rules of the two endpoints of SP-15a: for every
/// state a job can be in, the real service accepts the action exactly when the summary says it may. Scenario clock: Thursday
/// 8 October 2026, 12:00 in Rome.
/// </summary>
public class SupplierPaymentSummaryTests
{
    // ─── Which requests have a summary ───

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public async Task Payment_ARequestThatIsNotCompletedYet_HasNoSummary(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedAsync(status);

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.Null(view!.Payment);
    }

    [Fact]
    public async Task Payment_AJobTheHostMarkedPaidByHand_HasNoRecordAndNoSummary()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedRequestAsync(
            ServiceRequestStatus.Pagato,
            "2026-10-01T08:00:00Z",
            taken: "2026-10-01T08:10:00Z",
            completed: "2026-10-02T08:00:00Z",
            paid: "2026-10-03T08:00:00Z",
            change: r => r.FinalAmountCents = 6000);

        var view = await s.Reader.GetAsync(request.Id, s.SupplierOrgId);

        Assert.Null(view!.Payment);
        Assert.Equal(Utc("2026-10-03T08:00:00Z"), view.PaidAt);
    }

    [Fact]
    public async Task Payment_ACompletedJobPaidOutsideCasaZen_HasASummaryWithNoRecord_ButTheActionsAreTold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(finalAmountCents: 7500);

        var payment = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment;

        Assert.NotNull(payment);
        Assert.Null(payment.Id);
        Assert.Null(payment.Status);
        Assert.Equal(7500, payment.AmountCents); // what the supplier declared
        Assert.Null(payment.CommissionPercent); // nothing was computed: no commission is charged outside CasaZen
        Assert.Null(payment.CommissionCents);
        Assert.Null(payment.NetCents);
        Assert.Null(payment.PaidAt);
        Assert.Null(payment.PaidVia);
        Assert.Null(payment.RequestedAt);
        Assert.Null(payment.LastSentAt);
        Assert.Equal(0, payment.SentCount);
        Assert.False(payment.Late);
        Assert.False(payment.NeedsAmountConfirmation);
        Assert.False(payment.CanRequest);
        Assert.True(payment.CanRecordOffline);
    }

    [Fact]
    public async Task Payment_ACompletedJobWithNoDeclaredAmount_CannotBeRecordedAsPaid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(finalAmountCents: null);

        var payment = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment;

        Assert.NotNull(payment);
        Assert.Null(payment.AmountCents);
        Assert.False(payment.CanRecordOffline);
        Assert.False(payment.CanRequest);
    }

    // ─── The record ───

    [Fact]
    public async Task Payment_OfAJobAskedInsideCasaZen_ShowsTheSplitAndWhereItStands()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
        await s.ConnectSupplierAsync();
        var request = await s.SeedCompletedAsync(finalAmountCents: 12345, mode: ServiceRequestPaymentMode.Online);
        var payment = await s.AddPaymentAsync(
            request.Id,
            ServicePaymentStatus.Requested,
            amountCents: 12345,
            commissionPercent: 10m,
            requestedAt: Utc("2026-10-06T10:00:00Z"),
            lastSentAt: Utc("2026-10-07T10:00:00Z"),
            sentCount: 2);

        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Equal(payment.Id, summary.Id);
        Assert.Equal(ServicePaymentStatus.Requested, summary.Status);
        Assert.Equal(12345, summary.AmountCents);
        Assert.Equal(10m, summary.CommissionPercent);
        Assert.Equal(1235, summary.CommissionCents); // 1234.5 rounds away from zero, like the payment was created
        Assert.Equal(12345 - 1235, summary.NetCents);
        Assert.Null(summary.PaidAt);
        Assert.Null(summary.PaidVia);
        Assert.Equal(Utc("2026-10-06T10:00:00Z"), summary.RequestedAt);
        Assert.Equal(Utc("2026-10-07T10:00:00Z"), summary.LastSentAt);
        Assert.Equal(2, summary.SentCount);
        Assert.False(summary.Late);
        Assert.False(summary.NeedsAmountConfirmation);
    }

    [Fact]
    public async Task Payment_TheSplitIsTheOneOfTheRecord_NotRecomputedWithTheCurrentCommission()
    {
        // The commission is a snapshot taken when the payment was created: a later change of the percentage does not rewrite it.
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(finalAmountCents: 10000, mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, amountCents: 10000, commissionPercent: 3.5m);

        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Equal(3.5m, summary.CommissionPercent); // the configured commission of the scenario is 10 %
        Assert.Equal(350, summary.CommissionCents);
        Assert.Equal(9650, summary.NetCents);
    }

    [Fact]
    public async Task Payment_OfAPaidJob_ShowsWhenAndHow()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedRequestAsync(
            ServiceRequestStatus.Pagato,
            "2026-10-01T08:00:00Z",
            taken: "2026-10-01T08:10:00Z",
            completed: "2026-10-02T08:00:00Z",
            paid: "2026-10-04T09:00:00Z",
            change: r =>
            {
                r.FinalAmountCents = 6000;
                r.PaymentMode = ServiceRequestPaymentMode.Online;
            });
        await s.AddPaymentAsync(
            request.Id,
            ServicePaymentStatus.Paid,
            lateAt: Utc("2026-10-03T08:00:00Z"), // it was late for a while
            paidAt: Utc("2026-10-04T09:00:00Z"),
            paidVia: ServicePaymentChannel.Stripe);

        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Equal(ServicePaymentStatus.Paid, summary.Status);
        Assert.Equal(Utc("2026-10-04T09:00:00Z"), summary.PaidAt);
        Assert.Equal(ServicePaymentChannel.Stripe, summary.PaidVia);
        Assert.False(summary.Late); // paid: no longer late
        Assert.False(summary.CanRequest);
        Assert.False(summary.CanRecordOffline);
    }

    [Fact]
    public async Task Payment_OfAPaymentRecordedAsReceivedOutsideCasaZen_HasNoCommission()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedRequestAsync(
            ServiceRequestStatus.Pagato,
            "2026-10-01T08:00:00Z",
            taken: "2026-10-01T08:10:00Z",
            completed: "2026-10-02T08:00:00Z",
            paid: "2026-10-04T09:00:00Z",
            change: r => r.FinalAmountCents = 6000);
        await s.AddPaymentAsync(
            request.Id, ServicePaymentStatus.Paid, commissionPercent: 0m, paidAt: Utc("2026-10-04T09:00:00Z"), paidVia: ServicePaymentChannel.Offline);

        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Equal(ServicePaymentChannel.Offline, summary.PaidVia);
        Assert.Equal(0m, summary.CommissionPercent);
        Assert.Equal(0, summary.CommissionCents);
        Assert.Equal(6000, summary.NetCents);
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, true)]
    [InlineData(ServicePaymentStatus.Failed, true)]
    [InlineData(ServicePaymentStatus.Processing, false)]
    [InlineData(ServicePaymentStatus.NeedsReview, false)]
    public async Task Late_IsAFlaggedPaymentTheHostStillOwes(ServicePaymentStatus status, bool late)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, status, lateAt: Utc("2026-10-07T08:00:00Z"));

        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Equal(late, summary.Late);
    }

    [Fact]
    public async Task Late_APaymentNotFlaggedYet_IsNotLate_WhateverTheDate()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, requestedAt: Utc("2026-09-01T08:00:00Z"), lateAt: null);

        Assert.False((await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!.Late);
    }

    [Fact]
    public async Task Payment_ACanceledRecord_IsNoPayment_TheLiveOneIsTheOneShown()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(finalAmountCents: 6000, mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, ServicePaymentStatus.Canceled, amountCents: 4000);

        var onlyCanceled = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Null(onlyCanceled.Id);
        Assert.Null(onlyCanceled.Status);
        Assert.Equal(6000, onlyCanceled.AmountCents); // the declared total, not the withdrawn record's

        var live = await s.AddPaymentAsync(request.Id, ServicePaymentStatus.Requested, amountCents: 6000);
        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.Equal(live.Id, summary.Id);
        Assert.Equal(ServicePaymentStatus.Requested, summary.Status);
    }

    [Fact]
    public async Task Payment_AnAmountTheHostHasToConfirm_IsToldWithNoRecordYet()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(
            finalAmountCents: 8000, mode: ServiceRequestPaymentMode.Online, change: r => r.FinalAmountNeedsConfirmation = true);

        var summary = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.True(summary.NeedsAmountConfirmation);
        Assert.Null(summary.Id);
        Assert.Equal(8000, summary.AmountCents);
        Assert.False(summary.CanRequest);
        Assert.True(summary.CanRecordOffline);
    }

    // ─── The list, the detail and the others ───

    [Fact]
    public async Task Payment_IsInTheListAndInTheDetail_AndOnlyForTheCompletedOnes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var completed = await s.SeedCompletedAsync(mode: ServiceRequestPaymentMode.Online);
        var payment = await s.AddPaymentAsync(completed.Id);
        var taken = await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => r.TakenAt = r.CreatedAt);

        var (items, _) = await s.Reader.ListAsync(s.SupplierOrgId, new SupplierInboxQuery([], null, null, 1, 100));

        Assert.Equal(payment.Id, items.Single(item => item.Id == completed.Id).Payment!.Id);
        Assert.Null(items.Single(item => item.Id == taken.Id).Payment);
        Assert.Equal(payment.Id, (await s.Reader.GetAsync(completed.Id, s.SupplierOrgId))!.Payment!.Id);
    }

    [Fact]
    public async Task Payment_IsInTheHomeOfTheConsoleToo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync(
            finalAmountCents: 6000,
            mode: ServiceRequestPaymentMode.Online,
            change: r =>
            {
                r.ScheduledStartUtc = Utc("2026-10-08T07:00:00Z");
                r.ScheduledEndUtc = Utc("2026-10-08T09:00:00Z");
            });
        await s.AddPaymentAsync(request.Id);
        var today = new SupplierTodayService(s.Reader, s.Kpis, Moq.Mock.Of<ISupplierService>(), s.Catalog, s.Agenda, s.Clock);

        var job = Assert.Single((await today.GetTodayAsync(s.SupplierOrgId)).Jobs);

        Assert.Equal("completed", job.Phase);
        Assert.Equal(ServicePaymentStatus.Requested, job.Payment!.Status);
    }

    [Fact]
    public async Task Payment_TheReadModelNeverReadsThePaymentsOfAnotherSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var theirs = await s.SeedCompletedAsync(supplierOrgId: other, mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(theirs.Id, amountCents: 12000, supplierOrgId: other);
        var input = new SupplierPaymentSummaryInput(theirs.Id, ServiceRequestStatus.Completato, ServiceRequestPaymentMode.Online, 12000, false, true);

        // Asked for by a supplier that is not theirs, even with the id of their request: their payment is not found.
        var summaries = await s.Kit.PaymentReads.SummariesAsync(s.SupplierOrgId, [input]);

        var summary = summaries[theirs.Id];
        Assert.Null(summary.Id);
        Assert.Null(summary.Status);
        Assert.Null(summary.CommissionCents);
        Assert.Null(summary.NetCents);
        Assert.Empty(await s.Kit.PaymentReads.NetToCollectAsync(s.SupplierOrgId, [theirs.Id]));
        Assert.Single(await s.Kit.PaymentReads.NetToCollectAsync(other, [theirs.Id]));
    }

    [Fact]
    public async Task Payment_ASupplierWithoutThePaymentReads_GetsViewsWithNoSummary_NotAnError()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.SeedCompletedAsync();
        var bare = new Casazen.Infrastructure.Services.SupplierServiceRequestReader(s.Db, s.Clock);

        Assert.Null((await bare.GetAsync(request.Id, s.SupplierOrgId))!.Payment);
    }

    // ─── What the payer's address and the origin of the request change ───

    [Fact]
    public async Task CanRequest_AHostOrgWithoutAnAddress_IsFalse_AndOfflineStaysPossible()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnablePaymentsAsync();
        var host = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
        host.ContactEmail = string.Empty;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var request = await s.SeedCompletedAsync(mode: ServiceRequestPaymentMode.Online);
        await s.AddPaymentAsync(request.Id, lastSentAt: Utc("2026-10-01T08:00:00Z"));

        var payment = (await s.Reader.GetAsync(request.Id, s.SupplierOrgId))!.Payment!;

        Assert.False(payment.CanRequest);
        Assert.True(payment.CanRecordOffline);
    }

    [Fact]
    public async Task CanRequest_ARequestFromTheShowcase_IsNeverTrue_BecauseItsCustomerHasNoAddressToSendALinkTo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (showcase, _) = await s.BookedAsync();
        // After the booking: with the payments on, a booking of a showcase blocks the card of its customer (SP-15c-a) and is paid inside CasaZen.
        await s.EnablePaymentsAsync();
        await s.ChangeRequestAsync(showcase.Id, r =>
        {
            // Even a showcase request marked Online by mistake, completed and old enough for a reminder.
            r.Status = ServiceRequestStatus.Completato;
            r.TakenAt = r.CreatedAt;
            r.CompletedAt = r.CreatedAt;
            r.FinalAmountCents = 6000;
            r.PaymentMode = ServiceRequestPaymentMode.Online;
        });

        var payment = (await s.Reader.GetAsync(showcase.Id, s.SupplierOrgId))!.Payment!;

        Assert.False(payment.CanRequest);
        Assert.True(payment.CanRecordOffline);
    }

    // ─── The rules are the rules of the endpoints ───

    private static readonly string[] ScenarioNames =
    [
        "not-completed-new",
        "not-completed-taken",
        "not-completed-in-progress",
        "paid-by-the-host-by-hand",
        "paid-recorded-outside-casazen",
        "manual-completed",
        "manual-completed-flag-on-supplier-not-connected",
        "completed-without-amount",
        "completed-below-the-minimum-amount",
        "showcase-completed",
        "online-just-requested",
        "online-23-hours-after",
        "online-a-day-after",
        "online-flag-switched-off-after",
        "online-supplier-lost-its-account",
        "online-supplier-suspended",
        "online-supplier-has-to-accept-new-terms",
        "online-host-without-an-address",
        "online-pending-supplier-not-ready",
        "online-pending-then-supplier-ready",
        "online-amount-to-confirm",
        "online-amount-confirmed-a-day-after",
        "online-failed-attempt-a-day-after",
        "online-processing",
        "online-under-review",
        "online-paid-while-the-job-is-still-completed",
        "online-refunded-while-the-job-is-still-completed",
    ];

    public static TheoryData<string> Scenarios
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in ScenarioNames)
                data.Add(name);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task CanRequest_IsTrueExactlyWhenThePaymentRequestEndpointAccepts(string scenario)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var id = await PrepareAsync(s, scenario);
        var summary = (await s.Reader.GetAsync(id, s.SupplierOrgId))!.Payment;
        var told = summary?.CanRequest ?? false;
        var flagOn = s.Flags.IsEnabled(FeatureFlags.SupplierOnlinePayments);

        // The endpoint is behind the flag (404 before authentication while it is off); the rest is the service it calls.
        var accepted = flagOn && await AcceptsAsync(() => s.Service.RequestPaymentAsync(id, s.SupplierOrgId));

        Assert.True(told == accepted, $"{scenario}: the summary says canRequest={told}, the endpoint {(accepted ? "accepts" : "refuses")}");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task CanRecordOffline_IsTrueExactlyWhenTheOfflinePaymentEndpointAccepts(string scenario)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var id = await PrepareAsync(s, scenario);
        var summary = (await s.Reader.GetAsync(id, s.SupplierOrgId))!.Payment;
        var told = summary?.CanRecordOffline ?? false;

        // A reason is always given: it is mandatory for a request paid inside CasaZen, an input of the action and not a condition of it.
        var accepted = await AcceptsAsync(() => s.Service.RecordOfflinePaymentAsync(
            id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Pagato in contanti alla consegna"));

        Assert.True(told == accepted, $"{scenario}: the summary says canRecordOffline={told}, the endpoint {(accepted ? "accepts" : "refuses")}");
    }

    [Fact]
    public async Task TheScenarios_CoverEveryReasonForARefusal_AndBothAnswers()
    {
        // The matrix proves both directions: some scenarios where the action is possible, some where each rule says no.
        var requestable = new List<string>();
        var recordable = new List<string>();
        foreach (var scenario in ScenarioNames)
        {
            using var s = await ServiceRequestScenario.CreateAsync();
            var id = await PrepareAsync(s, scenario);
            var summary = (await s.Reader.GetAsync(id, s.SupplierOrgId))!.Payment;
            if (summary?.CanRequest == true)
                requestable.Add(scenario);
            if (summary?.CanRecordOffline == true)
                recordable.Add(scenario);
        }

        Assert.Contains("online-a-day-after", requestable);
        Assert.Contains("online-pending-then-supplier-ready", requestable);
        Assert.Contains("online-failed-attempt-a-day-after", requestable);
        Assert.Contains("online-amount-confirmed-a-day-after", requestable);
        Assert.True(requestable.Count >= 4 && requestable.Count < ScenarioNames.Length / 2, $"requestable in {requestable.Count} scenarios");
        Assert.Contains("manual-completed", recordable);
        Assert.Contains("online-amount-to-confirm", recordable);
        Assert.DoesNotContain("online-supplier-suspended", recordable);
        Assert.True(recordable.Count >= 10, $"recordable in {recordable.Count} scenarios");
    }

    // ─── helpers ───

    private static async Task<bool> AcceptsAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (DomainRuleException)
        {
            return false;
        }
        catch (DomainConflictException)
        {
            return false;
        }
    }

    /// <summary>The request of the scenario <paramref name="name"/> (see <see cref="Scenarios"/>), in the state it names.</summary>
    private static async Task<Guid> PrepareAsync(ServiceRequestScenario s, string name)
    {
        switch (name)
        {
            case "not-completed-new":
                return (await s.RequestAsync()).Id;

            case "not-completed-taken":
                return (await s.TakenAsync()).Id;

            case "not-completed-in-progress":
                return (await s.StartedAsync()).Id;

            case "paid-by-the-host-by-hand":
                {
                    var taken = await s.TakenAsync();
                    await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
                    await s.Service.MarkPaidAsync(taken.Id, s.HostOrgId);
                    return taken.Id;
                }

            case "paid-recorded-outside-casazen":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await s.Service.RecordOfflinePaymentAsync(completed.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, "Contanti");
                    return completed.Id;
                }

            case "manual-completed":
                {
                    var taken = await s.TakenAsync();
                    return (await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId)).Id;
                }

            case "manual-completed-flag-on-supplier-not-connected":
                {
                    s.Flags.Set(FeatureFlags.SupplierOnlinePayments, true);
                    var taken = await s.TakenAsync();
                    return (await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId)).Id;
                }

            case "completed-without-amount":
                return (await s.SeedCompletedAsync(finalAmountCents: null)).Id;

            case "completed-below-the-minimum-amount":
                {
                    await s.EnablePaymentsAsync();
                    var taken = await s.TakenAsync();
                    return (await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 40))).Id;
                }

            case "showcase-completed":
                {
                    await s.EnableBookingAsync();
                    var (showcase, _) = await s.BookedAsync();
                    await s.EnablePaymentsAsync(); // after the booking: see SupplierPaymentShowcaseTests
                    await s.Service.TakeAsync(showcase.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
                    await s.Service.CompleteAsync(showcase.Id, s.SupplierOrgId);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return showcase.Id;
                }

            case "online-just-requested":
                return (await s.CompletedOnlineAsync()).Id;

            case "online-23-hours-after":
                {
                    var completed = await s.CompletedOnlineAsync();
                    s.Clock.Advance(TimeSpan.FromHours(23));
                    return completed.Id;
                }

            case "online-a-day-after":
                {
                    var completed = await s.CompletedOnlineAsync();
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-flag-switched-off-after":
                {
                    var completed = await s.CompletedOnlineAsync();
                    s.Flags.Set(FeatureFlags.SupplierOnlinePayments, false);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-supplier-lost-its-account":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await s.ConnectSupplierAsync(ready: false);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-supplier-suspended":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-supplier-has-to-accept-new-terms":
                {
                    var completed = await s.CompletedOnlineAsync();
                    var profile = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == s.SupplierOrgId);
                    profile.TosAcceptedAt = Now.AddDays(-30);
                    profile.TosVersion = "2020-01-old";
                    await s.Db.SaveChangesAsync();
                    s.Db.ChangeTracker.Clear();
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-host-without-an-address":
                {
                    var host = await s.Db.Orgs.SingleAsync(o => o.Id == s.HostOrgId);
                    host.ContactEmail = string.Empty;
                    await s.Db.SaveChangesAsync();
                    s.Db.ChangeTracker.Clear();
                    var completed = await s.CompletedOnlineAsync();
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-pending-supplier-not-ready":
                {
                    await s.EnablePaymentsAsync();
                    var taken = await s.TakenAsync();
                    await s.ConnectSupplierAsync(ready: false);
                    return (await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId)).Id;
                }

            case "online-pending-then-supplier-ready":
                {
                    await s.EnablePaymentsAsync();
                    var taken = await s.TakenAsync();
                    await s.ConnectSupplierAsync(ready: false);
                    var completed = await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId);
                    await s.ConnectSupplierAsync(ready: true);
                    return completed.Id;
                }

            case "online-amount-to-confirm":
                {
                    await s.EnablePaymentsAsync();
                    var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 6000));
                    return (await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 8000))).Id;
                }

            case "online-amount-confirmed-a-day-after":
                {
                    await s.EnablePaymentsAsync();
                    var taken = await s.TakenAsync(command: new TakeServiceRequestCommand(QuotedAmountCents: 6000));
                    await s.Service.CompleteAsync(taken.Id, s.SupplierOrgId, new CompleteServiceRequestCommand(FinalAmountCents: 8000));
                    await s.Service.ConfirmFinalAmountAsync(taken.Id, s.HostOrgId);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return taken.Id;
                }

            case "online-failed-attempt-a-day-after":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await SetPaymentStatusAsync(s, completed.Id, ServicePaymentStatus.Failed);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-processing":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await SetPaymentStatusAsync(s, completed.Id, ServicePaymentStatus.Processing);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-under-review":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await SetPaymentStatusAsync(s, completed.Id, ServicePaymentStatus.NeedsReview);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-paid-while-the-job-is-still-completed":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await SetPaymentStatusAsync(s, completed.Id, ServicePaymentStatus.Paid);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            case "online-refunded-while-the-job-is-still-completed":
                {
                    var completed = await s.CompletedOnlineAsync();
                    await SetPaymentStatusAsync(s, completed.Id, ServicePaymentStatus.Refunded);
                    s.Clock.Advance(TimeSpan.FromHours(25));
                    return completed.Id;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown scenario.");
        }
    }

    /// <summary>Puts the only payment of the request in <paramref name="status"/> (a paid one says when and how it was paid).</summary>
    private static async Task SetPaymentStatusAsync(ServiceRequestScenario s, Guid requestId, ServicePaymentStatus status)
    {
        var payment = await s.Db.ServiceRequestPayments.SingleAsync(p => p.ServiceRequestId == requestId);
        payment.Status = status;
        if (status is ServicePaymentStatus.Paid or ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded)
        {
            payment.PaidAt = Now;
            payment.PaidVia = ServicePaymentChannel.Stripe;
        }

        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }
}
