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
/// SP-10: the upkeep that runs every five minutes, always on. It deletes the bookings whose time to check the e-mail has passed
/// (and, a little later, the ones that were checked and kept for the replay of the link), and cancels the requests from a
/// supplier's showcase that nobody answered in time — the supplier, and the customer who has to answer a proposal — telling the
/// customer. It never touches the requests of the hosts, whose cancellation is another job (SP-04), behind its own flag. The
/// session lock and the check of the row version need PostgreSQL (<c>ServiceRequestExpiryPostgresTests</c>).
/// </summary>
public class ServiceRequestExpiryServiceTests
{
    private static readonly TimeSpan SupplierWindow = TimeSpan.FromMinutes(180);
    private static readonly TimeSpan HoldWindow = TimeSpan.FromMinutes(30);

    // ─── The bookings that lapsed ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_DeletesTheHoldsWhoseTimeHasPassed_AndKeepsTheLiveOnes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10, email: "uno@example.com"));
        await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "due@example.com"));
        s.Clock.Advance(HoldWindow + TimeSpan.FromSeconds(1));
        var (live, _) = await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(3), email: "tre@example.com"));

        var run = await Expiry(s).RunAsync();

        Assert.Equal(new ServiceRequestExpiryRun(false, 2, 0, 0, 0), run);
        Assert.Equal(live.Id, Assert.Single(await s.HoldsAsync()).Id);
    }

    [Fact]
    public async Task Run_AHoldAtTheVeryEndOfItsTime_IsDeleted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.HoldAsync();
        s.Clock.Advance(HoldWindow);

        var run = await Expiry(s).RunAsync();

        Assert.Equal(1, run.HoldsDeleted);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task Run_ACheckedBooking_IsKeptForTheReplayOfTheLink_ThenDeleted()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        var early = await Expiry(s).RunAsync();
        // The customer clicks the link again: the replay still works.
        var replay = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);

        s.Clock.Advance(HoldWindow + TimeSpan.FromSeconds(1));
        var late = await Expiry(s).RunAsync();

        Assert.Equal(0, early.HoldsDeleted);
        Assert.True(replay.AlreadyConfirmed);
        Assert.Equal(1, late.HoldsDeleted);
        Assert.Empty(await s.HoldsAsync());
        // The request it made is not touched: only the hold goes.
        Assert.Single(await s.ShowcaseRequestsAsync());
    }

    [Fact]
    public async Task Run_ADeletedHold_NoLongerHoldsOrChecksAnything()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var (hold, token) = await s.HoldAsync();
        s.Clock.Advance(HoldWindow + TimeSpan.FromMinutes(1));
        await Expiry(s).RunAsync();

        await Assert.ThrowsAsync<Casazen.Core.Exceptions.NotFoundException>(
            async () => await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token));
        Assert.Empty(await s.ShowcaseRequestsAsync());
    }

    // ─── The requests nobody answered ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_ARequestTheSupplierDidNotAnswerInTime_IsCancelledBySystem_AndTheCustomerIsTold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.Clock.Advance(SupplierWindow + TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var run = await Expiry(s).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        Assert.Equal(ServiceRequestCancellationReasons.NoResponse, saved.CancellationReason);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, saved.CancelledAt);
        Assert.Null(saved.ResponseDueAt);

        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingExpired, toCustomer.Template);
        Assert.Contains("è scaduta", toCustomer.Content.Subject);
        Assert.Contains("Non hai pagato nulla", toCustomer.Content.HtmlBody);
        // The supplier learns that it lost the request, without the customer's data.
        var toSupplier = Assert.Single(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.DoesNotContain("Rossi", toSupplier.Content.Subject + toSupplier.Content.HtmlBody);
        Assert.DoesNotContain(ShowcaseScenario.Address, toSupplier.Content.Subject + toSupplier.Content.HtmlBody);
    }

    [Fact]
    public async Task Run_TheCancelledRequest_FreesItsSlotForAnotherCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.BookedAsync();
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
        s.Clock.Advance(SupplierWindow + TimeSpan.FromMinutes(1));

        await Expiry(s).RunAsync();

        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
        var next = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(email: "altra.persona@example.com"));
        Assert.NotEqual(Guid.Empty, next.Id);
    }

    [Fact]
    public async Task Run_ARequestStillInTime_OrAlreadyTaken_IsLeftAlone()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (inTime, _) = await s.BookedAsync();
        var (taken, _) = await s.BookedAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "anna.verdi@example.com"));
        await s.Service.TakeAsync(taken.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        s.Clock.Advance(SupplierWindow - TimeSpan.FromMinutes(1));

        var early = await Expiry(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromMinutes(5));
        var late = await Expiry(s).RunAsync();

        Assert.Equal(0, early.RequestsCancelled);
        // Past its deadline: the one nobody answered goes, the taken one has no deadline anymore.
        Assert.Equal(1, late.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(inTime.Id)).Status);
        Assert.Equal(ServiceRequestStatus.PresoInCarico, (await s.ReadAsync(taken.Id)).Status);
    }

    [Fact]
    public async Task Run_AProposalTheCustomerDoesNotAnswerInADay_LapsesTheRequest()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        await s.Service.ProposeTimeAsync(
            request.Id,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(ServiceRequestScenario.FridayAt14));
        s.ForgetNotifications();

        // The deadline of the supplier is spent: the supplier did answer. What is waited for is the customer.
        s.Clock.Advance(SupplierWindow + TimeSpan.FromMinutes(1));
        var whileWaiting = await Expiry(s).RunAsync();
        s.Clock.Advance(TimeSpan.FromHours(24));
        var afterADay = await Expiry(s).RunAsync();

        Assert.Equal(0, whileWaiting.RequestsCancelled);
        Assert.Equal(1, afterADay.RequestsCancelled);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        Assert.Equal(ServiceRequestActorParty.System, saved.CancelledBy);
        Assert.Null(saved.ProposedStartUtc);
        var toCustomer = Assert.Single(s.Emails.Snapshot(), e => e.To == ShowcaseScenario.CustomerEmail);
        Assert.Equal(EmailTemplates.Names.SupplierBookingExpired, toCustomer.Template);
    }

    [Fact]
    public async Task Run_TheShowcaseCancellationIsAlwaysOn_EvenWithTheFlagOfTheHostsOff()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.Clock.Advance(SupplierWindow + TimeSpan.FromMinutes(1));

        // SupplierRequestAutoCancel (decision D8 for the hosts) is off: that is the flag of another job.
        var run = await Expiry(s, hostsFlagOn: false).RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
    }

    [Fact]
    public async Task Run_AndTheJobOfTheHosts_NeverCrossIntoEachOthersRequests()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var host = await s.RequestAsync(ServiceRequestScenario.SaturdayAt09);
        var (showcase, _) = await s.BookedAsync();
        s.Clock.Advance(TimeSpan.FromDays(2));

        var expiry = await Expiry(s).RunAsync();
        var hostsRun = await AutoCancel(s, hostsFlagOn: true).CancelUnansweredAsync();

        // Each job cancelled exactly its own.
        Assert.Equal(1, expiry.RequestsCancelled);
        Assert.Equal(1, hostsRun.Cancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(host.Id)).Status);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(showcase.Id)).Status);
    }

    [Fact]
    public async Task Run_TheJobOfTheHostsAlone_LeavesTheShowcaseRequestToTheExpiryJob()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (showcase, _) = await s.BookedAsync();
        s.Clock.Advance(TimeSpan.FromDays(2));

        var hostsRun = await AutoCancel(s, hostsFlagOn: true).CancelUnansweredAsync();

        Assert.Equal(0, hostsRun.Cancelled);
        Assert.Equal(ServiceRequestStatus.Richiesto, (await s.ReadAsync(showcase.Id)).Status);
    }

    [Fact]
    public async Task Run_Twice_DoesNothingTheSecondTime()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "uno@example.com"));
        await s.BookedAsync();
        s.Clock.Advance(SupplierWindow + TimeSpan.FromMinutes(1));
        var first = await Expiry(s).RunAsync();
        s.ForgetNotifications();

        var second = await Expiry(s).RunAsync();

        // The booking nobody checked and the one that became the request lapse together; the request nobody answered is cancelled.
        Assert.Equal(new ServiceRequestExpiryRun(false, 2, 1, 0, 0), first);
        Assert.Equal(new ServiceRequestExpiryRun(false, 0, 0, 0, 0), second);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task Run_ANotifierThatFails_DoesNotUndoTheCancellation()
    {
        var emails = new ThrowingEmailQueue();
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedAsync();
        s.Clock.Advance(SupplierWindow + TimeSpan.FromMinutes(1));
        using var kit = new ServiceRequestTestKit(s.Db, emails, clock: s.Clock);
        var expiry = new ServiceRequestExpiryService(
            s.Db,
            new ServiceRequestAutoCancelService(s.Db, kit.Notifier, Flags(false), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock),
            NullLogger<ServiceRequestExpiryService>.Instance,
            s.Clock);

        var run = await expiry.RunAsync();

        Assert.Equal(1, run.RequestsCancelled);
        Assert.Equal(ServiceRequestStatus.Annullato, (await s.ReadAsync(request.Id)).Status);
    }

    // ─── helpers ───

    private static ServiceRequestExpiryService Expiry(ServiceRequestScenario s, bool hostsFlagOn = true) =>
        new(s.Db, AutoCancel(s, hostsFlagOn), NullLogger<ServiceRequestExpiryService>.Instance, s.Clock);

    private static ServiceRequestAutoCancelService AutoCancel(ServiceRequestScenario s, bool hostsFlagOn) =>
        new(s.Db, s.Notifier, Flags(hostsFlagOn), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock);

    private static IFeatureFlags Flags(bool hostsFlagOn)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.SupplierRequestAutoCancel)).Returns(hostsFlagOn);
        return flags.Object;
    }

    private sealed class ThrowingEmailQueue : Casazen.Infrastructure.Email.IEmailQueue
    {
        public bool Enqueue(string? to, Casazen.Infrastructure.Email.EmailContent content, string template) =>
            throw new InvalidOperationException("The email queue is down.");
    }
}
