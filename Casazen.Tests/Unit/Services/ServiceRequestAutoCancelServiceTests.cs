using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-04, decision D8: the automatic cancellation of the requests nobody answered. The run is behind the flag
/// <c>SupplierRequestAutoCancel</c> (off by default), takes only the new requests past <c>ResponseDueAt</c> that have no
/// proposal, is idempotent, and tells the host and the supplier. The session lock and the xmin check need PostgreSQL and are in
/// <c>ServiceRequestAutoCancelPostgresTests</c>; a refused save is simulated here with an interceptor.
/// </summary>
public class ServiceRequestAutoCancelServiceTests
{
    private static readonly TimeSpan HostWindow = TimeSpan.FromMinutes(120);

    [Fact]
    public async Task CancelUnansweredAsync_FlagOff_DoesNothingEvenForAnOverdueRequest()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(HostWindow + TimeSpan.FromHours(1));
        s.ForgetNotifications();

        var run = await AutoCancel(s, enabled: false).CancelUnansweredAsync();

        Assert.True(run.Disabled);
        Assert.Equal(ServiceRequestAutoCancelRun.FlagOff, run);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task CancelUnansweredAsync_OverdueRequest_IsCancelledByCasaZenWithTheNoResponseReason()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync(ServiceRequestScenario.FridayAt10);
        s.Clock.Advance(HostWindow + TimeSpan.FromMinutes(1));

        var run = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(new ServiceRequestAutoCancelRun(false, false, 1, 0, 0), run);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, saved.CancellationReason);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.CancelledAt);
        Assert.Equal(saved.CancelledAt, saved.UpdatedAt);
        Assert.Null(saved.ResponseDueAt);
    }

    [Fact]
    public async Task CancelUnansweredAsync_OverdueRequest_TellsTheHostAndTheSupplier()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(HostWindow);
        s.ForgetNotifications();

        await AutoCancel(s).CancelUnansweredAsync();

        var emails = s.Emails.Snapshot();
        Assert.Equal(2, emails.Count);
        var toHost = Assert.Single(emails, e => e.To == "host@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestStatusChanged, toHost.Template);
        Assert.Contains("nessuna risposta", toHost.Content.Subject);
        Assert.Contains(ServiceRequestScenario.PropertyName, toHost.Content.Subject);
        var toSupplier = Assert.Single(emails, e => e.To == "supplier@test.com");
        Assert.Equal(EmailTemplates.Names.ServiceRequestCancelledToSupplier, toSupplier.Template);
        // The supplier never learns the property nor the notes of a request it did not take.
        Assert.DoesNotContain(ServiceRequestScenario.PropertyName, toSupplier.Content.Subject + toSupplier.Content.HtmlBody);
        Assert.DoesNotContain(ServiceRequestScenario.HostNotes, toSupplier.Content.Subject + toSupplier.Content.HtmlBody);

        var pushes = s.Pushes;
        Assert.Equal(2, pushes.Count);
        Assert.All(pushes, push => Assert.Equal(PushTypes.ServiceRequestCancelled, push.Payload.Type));
        Assert.Contains(pushes, push => push.Audience == PushAudience.PropertyHosts(s.PropertyId));
        Assert.Contains(pushes, push => push.Audience == PushAudience.SupplierOrg(s.SupplierOrgId));
        Assert.All(pushes, push => Assert.Equal(PushDeliveryKeys.ServiceRequestStatus(request.Id, ServiceRequestStatus.Annullato), push.DeliveryKey));
    }

    [Fact]
    public async Task CancelUnansweredAsync_ExactlyAtTheDeadline_IsCancelledAndOneMinuteBeforeIsNot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(HostWindow - TimeSpan.FromMinutes(1));

        var before = await AutoCancel(s).CancelUnansweredAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(1));
        var atTheDeadline = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(ServiceRequestAutoCancelRun.Empty, before);
        Assert.Equal(1, atTheDeadline.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task CancelUnansweredAsync_ARunRepeated_FindsNothingLeftAndSendsNothingTwice()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.RequestAsync();
        await s.RequestAsync();
        s.Clock.Advance(HostWindow + TimeSpan.FromMinutes(5));
        var service = AutoCancel(s);
        var first = await service.CancelUnansweredAsync();
        var sent = s.Emails.Snapshot().Count;
        var pushed = s.Pushes.Count;

        var second = await service.CancelUnansweredAsync();
        var third = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(2, first.Cancelled);
        Assert.Equal(ServiceRequestAutoCancelRun.Empty, second);
        Assert.Equal(ServiceRequestAutoCancelRun.Empty, third);
        Assert.Equal(sent, s.Emails.Snapshot().Count);
        Assert.Equal(pushed, s.Pushes.Count);
    }

    [Fact]
    public async Task CancelUnansweredAsync_OnlyTheRequestsNobodyAnswered()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var overdue = await s.RequestAsync();
        var taken = await s.RequestAsync();
        await s.Service.TakeAsync(taken.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        var rejected = await s.RequestAsync();
        await s.Service.RejectAsync(rejected.Id, s.SupplierOrgId, "Non posso");
        var withProposal = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            withProposal.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        var cancelledByHost = await s.RequestAsync();
        await s.Service.CancelAsHostAsync(cancelledByHost.Id, s.HostOrgId, "Non serve");
        var completed = await s.SeedAsync(ServiceRequestStatus.Completato, r => r.ResponseDueAt = ServiceRequestScenario.Instant.UtcDateTime);
        var longAgo = await s.SeedAsync(ServiceRequestStatus.Richiesto, r => r.ResponseDueAt = null);
        s.Clock.Advance(HostWindow + TimeSpan.FromDays(1));

        var run = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(1, run.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(overdue.Id)).Status);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(taken.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Rifiutato, (await s.ReadAsync(rejected.Id)).Status);
        // A request with a time proposed was answered by the supplier: the host has to answer, nobody cancels it.
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(withProposal.Id)).Status);
        var byHost = await s.ReadAsync(cancelledByHost.Id);
        Assert.Equal(ServiceRequestActorParty.Host, byHost.CancelledBy);
        Assert.Equal(ServiceRequestStatus.Completato, (await s.ReadAsync(completed.Id)).Status);
        // A request from before the deadlines existed has none: it is never cancelled by time.
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(longAgo.Id)).Status);
    }

    [Fact]
    public async Task CancelUnansweredAsync_AProposalTurnedDown_PutsTheRequestBackInTheRunWithItsOwnDeadline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        await s.Service.ProposeTimeAsync(
            request.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        s.Clock.Advance(HostWindow + TimeSpan.FromMinutes(10));
        Assert.Equal(0, (await AutoCancel(s).CancelUnansweredAsync()).Cancelled);

        await s.Service.RejectProposalAsync(request.Id, s.HostOrgId);
        var run = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(1, run.Cancelled);
    }

    [Fact]
    public async Task CancelUnansweredAsync_OldestDeadlineFirst_AndAtMostTheCapPerRun()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var capPlusOne = ServiceRequestAutoCancelService.MaxPerRun + 1;
        var oldest = Guid.Empty;
        for (var i = 0; i < capPlusOne; i++)
        {
            var seeded = await s.SeedAsync(
                ServiceRequestStatus.Richiesto,
                r => r.ResponseDueAt = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-capPlusOne + i));
            if (i == 0)
                oldest = seeded.Id;
        }

        var first = await AutoCancel(s).CancelUnansweredAsync();
        var second = await AutoCancel(s).CancelUnansweredAsync();
        var third = await AutoCancel(s).CancelUnansweredAsync();

        Assert.Equal(ServiceRequestAutoCancelService.MaxPerRun, first.Cancelled);
        Assert.Equal(1, second.Cancelled);
        Assert.Equal(ServiceRequestAutoCancelRun.Empty, third);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(oldest)).Status);
    }

    [Fact]
    public async Task CancelUnansweredAsync_ARequestThatChangedUnderTheRun_IsCountedAsAConflictAndLeftAsItIs()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var request = await s.RequestAsync();
        s.Clock.Advance(HostWindow);
        s.ForgetNotifications();
        // The save of the run finds the request changed by someone else (xmin): the winner's change stays.
        interceptor.Failure = new DbUpdateConcurrencyException("the request changed");

        var run = await AutoCancel(s).CancelUnansweredAsync();
        interceptor.Failure = null;

        Assert.Equal(new ServiceRequestAutoCancelRun(false, false, 0, 1, 0), run);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(request.Id)).Status);
        Assert.Empty(s.Emails.Snapshot());
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task CancelUnansweredAsync_AFailingRequest_IsCountedAndTheNextOnesAreStillCancelled()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        var first = await s.RequestAsync();
        var second = await s.RequestAsync();
        s.Clock.Advance(HostWindow);
        interceptor.Failure = new InvalidOperationException("the database is down");

        var failed = await AutoCancel(s).CancelUnansweredAsync();
        interceptor.Failure = null;
        var retried = await AutoCancel(s).CancelUnansweredAsync();

        // Every request failed in the first run; the next run (10 minutes later in production) takes them.
        Assert.Equal(2, failed.Failed);
        Assert.Equal(0, failed.Cancelled);
        Assert.Equal(2, retried.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(first.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(second.Id)).Status);
    }

    [Fact]
    public async Task CancelUnansweredAsync_TheNotificationsCannotBeQueued_StillCancels()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var request = await s.RequestAsync();
        s.Clock.Advance(HostWindow);
        using var failing = new ServiceRequestTestKit(s.Db, new ThrowingEmailQueue(), new ThrowingPushQueue(), s.Clock);
        var service = new ServiceRequestAutoCancelService(
            s.Db, failing.Notifier, Flags(enabled: true), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock);

        var run = await service.CancelUnansweredAsync();

        Assert.Equal(1, run.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task CancelUnansweredAsync_ARequestOfAnotherHostAndSupplier_IsHandledToo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        var theirs = await s.SeedAsync(
            ServiceRequestStatus.Richiesto,
            r => r.ResponseDueAt = ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-5),
            supplierOrgId: other);

        var run = await AutoCancel(s).CancelUnansweredAsync();

        // The job works across every org: it is not scoped to one host nor to one supplier.
        Assert.Equal(1, run.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(theirs.Id)).Status);
    }

    // ─── The job ───

    [Fact]
    public async Task Job_RunsTheServiceOnceAndStaysQuietWhenNothingFails()
    {
        var service = new Mock<IServiceRequestAutoCancelService>();
        service.Setup(s => s.CancelUnansweredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ServiceRequestAutoCancelRun(false, false, 3, 1, 0));
        var logger = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob>();

        await new Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob(service.Object, logger).ExecuteAsync(CancellationToken.None);

        service.Verify(s => s.CancelUnansweredAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task Job_WhenSomeRequestsFailed_LogsAWarningWithTheCounts()
    {
        var service = new Mock<IServiceRequestAutoCancelService>();
        service.Setup(s => s.CancelUnansweredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ServiceRequestAutoCancelRun(false, false, 3, 1, 2));
        var logger = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob>();

        await new Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob(service.Object, logger).ExecuteAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("3 cancelled", warning.Message);
        Assert.Contains("2 failed", warning.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Job_FlagOffOrLockTaken_LogsWhyItDidNothing(bool disabled, bool skipped)
    {
        var service = new Mock<IServiceRequestAutoCancelService>();
        service.Setup(s => s.CancelUnansweredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ServiceRequestAutoCancelRun(disabled, skipped, 0, 0, 0));
        var logger = new CapturingLogger<Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob>();

        await new Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob(service.Object, logger).ExecuteAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.Contains(disabled ? "flag is off" : "another run is in progress", entry.Message);
    }

    [Fact]
    public void Job_TheRecurringJobIdAndTheCron_AreTheOnesOfTheRunbook()
    {
        Assert.Equal("service-request-auto-cancel", Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob.RecurringJobId);
        Assert.Equal("*/10 * * * *", Casazen.Web.BackgroundJobs.ServiceRequestAutoCancelJob.Cron);
    }

    // ─── helpers ───

    private static ServiceRequestAutoCancelService AutoCancel(ServiceRequestScenario s, bool enabled = true) =>
        new(s.Db, s.Notifier, Flags(enabled), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock);

    private static IFeatureFlags Flags(bool enabled)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.SupplierRequestAutoCancel)).Returns(enabled);
        return flags.Object;
    }

    private sealed class ThrowingEmailQueue : Casazen.Infrastructure.Email.IEmailQueue
    {
        public bool Enqueue(string? to, Casazen.Infrastructure.Email.EmailContent content, string template) =>
            throw new InvalidOperationException("The email queue is down.");
    }

    private sealed class ThrowingPushQueue : IPushNotificationService
    {
        public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload) =>
            throw new InvalidOperationException("The push queue is down.");
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
