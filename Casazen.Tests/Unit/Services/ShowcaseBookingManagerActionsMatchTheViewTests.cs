using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-11: the page shows what the customer may do (<c>actions</c> of the lookup) and the endpoints enforce it, and the two must not
/// drift apart. Every state a booking can be in is put through the real manager, and each of the four actions is attempted: it is
/// accepted exactly when the lookup offers it, and refused (never half done) when the lookup does not. (A booking the customer has
/// already cancelled is the one exception on purpose: cancelling it again answers the same booking and does nothing, so a retry
/// after a lost answer is safe; it has its own test.)
/// </summary>
public class ShowcaseBookingManagerActionsMatchTheViewTests
{
    private static readonly string[] States =
    [
        "new",
        "new-with-proposal",
        "new-with-lapsed-proposal",
        "new-supplier-suspended",
        "new-with-proposal-supplier-suspended",
        "new-service-paused",
        "taken",
        "taken-time-passed",
        "in-progress",
        "completed",
        "rejected",
        "cancelled-by-supplier",
        "cancelled-by-system",
    ];

    private static readonly string[] Actions = ["cancel", "reschedule", "accept", "reject"];

    public static IEnumerable<object[]> Cases() =>
        States.SelectMany(state => Actions.Select(action => new object[] { state, action }));

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheActionIsAccepted_ExactlyWhenTheLookupOffersIt(string state, string action)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, credentials) = await s.BookedForManagementAsync();
        await PutInStateAsync(s, request, state);

        var offered = (await s.Kit.Manager.LookupAsync(credentials)).Actions;
        var isOffered = action switch
        {
            "cancel" => offered.CanCancel,
            "reschedule" => offered.CanReschedule,
            _ => offered.CanRespondToProposal,
        };

        Task Attempt() => action switch
        {
            "cancel" => s.Kit.Manager.CancelAsync(credentials, null),
            // A week later, at the same hour: a free slot in every state (the clock may have moved by a day).
            "reschedule" => s.Kit.Manager.RescheduleAsync(credentials, ServiceRequestScenario.FridayAt10.AddDays(7)),
            "accept" => s.Kit.Manager.AcceptProposalAsync(credentials),
            _ => s.Kit.Manager.RejectProposalAsync(credentials),
        };

        if (isOffered)
        {
            await Attempt();
        }
        else
        {
            // Refused by the rules (422), by the state machine or as not found: never an action half done.
            await Assert.ThrowsAnyAsync<Exception>(Attempt);
            var stored = await s.ReadAsync(request.Id);
            Assert.NotEqual(ServiceRequestActorParty.Customer, stored.CancelledBy);
        }
    }

    [Fact]
    public async Task TheOnlyExceptionToTheRule_ACancellationTheCustomerAlreadyMade_AnswersTheSameBookingAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (_, credentials) = await s.BookedForManagementAsync();
        await s.Kit.Manager.CancelAsync(credentials, null);

        var again = await s.Kit.Manager.LookupAsync(credentials);
        var cancelledAgain = await s.Kit.Manager.CancelAsync(credentials, null);

        Assert.False(again.Actions.CanCancel);
        Assert.Equal(ServiceRequestStatus.Annullato, cancelledAgain.Status);
        Assert.Equal(again.Cancellation, cancelledAgain.Cancellation);
    }

    private static async Task PutInStateAsync(ServiceRequestScenario s, ServiceRequest request, string state)
    {
        switch (state)
        {
            case "new":
                break;
            case "new-with-proposal":
                await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
                break;
            case "new-with-lapsed-proposal":
                await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
                s.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
                break;
            case "new-supplier-suspended":
                await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
                break;
            case "new-with-proposal-supplier-suspended":
                await s.ProposeAsSupplierAsync(request.Id, ServiceRequestScenario.FridayAt14);
                await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
                break;
            case "new-service-paused":
                var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == s.ListingId);
                listing.Status = SupplierServiceListingStatus.Paused;
                await s.Db.SaveChangesAsync();
                break;
            case "taken":
                await s.TakeAsSupplierAsync(request.Id);
                break;
            case "taken-time-passed":
                await s.TakeAsSupplierAsync(request.Id);
                s.Clock.Advance(TimeSpan.FromHours(23));
                break;
            case "in-progress":
                await SetStatusAsync(s, request.Id, ServiceRequestStatus.InCorso);
                break;
            case "completed":
                await SetStatusAsync(s, request.Id, ServiceRequestStatus.Completato);
                break;
            case "rejected":
                await SetStatusAsync(s, request.Id, ServiceRequestStatus.Rifiutato);
                break;
            case "cancelled-by-supplier":
                await SetStatusAsync(s, request.Id, ServiceRequestStatus.Annullato, ServiceRequestActorParty.Supplier);
                break;
            case "cancelled-by-system":
                await SetStatusAsync(s, request.Id, ServiceRequestStatus.Annullato, ServiceRequestActorParty.System);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    /// <summary>The request is put in a status the way the database would hold it (the states in between are other tests' business).</summary>
    private static async Task SetStatusAsync(
        ServiceRequestScenario s,
        Guid id,
        ServiceRequestStatus status,
        ServiceRequestActorParty? cancelledBy = null)
    {
        var stored = await s.Db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id);
        stored.Status = status;
        stored.ResponseDueAt = null;
        if (cancelledBy is not null)
        {
            stored.CancelledBy = cancelledBy;
            stored.CancelledAt = s.Clock.GetUtcNow().UtcDateTime;
        }

        await s.Db.SaveChangesAsync();
    }
}

/// <summary>
/// SP-11: what the customer's actions leave behind once the change is committed. A failure to write a notification, or a customer who
/// closes the tab, never takes back what was saved, and the instants written are at the precision the database keeps.
/// </summary>
public class ShowcaseBookingManagerAfterTheCommitTests
{
    [Fact]
    public async Task TheNotificationOfACancellation_NeverThrows_WhenTheCustomerCannotBeRead_AndSendsNothingToNobody()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedForManagementAsync();
        s.ForgetNotifications();
        var failing = new Mock<IServiceCustomerReader>();
        failing
            .Setup(reader => reader.FindContactAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is gone"));
        var notifier = new ShowcaseBookingNotifier(
            s.Db,
            failing.Object,
            s.Emails,
            EmailTestHelpers.Links(),
            s.Kit.Push,
            Microsoft.Extensions.Options.Options.Create(s.Kit.ShowcaseOptions),
            NullLogger<ShowcaseBookingNotifier>.Instance);

        await notifier.NotifyCancelledByCustomerAsync(request, CancellationToken.None);

        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task TheCancellation_StaysSavedAndTheSupplierIsTold_EvenIfTheRequestIsAbortedTheMomentItIsSaved()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedForManagementAsync();
        s.ForgetNotifications();
        using var aborted = new CancellationTokenSource();
        // The customer closes the tab the moment the cancellation is saved: the token of the request is cancelled before the notifications.
        interceptor.AfterSave = aborted.Cancel;

        await s.Service.CancelAsCustomerAsync(request.Id, s.SupplierOrgId, null, aborted.Token);

        Assert.True(aborted.IsCancellationRequested);
        var saved = await s.ReadAsync(request.Id);
        Assert.Equal(ServiceRequestStatus.Annullato, saved.Status);
        // The supplier is told all the same, and a retry (which finds it cancelled and does nothing) would never tell it.
        Assert.Contains(s.Emails.Snapshot(), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingCancelledByCustomer);
        Assert.Contains(s.Emails.Snapshot(), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingCancellationReceipt);
    }

    [Fact]
    public async Task TheMoveOfARequest_TellsTheSupplierEvenIfTheRequestIsAbortedTheMomentItIsSaved()
    {
        var interceptor = new FailingSaveInterceptor();
        using var s = await ServiceRequestScenario.CreateAsync(saveInterceptor: interceptor);
        await s.EnableBookingAsync();
        var (request, _) = await s.BookedForManagementAsync();
        s.ForgetNotifications();
        using var aborted = new CancellationTokenSource();
        interceptor.AfterSave = aborted.Cancel;

        await s.Service.RescheduleAsCustomerAsync(request.Id, s.SupplierOrgId, ServiceRequestScenario.FridayAt10.AddDays(7), aborted.Token);

        Assert.True(aborted.IsCancellationRequested);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddDays(7), (await s.ReadAsync(request.Id)).ScheduledStartUtc);
        Assert.Contains(s.Emails.Snapshot(), e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingRescheduledByCustomer);
    }

    [Fact]
    public async Task TheInstantsTheCustomersActionsWrite_AreAtTheMicrosecondThePrecisionOfTheDatabase()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();

        // A clock with ticks beyond the microsecond, which PostgreSQL cannot keep: what is written must already be cut.
        s.Clock.Advance(TimeSpan.FromTicks(7));

        var (cancelled, cancelledCredentials) = await s.BookedForManagementAsync();
        await s.Kit.Manager.CancelAsync(cancelledCredentials, "Cambio programma");
        var savedCancelled = await s.ReadAsync(cancelled.Id);
        Assert.Equal(0, savedCancelled.CancelledAt!.Value.Ticks % 10);

        var (moved, movedCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt14);
        await s.Kit.Manager.RescheduleAsync(movedCredentials, ServiceRequestScenario.FridayAt10.AddDays(7));
        var savedMoved = await s.ReadAsync(moved.Id);
        Assert.Equal(0, savedMoved.ResponseDueAt!.Value.Ticks % 10);

        var (accepted, acceptedCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt10.AddDays(10), "altro@example.com");
        await s.ProposeAsSupplierAsync(accepted.Id, ServiceRequestScenario.FridayAt10.AddDays(10).AddHours(4));
        await s.Kit.Manager.AcceptProposalAsync(acceptedCredentials);
        var savedAccepted = await s.ReadAsync(accepted.Id);
        Assert.Equal(0, savedAccepted.TakenAt!.Value.Ticks % 10);

        var (turnedDown, turnedDownCredentials) = await s.BookedForManagementAsync(ServiceRequestScenario.FridayAt10.AddDays(11), "terzo@example.com");
        await s.ProposeAsSupplierAsync(turnedDown.Id, ServiceRequestScenario.FridayAt10.AddDays(11).AddHours(4));
        await s.Kit.Manager.RejectProposalAsync(turnedDownCredentials);
        var savedTurnedDown = await s.ReadAsync(turnedDown.Id);
        Assert.Equal(0, savedTurnedDown.ResponseDueAt!.Value.Ticks % 10);
    }
}
