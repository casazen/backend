using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-REL: what a supplier does with a request weighs on its reliability. An answer before the deadline earns a point, a job completed
/// earns two, a dispute takes them back and costs more, and a request nobody answered costs the supplier points (and tells it so)
/// unless it could not have answered. Only the requests of the hosts count: the bookings of a supplier's own showcase do not. The clock
/// is Thursday 8 October 2026, 12:00 in Rome; a request of a host is due 120 minutes after it is made.
/// </summary>
public class ServiceRequestReliabilityTests
{
    private static readonly TimeSpan HostWindow = TimeSpan.FromMinutes(120);

    // ─── The answers ───

    [Fact]
    public async Task Take_BeforeTheDeadline_EarnsAPointOfReliability()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(30));

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var recorded = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, recorded.Kind);
        Assert.Equal(request.Id, recorded.RequestId);
        Assert.False(recorded.AsBackup);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, recorded.OccurredAt);
    }

    [Fact]
    public async Task Take_AfterTheDeadline_EarnsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(HostWindow + TimeSpan.FromMinutes(1));

        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Fact]
    public async Task Reject_BeforeTheDeadline_IsAnAnswerInTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        await s.Service.RejectAsync(request.Id, s.SupplierOrgId, "Siamo al completo");

        var recorded = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.RepliedInTime, recorded.Kind);
        Assert.Equal(request.Id, recorded.RequestId);
    }

    [Fact]
    public async Task AProposalAndAQuote_AreAnswersInTime_AndALaterTakeAddsNoSecondPoint()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var proposed = await s.RequestAsync();
        var quoted = await s.RequestAsync(ServiceRequestScenario.SaturdayAt09);

        await s.Service.ProposeTimeAsync(
            proposed.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        await s.Service.SendQuoteAsync(
            quoted.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new SendServiceRequestQuoteCommand(new List<ServiceRequestQuoteLine> { new("Pulizia profonda", 30000) }, "Materiali compresi", null));

        Assert.Equal(
            new[] { proposed.Id, quoted.Id }.Order(),
            (await s.ReliabilityEventsAsync()).Where(e => e.Kind == SupplierReliabilityEventKind.RepliedInTime).Select(e => e.RequestId!.Value).Order());

        // The host turns the proposal down and the supplier takes the request: the answer was already counted.
        await s.Service.RejectProposalAsync(proposed.Id, s.HostOrgId);
        await s.Service.TakeAsync(proposed.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(2, (await s.ReliabilityEventsAsync()).Count);
    }

    // ─── The jobs ───

    [Theory]
    [InlineData(ServiceRequestRentalContext.ShortRent)]
    [InlineData(ServiceRequestRentalContext.LongRent)]
    public async Task Complete_RecordsACleanJob_OnceForEachRequest(ServiceRequestRentalContext context)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(rentalContext: context, durationMinutes: ServiceRequestScenario.ServiceMinutes);
        await s.Service.TakeAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        var completed = await s.Service.CompleteAsync(request.Id, s.SupplierOrgId);

        var job = Assert.Single(await s.ReliabilityEventsAsync(), e => e.Kind == SupplierReliabilityEventKind.JobCompletedClean);
        Assert.Equal(request.Id, job.RequestId);
        Assert.Equal(completed.CompletedAt, job.OccurredAt);
        // The reply in time and the job: one point and two.
        Assert.Equal(1 + 2, (await s.Kit.Reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);
    }

    [Fact]
    public async Task ARefusedCompletion_RecordsNoJob()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();

        // Not taken yet: the state machine refuses the completion, and nothing is written for it.
        await Assert.ThrowsAnyAsync<Exception>(() => s.Service.CompleteAsync(request.Id, s.SupplierOrgId));

        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    [Theory]
    [InlineData(ServiceRequestRentalContext.ShortRent, true)]
    [InlineData(ServiceRequestRentalContext.LongRent, true)]
    [InlineData(ServiceRequestRentalContext.Showcase, false)]
    public void CountsForReliability_OnlyTheRequestsOfTheHosts(ServiceRequestRentalContext context, bool counts)
    {
        Assert.Equal(counts, ServiceRequestService.CountsForReliability(new ServiceRequest { RentalContext = context }));
    }

    [Fact]
    public async Task ABookingFromTheShowcase_EarnsAndCostsNothing()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestAutoCancel, true);
        var booking = await s.SeedAsync(
            ServiceRequestStatus.Richiesto,
            r =>
            {
                r.RentalContext = ServiceRequestRentalContext.Showcase;
                r.OrgId = s.SupplierOrgId;
                r.PropertyId = null;
                r.BookingId = null;
                r.CustomerId = Guid.NewGuid();
                r.ResponseDueAt = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-5);
            });
        var answered = await s.SeedAsync(
            ServiceRequestStatus.Richiesto,
            r =>
            {
                r.RentalContext = ServiceRequestRentalContext.Showcase;
                r.OrgId = s.SupplierOrgId;
                r.PropertyId = null;
                r.BookingId = null;
                r.CustomerId = Guid.NewGuid();
                r.ScheduledStartUtc = ServiceRequestScenario.FridayAt14;
                r.ScheduledEndUtc = ServiceRequestScenario.FridayAt14.AddHours(2);
                r.ResponseDueAt = ServiceRequestScenario.Instant.UtcDateTime.AddHours(2);
            });

        // One lapses with nobody answering it, the other one is taken in time: neither is a sign of reliability for the hosts.
        await AutoCancel(s).CancelUnansweredShowcaseAsync();
        await s.Service.TakeAsync(answered.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);

        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(booking.Id)).Status);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(answered.Id)).Status);
        Assert.Empty(await s.ReliabilityEventsAsync());
    }

    // ─── The requests nobody answered ───

    [Fact]
    public async Task AutoCancel_ARequestNobodyAnswered_CostsThePrimaryItsPoints_AndTheEmailSaysSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestAutoCancel, true);
        var request = await s.RequestAsync();
        // Due at 14:00 in Rome: the two hours it waited include the last hour of the morning band.
        s.Clock.Advance(HostWindow);
        s.ForgetNotifications();

        var run = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(1, run.Cancelled);
        var missed = Assert.Single(await s.ReliabilityEventsAsync());
        Assert.Equal(SupplierReliabilityEventKind.NoReplyInTime, missed.Kind);
        Assert.Equal(request.Id, missed.RequestId);
        Assert.False(missed.AsBackup);
        Assert.Equal(-10, (await s.Kit.Reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestCancelledToSupplier, toSupplier.Template);
        Assert.Contains("non hai risposto entro il termine", toSupplier.Content.HtmlBody);
        Assert.Contains("punteggio di affidabilità", toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task AutoCancel_ASupplierThatWasClosedWhileTheRequestWaited_LosesNothing_AndIsNotToldItDid()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestAutoCancel, true);
        // A Sunday morning: the supplier does not work on Sundays. The job is for Monday at 10:00.
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero));
        var request = await s.RequestAsync(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        s.Clock.Advance(HostWindow);
        s.ForgetNotifications();

        var run = await AutoCancel(s).CancelUnansweredAsync();

        // The request still lapses (the host must not wait for ever), but the supplier is not marked down for it.
        Assert.Equal(1, run.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(await s.ReliabilityEventsAsync());
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.Contains("non hai risposto entro il termine", toSupplier.Content.HtmlBody);
        Assert.DoesNotContain("punteggio di affidabilità", toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task AutoCancel_WithTheReliabilityOff_StillCancels_AndDoesNotPromiseLostPoints()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        s.Flags.Set(FeatureFlags.SupplierRequestAutoCancel, true);
        await s.RequestAsync();
        s.Clock.Advance(HostWindow);
        s.ForgetNotifications();
        var off = new SupplierReliabilityService(
            s.Db,
            s.Agenda,
            Options.Create(new Casazen.Core.Options.SupplierRankingOptions { ReliabilityEnabled = false }),
            NullLogger<SupplierReliabilityService>.Instance,
            s.Clock);
        var service = new ServiceRequestAutoCancelService(
            s.Db,
            s.Notifier,
            s.Flags,
            NullLogger<ServiceRequestAutoCancelService>.Instance,
            s.Clock,
            reliability: off,
            requestOptions: Options.Create(s.Kit.Options));

        var run = await service.CancelUnansweredAsync();

        Assert.Equal(1, run.Cancelled);
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.DoesNotContain("punteggio di affidabilità", toSupplier.Content.HtmlBody);
    }

    // ─── A dispute ───

    [Fact]
    public async Task Dispute_OnAServicePayment_IsRecordedOnTheSupplier_OnceEvenIfStripeSendsItAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var opened = await s.OpenedAsync();
        await s.PayAsync(opened);
        var dispute = new ServiceDisputeEvent("evt_dp", "dp_123", opened.IntentId, opened.Intent.ConnectedAccountId, 6_000, "eur", "fraudulent", "needs_response");

        await s.Payments.ApplyDisputeCreatedAsync(dispute);
        await s.Payments.ApplyDisputeCreatedAsync(dispute with { EventId = "evt_dp_again" });

        var events = await s.ReliabilityEventsAsync();
        var recorded = Assert.Single(events, e => e.Kind == SupplierReliabilityEventKind.DisputeOpened);
        Assert.Equal(opened.Request.Id, recorded.RequestId);
        Assert.Equal("dp_123", recorded.Detail);
        // The reply in time (+1) stays, the job (+2) is taken back and the dispute costs 12.
        Assert.Equal(1 - 12, (await s.Kit.Reliability.PointsAsync([s.SupplierOrgId]))[s.SupplierOrgId]);
    }

    [Fact]
    public async Task Dispute_OnAnotherPaymentIntent_IsNotRecordedOnAnySupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.OpenedAsync();

        await s.Payments.ApplyDisputeCreatedAsync(new ServiceDisputeEvent("evt", "dp_1", "pi_booking", "acct_host", 100, "eur", null, null));

        Assert.DoesNotContain(await s.ReliabilityEventsAsync(), e => e.Kind == SupplierReliabilityEventKind.DisputeOpened);
    }

    private static ServiceRequestAutoCancelService AutoCancel(ServiceRequestScenario s) =>
        new(
            s.Db,
            s.Notifier,
            s.Flags,
            NullLogger<ServiceRequestAutoCancelService>.Instance,
            s.Clock,
            reliability: s.Kit.Reliability,
            requestOptions: Options.Create(s.Kit.Options));
}
