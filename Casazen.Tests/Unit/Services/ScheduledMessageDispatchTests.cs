using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F07/US01/T03 (NF-71, ADR-022-R11): the one place that sends a scheduled message. One test for each rule that costs a guest, a host or the trust in the product if it is wrong: a message goes out once whichever
/// of the routine, the sweep and «Send now» reaches it first, the access code never leaves without the verification of the guest, what cannot go out is held back with ONE «To do» or skipped with an alert, and what
/// the channel answers is the state of the row.
/// </summary>
public class ScheduledMessageDispatchTests
{
    private static readonly Guid OrgA = ScheduledMessageRig.OrgA;

    private static DateTime Now => ScheduledMessageRig.Now.UtcDateTime;

    /// <summary>A booking of a property with a template and a routine that holds it, and one planned message due half an hour ago.</summary>
    private static async Task<(Property Property, Booking Booking, MessageTemplate Template, Routine Routine, ScheduledMessage Row)> SeedAsync(
        ScheduledMessageRig rig,
        string body = "Ciao {{guest_name}}, benvenuto a {{property_name}}! (TEST)",
        BookingStatus status = BookingStatus.Confirmed,
        string guestCountry = "IT",
        ScheduledMessageStatus rowStatus = ScheduledMessageStatus.Scheduled,
        DateTime? plannedAt = null,
        string? siteProfileJson = null)
    {
        var property = await rig.SeedPropertyAsync(OrgA, siteProfileJson: siteProfileJson);
        var booking = await rig.SeedBookingAsync(property, status, guestCountry: guestCountry);
        var template = await rig.SeedTemplateAsync(OrgA, body);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var row = await rig.SeedRowAsync(booking, routine, template, rowStatus, plannedAt);
        return (property, booking, template, routine, row);
    }

    private static async Task<ScheduledMessageDispatchResult> DispatchAsync(
        ScheduledMessageRig rig,
        Guid id,
        ScheduledMessageDispatchMode mode = ScheduledMessageDispatchMode.Routine,
        string? userId = null,
        DateTime? now = null)
    {
        await using var db = rig.NewDb();
        return await rig.Dispatcher(db).TryDispatchAsync(id, now ?? Now, mode, userId);
    }

    private static async Task AddPropertyAccessCodeAsync(ScheduledMessageRig rig, Property property)
    {
        await using var db = rig.NewDb();
        db.PropertyAccessCodes.Add(new PropertyAccessCode { OrgId = property.OrgId, PropertyId = property.Id, EncryptedCode = "1234" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task APlannedMessageGoesOutAtItsTime_ThroughTheChannel_WithTheKeyOfTheRow_AsAnAutomaticMessage()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, _, _, row) = await SeedAsync(rig);

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, result.Outcome);
        var sent = Assert.Single(rig.Sent);
        Assert.Equal("Ciao Giulia, benvenuto a Casa Rosa (TEST)! (TEST)", sent.Text);
        Assert.Equal($"sm-{row.Id:N}", sent.Key);
        Assert.Equal(MessageOrigin.Automation, sent.Origin);
        Assert.Null(sent.UserId);
        Assert.Equal(OrgA, sent.Scope.OrgId);
        Assert.True(sent.Scope.IsOrgWide);

        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Sent, saved.Status);
        Assert.Equal(Now, saved.SentAt);
        Assert.Equal($"direct:{booking.Id:D}", saved.SentThreadKey);
        Assert.False(saved.OutsideHours);
        Assert.Null(saved.LastCheckedAt);
        Assert.Equal(1, saved.Attempts);
    }

    /// <summary>
    /// PROOF TEST: data rule (no duplicate message to a guest). The routine that fires, the sweep and «Send now» arriving at one row reach the guest ONCE, whichever comes first: one after another, the second and the
    /// third find the message sent and do nothing; all three at the same moment, the message layer finds the key of the row and writes the message once.
    /// </summary>
    [Fact]
    public async Task ScheduledMessage_ConcurrentTriggers_SendOnce_Test()
    {
        var sequential = new ScheduledMessageRig();
        var (_, _, _, _, first) = await SeedAsync(sequential);

        var routine = await DispatchAsync(sequential, first.Id, ScheduledMessageDispatchMode.Routine);
        var sweep = await DispatchAsync(sequential, first.Id, ScheduledMessageDispatchMode.Sweep);
        var sendNow = await DispatchAsync(sequential, first.Id, ScheduledMessageDispatchMode.SendNow, "auth0|owner");

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, routine.Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.NothingToDo, sweep.Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.NothingToDo, sendNow.Outcome);
        Assert.Single(sequential.Sent);
        Assert.Equal(1, sequential.Delivered);

        // While the first send is under way (the row is claimed, the channel has not answered) the sweep and «Send now» arrive: both find the row held and send nothing.
        var racing = new ScheduledMessageRig();
        var (_, _, _, _, second) = await SeedAsync(racing);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        racing.SendGate = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var underWay = DispatchAsync(racing, second.Id, ScheduledMessageDispatchMode.Routine);
        await entered.Task;

        var meanwhileSweep = await DispatchAsync(racing, second.Id, ScheduledMessageDispatchMode.Sweep);
        var meanwhileSendNow = await DispatchAsync(racing, second.Id, ScheduledMessageDispatchMode.SendNow, "auth0|owner");
        release.SetResult();
        var underWayResult = await underWay;

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, underWayResult.Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.InProgress, meanwhileSweep.Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.InProgress, meanwhileSendNow.Outcome);
        Assert.Single(racing.Sent);
        Assert.Equal(1, racing.Delivered);
        Assert.Equal(ScheduledMessageStatus.Sent, (await racing.RowAsync(second.Id)).Status);
    }

    /// <summary>
    /// A claim that lapsed (a process that died after it wrote the message and before it saved the row) cannot write the message twice: the next actor sends with the same key, and the message layer finds
    /// the message it has.
    /// </summary>
    [Fact]
    public async Task AMessageThatWasWrittenWhenTheClaimLapsed_IsNotWrittenAgain_ByTheKeyOfTheRow()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, _, row) = await SeedAsync(rig);
        await DispatchAsync(rig, row.Id);

        // The row is back as it was before the save (the process died): claimed long ago, planned, not sent.
        await using (var db = rig.NewDb())
        {
            var undone = await db.ScheduledMessages.SingleAsync(m => m.Id == row.Id);
            undone.Status = ScheduledMessageStatus.Scheduled;
            undone.SentAt = null;
            undone.LastCheckedAt = Now.AddMinutes(-10);
            await db.SaveChangesAsync();
        }

        var again = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.Sweep);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, again.Outcome);
        Assert.Equal(1, rig.Delivered);
        Assert.All(rig.Sent, s => Assert.Equal($"sm-{row.Id:N}", s.Key));
    }

    [Fact]
    public async Task ARowHeldByASendUnderWay_IsNotSentASecondTime_UntilTheClaimLapses()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, _, row) = await SeedAsync(rig);
        await using (var db = rig.NewDb())
        {
            var held = await db.ScheduledMessages.SingleAsync(m => m.Id == row.Id);
            held.LastCheckedAt = Now.AddSeconds(-30);
            await db.SaveChangesAsync();
        }

        var inProgress = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.SendNow, "auth0|owner");
        Assert.Equal(ScheduledMessageDispatchOutcome.InProgress, inProgress.Outcome);
        Assert.Empty(rig.Sent);

        // A claim older than the lease is the one of a process that died: the message goes, and the message layer still finds the key if it had been written.
        var later = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.Sweep, now: Now.Add(ScheduledMessageRules.ClaimLease));
        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, later.Outcome);
        Assert.Single(rig.Sent);
    }

    /// <summary>
    /// PROOF TEST: privacy rule (ADR-022-R11). A message with <c>{{access_code}}</c> is never sent while the access code service answers null (the guest is not verified): the row is blocked, nothing leaves, ONE
    /// «To do» is raised and shared by the other blocked message of the booking, the hosts are told once; once the guest is verified the message goes with the code, and the «To do» closes when the last one is sent.
    /// </summary>
    [Fact]
    public async Task ScheduledMessage_AccessCode_NeverSentWithoutVerification_Test()
    {
        var rig = new ScheduledMessageRig { AccessCode = null };
        var (property, booking, template, _, row) = await SeedAsync(rig, "Il codice è {{access_code}} (TEST)");
        await AddPropertyAccessCodeAsync(rig, property);
        var otherRoutine = await rig.SeedRoutineAsync(OrgA, template, eventConfig: """{"days":2}""");
        var other = await rig.SeedRowAsync(booking, otherRoutine, template);

        var first = await DispatchAsync(rig, row.Id);
        var second = await DispatchAsync(rig, other.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Blocked, first.Outcome);
        Assert.Equal(ScheduledMessageReasons.IdNotVerified, first.Code);
        Assert.Equal(ScheduledMessageDispatchOutcome.Blocked, second.Outcome);
        Assert.Empty(rig.Sent);
        Assert.Equal(0, rig.Delivered);
        Assert.Equal(ScheduledMessageStatus.Blocked, (await rig.RowAsync(row.Id)).Status);
        Assert.Equal(ScheduledMessageReasons.IdNotVerified, (await rig.RowAsync(row.Id)).BlockedReason);
        var todo = Assert.Single(await rig.OpenTodosAsync(booking.Id));
        Assert.Equal(ActivityAction.FixScheduledMessage, todo.Action);
        Assert.Equal(nameof(ActivityType.ScheduledMessageIssue), todo.ActivityType);
        Assert.Equal(todo.Id, (await rig.RowAsync(row.Id)).ActivityId);
        Assert.Equal(todo.Id, (await rig.RowAsync(other.Id)).ActivityId);

        // The hosts were told once for each message: the bell of the owner and of the manager, and the push of the one who chose it, with the route of the booking and nothing of the message.
        Assert.Equal(4, (await rig.BellAsync()).Count);
        Assert.Equal(2, rig.Push.Queued.Count);
        Assert.All(
            rig.Push.Queued,
            p =>
            {
                Assert.Equal(PushTypes.ScheduledMessageIssue, p.Payload.Type);
                Assert.Equal(booking.Id, p.Payload.BookingId);
                Assert.Equal(PushRoutes.Booking(booking.Id), p.Payload.Route);
                Assert.Equal(ScheduledMessageRig.Manager, p.Audience.UserId);
                Assert.DoesNotContain("4321", p.Payload.Body, StringComparison.Ordinal);
            });

        // A retry of the sweep that still finds the guest not verified raises nothing more.
        await using (var db = rig.NewDb())
            await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(20));
        Assert.Single(await rig.OpenTodosAsync(booking.Id));
        Assert.Equal(4, (await rig.BellAsync()).Count);

        // The guest is verified: the message goes with the code, through the channel it uses, and the «To do» stays until the last blocked message is out.
        rig.AccessCode = "4321";
        await using (var db = rig.NewDb())
            await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(40));
        Assert.All(rig.Released, channel => Assert.Equal(AccessCodeReleaseChannels.GuestArea, channel));
        Assert.All(rig.Sent, s => Assert.Equal("Il codice è 4321 (TEST)", s.Text));
        Assert.Equal(2, rig.Delivered);
        Assert.Empty(await rig.OpenTodosAsync(booking.Id));
        Assert.All(await rig.RowsAsync(booking.Id), r => Assert.Equal(ScheduledMessageStatus.Sent, r.Status));
    }

    [Fact]
    public async Task APropertyWithNoCodeSet_BlocksTheMessageAsAMissingVariable_NotAsAGuestNotVerified()
    {
        var rig = new ScheduledMessageRig { AccessCode = "4321" };
        var (_, _, _, _, row) = await SeedAsync(rig, "Il codice è {{access_code}} (TEST)");

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Blocked, result.Outcome);
        Assert.Equal("variable_missing:access_code", result.Code);
        Assert.Empty(rig.Released);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public async Task AVariableTheBookingHasNoValueFor_HoldsTheMessageBack_WithOneTodoAndOneAlert_AndTheSweepSendsItOnceTheDataIsThere()
    {
        var rig = new ScheduledMessageRig();
        var (property, booking, _, _, row) = await SeedAsync(rig, "Come arrivare: {{check_in_method}} (TEST)");

        var blocked = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Blocked, blocked.Outcome);
        Assert.Equal("variable_missing:check_in_method", blocked.Code);
        Assert.Empty(rig.Sent);
        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Blocked, saved.Status);
        Assert.Equal(row.PlannedAtUtc, saved.PlannedAtUtc);
        Assert.Single(await rig.OpenTodosAsync(booking.Id));
        Assert.Equal(2, (await rig.BellAsync()).Count);

        // The sweep tries again only after BlockedRetryMinutes: five minutes later nothing is looked at.
        await using (var early = rig.NewDb())
            Assert.Equal(0, await rig.Dispatcher(early).SweepAsync(Now.AddMinutes(5)));

        await using (var db = rig.NewDb())
        {
            var tracked = await db.Properties.SingleAsync(p => p.Id == property.Id);
            tracked.SiteProfileJson = """{"checkInMethod":{"it":"ritiro delle chiavi nella cassetta (TEST)"}}""";
            await db.SaveChangesAsync();
        }

        await using (var db = rig.NewDb())
            Assert.Equal(1, await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(20)));

        Assert.Equal("Come arrivare: ritiro delle chiavi nella cassetta (TEST) (TEST)", Assert.Single(rig.Sent).Text);
        Assert.Equal(ScheduledMessageStatus.Sent, (await rig.RowAsync(row.Id)).Status);
        Assert.Empty(await rig.OpenTodosAsync(booking.Id));
        Assert.Equal(2, (await rig.BellAsync()).Count);
    }

    /// <summary>
    /// A «To do» that could not be raised (the database hiccuped: the failure is logged and the row says why it is held back) is raised at the next pass of the sweep, and only then: a blocked message with no
    /// «To do» linked never stays so, and the alert to the hosts is not repeated at every pass.
    /// </summary>
    [Fact]
    public async Task ABlockedMessageWhoseTodoCouldNotBeRaised_GetsItAtTheNextPass_WithoutAnotherAlert()
    {
        var rig = new ScheduledMessageRig();
        var raised = Guid.NewGuid();
        var todos = new Mock<IInternalActivityService>();
        todos
            .SetupSequence(t => t.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database hiccuped"))
            .ReturnsAsync(raised);
        rig.ActivitiesDouble = todos.Object;
        var (_, _, _, _, row) = await SeedAsync(rig, "Come arrivare: {{check_in_method}} (TEST)");

        var first = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Blocked, first.Outcome);
        Assert.Null((await rig.RowAsync(row.Id)).ActivityId);
        Assert.Equal(2, (await rig.BellAsync()).Count);

        await using (var db = rig.NewDb())
            await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(20));
        Assert.Equal(raised, (await rig.RowAsync(row.Id)).ActivityId);
        Assert.Equal(2, (await rig.BellAsync()).Count);

        await using (var db = rig.NewDb())
            await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(40));
        todos.Verify(t => t.CreateAsync(It.IsAny<CreateTodoRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(2, (await rig.BellAsync()).Count);
    }

    [Fact]
    public async Task TheDatesAndTheTimesOfTheStay_AreWrittenInTheLanguageOfTheGuest_AsTheComposerWritesThem()
    {
        var italian = new ScheduledMessageRig();
        var (_, _, _, _, italianRow) = await SeedAsync(italian, "Arrivo il {{check_in_date}} dalle {{check_in_time}}, partenza il {{check_out_date}} entro le {{check_out_time}} (TEST)");
        await DispatchAsync(italian, italianRow.Id);
        Assert.Equal("Arrivo il 20 novembre 2026 dalle 15:00, partenza il 23 novembre 2026 entro le 10:00 (TEST)", Assert.Single(italian.Sent).Text);

        var english = new ScheduledMessageRig();
        var (_, _, _, _, englishRow) = await SeedAsync(english, "Arrivo il {{check_in_date}} (TEST)", guestCountry: "GB");
        await english.SeedTemplateAsync(OrgA, "Arrival on {{check_in_date}} (TEST)", language: "en");
        await DispatchAsync(english, englishRow.Id);
        Assert.Equal("Arrival on 20 November 2026 (TEST)", Assert.Single(english.Sent).Text);
    }

    [Fact]
    public async Task TheCheckInLink_IsIssuedAtTheMomentOfTheSend_Once_AndNeverInAPreview()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, _, _, row) = await SeedAsync(rig, "Check-in: {{check_in_link}} (TEST)");

        // The timeline shows the placeholder and issues nothing.
        await using (var db = rig.NewDb())
        {
            var view = Assert.Single(await rig.Service(db).ListForBookingAsync(new HostScope(OrgA), booking.Id));
            Assert.Equal("Check-in: [check_in_link] (TEST)", view.Text);
            Assert.Empty(view.MissingVariables);
        }

        rig.CheckIn.Verify(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Empty(rig.Released);

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, result.Outcome);
        var text = Assert.Single(rig.Sent).Text!;
        Assert.StartsWith("Check-in: https://", text, StringComparison.Ordinal);
        Assert.EndsWith("/checkin/token-1 (TEST)", text, StringComparison.Ordinal);
        rig.CheckIn.Verify(c => c.IssueLinkAsync(booking.Id, OrgA), Times.Once);
    }

    [Fact]
    public async Task TheTextIsTheTemplateInTheLanguageOfTheGuest_ElseItalian()
    {
        var german = new ScheduledMessageRig();
        var (_, _, _, _, germanRow) = await SeedAsync(german, "Ciao {{guest_name}}! (TEST)", guestCountry: "DE");
        await german.SeedTemplateAsync(OrgA, "Hallo {{guest_name}}! (TEST)", language: "de");
        await DispatchAsync(german, germanRow.Id);
        Assert.Equal("Hallo Giulia! (TEST)", Assert.Single(german.Sent).Text);

        // A guest of a country whose language has no template gets the Italian one.
        var french = new ScheduledMessageRig();
        var (_, _, _, _, frenchRow) = await SeedAsync(french, "Ciao {{guest_name}}! (TEST)", guestCountry: "FR");
        await DispatchAsync(french, frenchRow.Id);
        Assert.Equal("Ciao Giulia! (TEST)", Assert.Single(french.Sent).Text);
    }

    [Fact]
    public async Task TheEditOfTheHostWinsOverTheTemplate_AndItsVariablesAreFilled()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template, _, _) = await SeedAsync(rig, "Ciao {{guest_name}}! (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template, eventConfig: """{"days":3}""");
        var row = await rig.SeedRowAsync(booking, routine, template, textOverride: "Solo per te, {{guest_name}} (TEST)");

        await DispatchAsync(rig, row.Id);

        Assert.Equal("Solo per te, Giulia (TEST)", Assert.Single(rig.Sent).Text);
    }

    [Fact]
    public async Task ARefusalOfTheRulesOfTheChannel_IsASkipOfTheSystem_ToldToEveryoneWhoReachesTheProperty()
    {
        var rig = new ScheduledMessageRig { SendThrows = new DomainRuleException(MessageChannelRules.ContactDetailsCode, "MessageChannelRuleViolation") };
        var (_, booking, _, _, row) = await SeedAsync(rig);

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Skipped, result.Outcome);
        Assert.Equal(ScheduledMessageReasons.ChannelRule, result.Code);
        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Skipped, saved.Status);
        Assert.Equal(ScheduledMessageReasons.ChannelRule, saved.SkipReason);
        Assert.Null(saved.SkippedByUserId);
        Assert.Equal(2, (await rig.BellAsync()).Count);
        Assert.Equal(ScheduledMessageRig.Manager, Assert.Single(rig.Push.Queued).Audience.UserId);
        Assert.Empty(await rig.OpenTodosAsync(booking.Id));
        var context = Assert.Single(rig.Contexts);
        Assert.Equal(NotificationEventType.ScheduledMessageIssue, context.EventType);
        Assert.Equal(row.PropertyId, context.PropertyId);
    }

    [Theory]
    [InlineData("mapping_not_confirmed", ScheduledMessageReasons.MappingNotConfirmed)]
    [InlineData("contact_info_unavailable", ScheduledMessageReasons.ContactUnavailable)]
    [InlineData("no_contact_channel", ScheduledMessageReasons.ContactUnavailable)]
    [InlineData("thread_closed", ScheduledMessageReasons.ContactUnavailable)]
    public async Task AGuestTheChannelCannotReach_IsASkipOfTheSystem_WithTheReasonOfTheRefusal(string code, string reason)
    {
        var rig = new ScheduledMessageRig { SendThrows = new DomainConflictException(code, "AnyKey") };
        var (_, _, _, _, row) = await SeedAsync(rig);

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Skipped, result.Outcome);
        Assert.Equal(reason, (await rig.RowAsync(row.Id)).SkipReason);
    }

    [Fact]
    public async Task WithNoWayToTheGuest_NothingIsSent_AndTheRowIsSkippedBeforeTheCodeIsReleasedOrALinkIssued()
    {
        var rig = new ScheduledMessageRig
        {
            AccessCode = "4321",
            Channel = new MessageContactOptions(MessageContactChannel.None, null, MessageContactOptions.NoConversationReason, false, false),
        };
        var (property, _, _, _, row) = await SeedAsync(rig, "Il codice è {{access_code}} e il link {{check_in_link}} (TEST)");
        await AddPropertyAccessCodeAsync(rig, property);

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Skipped, result.Outcome);
        Assert.Equal(ScheduledMessageReasons.ContactUnavailable, result.Code);
        Assert.Empty(rig.Sent);
        Assert.Empty(rig.Released);
        rig.CheckIn.Verify(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task AMessageSentOutsideTheSendWindow_StillGoes_IsFlagged_AndTheOthersAreTold_NotThePersonWhoAskedForIt()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, _, row) = await SeedAsync(rig);
        var night = new DateTime(2026, 11, 18, 21, 30, 0, DateTimeKind.Utc); // 22:30 Rome

        var result = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.SendNow, ScheduledMessageRig.Owner, night);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, result.Outcome);
        Assert.Single(rig.Sent);
        var saved = await rig.RowAsync(row.Id);
        Assert.True(saved.OutsideHours);
        var bell = Assert.Single(await rig.BellAsync());
        Assert.Equal(ScheduledMessageRig.Manager, bell.UserId);
    }

    [Fact]
    public async Task ARoutineThatFiresBeforeTheTimeOfTheMessage_LeavesItToTheSweep()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, _, row) = await SeedAsync(rig, plannedAt: Now.AddHours(10));

        var result = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.Routine);

        Assert.Equal(ScheduledMessageDispatchOutcome.NotDue, result.Outcome);
        Assert.Empty(rig.Sent);
        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(0, saved.Attempts);
        Assert.Null(saved.LastCheckedAt);
    }

    [Fact]
    public async Task AMessageTheChannelRefused_Fails_AndTheNextTryUsesTheNextKey_UntilTheTriesAreUsed()
    {
        var threadId = Guid.NewGuid();
        var rig = new ScheduledMessageRig { Channel = new MessageContactOptions(MessageContactChannel.Ota, MessageThreadKey.ForOta(threadId), null, false, false) };
        var (_, booking, _, _, row) = await SeedAsync(rig);
        rig.Refuse($"sm-{row.Id:N}");

        var failed = await DispatchAsync(rig, row.Id);
        Assert.Equal(ScheduledMessageDispatchOutcome.Failed, failed.Outcome);
        Assert.Equal("octorate_request_rejected", failed.Code);
        Assert.Equal(ScheduledMessageStatus.Failed, (await rig.RowAsync(row.Id)).Status);
        Assert.Empty(await rig.OpenTodosAsync(booking.Id));

        // The message layer wrote the refused message (state Error): the key of the next attempt goes out.
        await using (var db = rig.NewDb())
        {
            db.OtaMessages.Add(new OtaMessage { OrgId = OrgA, ThreadId = threadId, Direction = OtaMessageDirection.Outbound, Text = "x", IdempotencyKey = $"sm-{row.Id:N}", DeliveryState = OtaMessageDeliveryState.Error });
            await db.SaveChangesAsync();
        }

        await using (var db = rig.NewDb())
            Assert.Equal(1, await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(20)));

        Assert.Equal(new[] { $"sm-{row.Id:N}", $"sm-{row.Id:N}-2" }, rig.Sent.Select(s => s.Key));
        var sent = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Sent, sent.Status);
        Assert.Equal(2, sent.Attempts);
    }

    /// <summary>
    /// What is counted is the tries that reached the channel, not the passes of the sweep over a message that is blocked: a message that waited for days has all its tries when it goes out, and a channel that
    /// fails twice does not make it «failed for good» (the «To do» and the alert of a message the tries of which are used come after the fourth).
    /// </summary>
    [Fact]
    public async Task AMessageThatWaitedBlockedForDays_HasAllItsTriesLeft_WhenItGoesOut()
    {
        var threadId = Guid.NewGuid();
        var rig = new ScheduledMessageRig { Channel = new MessageContactOptions(MessageContactChannel.Ota, MessageThreadKey.ForOta(threadId), null, false, false) };
        var (property, _, _, _, row) = await SeedAsync(rig, "Come arrivare: {{check_in_method}} (TEST)");
        await DispatchAsync(rig, row.Id);
        for (var pass = 1; pass <= 6; pass++)
        {
            await using var db = rig.NewDb();
            await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(20 * pass));
        }

        Assert.Equal((ScheduledMessageStatus.Blocked, 0), ((await rig.RowAsync(row.Id)).Status, (await rig.RowAsync(row.Id)).Attempts));

        // The house gets its method, and the channel fails (the same refusal twice: no message of the layer is stored, so the key is the same).
        await using (var db = rig.NewDb())
        {
            (await db.Properties.SingleAsync(p => p.Id == property.Id)).SiteProfileJson = """{"checkInMethod":{"it":"ritiro delle chiavi (TEST)"}}""";
            await db.SaveChangesAsync();
        }

        rig.Refuse($"sm-{row.Id:N}");
        await using (var db = rig.NewDb())
            Assert.Equal(1, await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(200)));
        var failed = await rig.RowAsync(row.Id);
        Assert.Equal((ScheduledMessageStatus.Failed, 1), (failed.Status, failed.Attempts));

        // Failed once is not failed for good: the sweep tries it again, and nobody is told that the tries are used.
        await using (var db = rig.NewDb())
            Assert.Equal(1, await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(220)));
        var again = await rig.RowAsync(row.Id);
        Assert.Equal((ScheduledMessageStatus.Failed, 2), (again.Status, again.Attempts));
        Assert.Equal(2, (await rig.BellAsync()).Count);
    }

    [Fact]
    public async Task AnOutcomeNobodyKnows_IsASentMessage_ThatIsNeverSentAgain()
    {
        var rig = new ScheduledMessageRig { State = OtaMessageDeliveryState.ManualVerify };
        var (_, _, _, _, row) = await SeedAsync(rig);

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, result.Outcome);
        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Sent, saved.Status);
        Assert.Equal("manual_verify", saved.BlockedReason);
        Assert.Equal(ScheduledMessageDispatchOutcome.NothingToDo, (await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.SendNow, "auth0|owner")).Outcome);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task AFailureNobodyExpected_IsRecordedOnTheRow_AndThrown()
    {
        var rig = new ScheduledMessageRig { SendThrows = new InvalidOperationException("boom") };
        var (_, _, _, _, row) = await SeedAsync(rig);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DispatchAsync(rig, row.Id));

        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Failed, saved.Status);
        Assert.Equal(ScheduledMessageDispatcher.UnexpectedErrorCode, saved.BlockedReason);
        Assert.Null(saved.LastCheckedAt);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Pending)]
    public async Task ABookingThatDoesNotHappen_CancelsTheMessage_AndSendsNothing(BookingStatus status)
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, _, row) = await SeedAsync(rig, status: status);

        var result = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.SendNow, "auth0|owner");

        Assert.Equal(ScheduledMessageDispatchOutcome.Cancelled, result.Outcome);
        Assert.Empty(rig.Sent);
        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Cancelled, saved.Status);
        Assert.Equal(ScheduledMessageReasons.BookingCancelled, saved.SkipReason);
    }

    [Fact]
    public async Task ARoutineSwitchedOffOrInTestMode_CancelsTheMessage_AndSendsNothing()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, routine, row) = await SeedAsync(rig);
        await using (var db = rig.NewDb())
        {
            (await db.Routines.SingleAsync(r => r.Id == routine.Id)).Active = false;
            await db.SaveChangesAsync();
        }

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Cancelled, result.Outcome);
        Assert.Equal(ScheduledMessageReasons.RoutineInactive, (await rig.RowAsync(row.Id)).SkipReason);
        Assert.Empty(rig.Sent);
    }

    [Theory]
    [InlineData(FeatureFlags.ScheduledMessages)]
    [InlineData(FeatureFlags.Routines)]
    public async Task WithTheModuleOfTheMessagesOrOfTheRoutinesClosed_NothingIsSent_ByAnyActor(string flag)
    {
        var rig = new ScheduledMessageRig();
        rig.Flags.Set(flag, false);
        var (_, _, _, _, row) = await SeedAsync(rig);

        Assert.Equal(ScheduledMessageDispatchOutcome.NothingToDo, (await DispatchAsync(rig, row.Id)).Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.NothingToDo, (await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.SendNow, "auth0|owner")).Outcome);
        await using (var db = rig.NewDb())
            Assert.Equal(0, await rig.Dispatcher(db).SweepAsync(Now.AddDays(1)));

        Assert.Empty(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Scheduled, (await rig.RowAsync(row.Id)).Status);
    }

    [Fact]
    public async Task AHeldMessageWhoseStayMovedToLater_WaitsForItsNewTime_AndOnlyTheHostCanSendItBefore()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, _, _, row) = await SeedAsync(rig, rowStatus: ScheduledMessageStatus.Blocked, plannedAt: Now.AddDays(3));

        // The data is there, but the time has not come: the sweep does not even look at the row, and a try by the sweep or by the routine says «not yet».
        await using (var db = rig.NewDb())
            Assert.Equal(0, await rig.Dispatcher(db).SweepAsync(Now.AddHours(1)));
        Assert.Equal(ScheduledMessageDispatchOutcome.NotDue, (await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.Sweep, now: Now.AddHours(1))).Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.NotDue, (await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.Routine, now: Now.AddHours(1))).Outcome);
        Assert.Empty(rig.Sent);

        // «Send now» is the decision of the host, whatever the time.
        var sendNow = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.SendNow, ScheduledMessageRig.Owner);
        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, sendNow.Outcome);
        Assert.Single(rig.Sent);
    }

    private const string StayOfMoreThanThreeNights = """{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"stayDuration","operator":"greaterThan","value":3}]}]}""";

    private const string StayOfMoreThanTwoNights = """{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"stayDuration","operator":"greaterThan","value":2}]}]}""";

    /// <summary>
    /// A routine «only for stays of more than three nights» does not send its message to a stay of three, whoever tries (the sweep reads the condition as the engine reads it): the row is cancelled with
    /// its reason. A stay that meets the condition of its routine gets the message. «Send now» is the host's own decision: the condition is not asked.
    /// </summary>
    [Fact]
    public async Task TheConditionOfTheRoutine_IsReadAsTheEngineReadsIt_AndAStayThatDoesNotMeetItGetsNothing()
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var template = await rig.SeedTemplateAsync(OrgA, "Ciao {{guest_name}}! (TEST)");
        var strict = await rig.SeedRoutineAsync(OrgA, template, conditionJson: StayOfMoreThanThreeNights);
        var loose = await rig.SeedRoutineAsync(OrgA, template, eventConfig: """{"days":2}""", conditionJson: StayOfMoreThanTwoNights);
        var booking = await rig.SeedBookingAsync(property);
        var refused = await rig.SeedRowAsync(booking, strict, template);
        var accepted = await rig.SeedRowAsync(booking, loose, template);
        var forced = await rig.SeedRowAsync(await rig.SeedBookingAsync(property), strict, template);

        var no = await DispatchAsync(rig, refused.Id, ScheduledMessageDispatchMode.Sweep);
        var yes = await DispatchAsync(rig, accepted.Id, ScheduledMessageDispatchMode.Sweep);
        var host = await DispatchAsync(rig, forced.Id, ScheduledMessageDispatchMode.SendNow, ScheduledMessageRig.Owner);

        Assert.Equal(ScheduledMessageDispatchOutcome.Cancelled, no.Outcome);
        var saved = await rig.RowAsync(refused.Id);
        Assert.Equal(ScheduledMessageStatus.Cancelled, saved.Status);
        Assert.Equal(ScheduledMessageReasons.ConditionNotMet, saved.SkipReason);
        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, yes.Outcome);
        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, host.Outcome);
        Assert.Equal(2, rig.Sent.Count);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"guestLanguage","operator":"equals","value":"it"}]}]}""")]
    [InlineData("""{"conditions":[]}""")]
    public async Task ACondition_NobodyCanRead_IsNeverAReasonToSend(string conditionJson)
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var template = await rig.SeedTemplateAsync(OrgA, "Ciao {{guest_name}}! (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template, conditionJson: conditionJson);
        var row = await rig.SeedRowAsync(await rig.SeedBookingAsync(property), routine, template);

        var result = await DispatchAsync(rig, row.Id, ScheduledMessageDispatchMode.Sweep);

        Assert.Equal(ScheduledMessageDispatchOutcome.Cancelled, result.Outcome);
        Assert.Empty(rig.Sent);
        Assert.Equal(ScheduledMessageReasons.ConditionNotMet, (await rig.RowAsync(row.Id)).SkipReason);
    }

    [Fact]
    public async Task TheSweep_SendsWhatIsOverdueByMoreThanTheCatchUp_NeverWhatIsLateByMoreThanADay_AndEndsWhatIsBlockedAfterTheCheckOut()
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var template = await rig.SeedTemplateAsync(OrgA, "Ciao {{guest_name}}! (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var overdue = await rig.SeedRowAsync(await rig.SeedBookingAsync(property), routine, template, plannedAt: Now.AddMinutes(-20));
        var notYet = await rig.SeedRowAsync(await rig.SeedBookingAsync(property), routine, template, plannedAt: Now.AddMinutes(-10));
        var tooLate = await rig.SeedRowAsync(await rig.SeedBookingAsync(property), routine, template, plannedAt: Now.AddDays(-2));
        var pastStay = await rig.SeedBookingAsync(property, checkIn: Now.AddDays(-10));
        var blocked = await rig.SeedRowAsync(pastStay, routine, template, ScheduledMessageStatus.Blocked, Now.AddDays(-9), blockedReason: "variable_missing:check_in_method", updatedAt: Now.AddDays(-9));

        await using (var db = rig.NewDb())
            await rig.Dispatcher(db).SweepAsync(Now);

        Assert.Equal(ScheduledMessageStatus.Sent, (await rig.RowAsync(overdue.Id)).Status);
        Assert.Equal(ScheduledMessageStatus.Scheduled, (await rig.RowAsync(notYet.Id)).Status);
        var late = await rig.RowAsync(tooLate.Id);
        Assert.Equal(ScheduledMessageStatus.Expired, late.Status);
        Assert.Equal(ScheduledMessageReasons.Missed, late.SkipReason);
        var ended = await rig.RowAsync(blocked.Id);
        Assert.Equal(ScheduledMessageStatus.Expired, ended.Status);
        Assert.Equal(ScheduledMessageReasons.CheckOutReached, ended.SkipReason);
        Assert.Single(rig.Sent);
    }

    [Fact]
    public async Task AMessageThatAlreadyHasItsMessageInTheConversation_IsOnlyMarkedSent_NothingIsIssuedReleasedOrSentAgain()
    {
        var threadId = Guid.NewGuid();
        var rig = new ScheduledMessageRig { Channel = new MessageContactOptions(MessageContactChannel.Ota, MessageThreadKey.ForOta(threadId), null, false, false) };
        var (_, _, _, _, row) = await SeedAsync(rig, "Check-in: {{check_in_link}} (TEST)");
        await using (var db = rig.NewDb())
        {
            db.OtaMessages.Add(new OtaMessage { OrgId = OrgA, ThreadId = threadId, Direction = OtaMessageDirection.Outbound, Text = "x", IdempotencyKey = $"sm-{row.Id:N}", DeliveryState = OtaMessageDeliveryState.Sent });
            await db.SaveChangesAsync();
        }

        var result = await DispatchAsync(rig, row.Id);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, result.Outcome);
        Assert.Empty(rig.Sent);
        rig.CheckIn.Verify(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Equal(ScheduledMessageStatus.Sent, (await rig.RowAsync(row.Id)).Status);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // Campaigns
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>A booking with a routine whose only action is a campaign (<c>isCampaign</c>) and its planned message, due half an hour ago.</summary>
    private static async Task<(Booking Booking, ScheduledMessage Row)> SeedCampaignAsync(ScheduledMessageRig rig, Property property, bool consent)
    {
        var booking = await rig.SeedBookingAsync(property);
        await rig.SetMarketingConsentAsync(booking, consent);
        var template = await rig.SeedTemplateAsync(OrgA, "Un'offerta per te, {{guest_name}}! (TEST)", name: $"Offerta {Guid.NewGuid():N} (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template, actionsJson: $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{template.Id}}}","isCampaign":true}}]""");
        return (booking, await rig.SeedRowAsync(booking, routine, template));
    }

    /// <summary>
    /// PROOF TEST: privacy rule (ADR-022-R11, campaigns only with the marketing consent). The engine reads the consent when the routine fires, but the sweep sends what the routine did not, so a message the engine
    /// blocked would still go out: the dispatch reads the consent too, as the engine reads it, for the routine, for the sweep and for «Send now». A campaign for a guest who did not agree is skipped with the code
    /// of the guardrail, nothing is sent, nobody is alerted (it is the normal end of a campaign for that guest), and it does not count as a try; for a guest who agreed it goes out as any message.
    /// </summary>
    [Theory]
    [InlineData(ScheduledMessageDispatchMode.Routine)]
    [InlineData(ScheduledMessageDispatchMode.Sweep)]
    [InlineData(ScheduledMessageDispatchMode.SendNow)]
    public async Task ACampaign_GoesOnlyToAGuestWhoGaveTheMarketingConsent_WhoeverAsksForTheSend(ScheduledMessageDispatchMode mode)
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var (_, withoutConsent) = await SeedCampaignAsync(rig, property, consent: false);
        var (_, withConsent) = await SeedCampaignAsync(rig, property, consent: true);
        var userId = mode == ScheduledMessageDispatchMode.SendNow ? ScheduledMessageRig.Owner : null;

        var refused = await DispatchAsync(rig, withoutConsent.Id, mode, userId);

        Assert.Equal(ScheduledMessageDispatchOutcome.Skipped, refused.Outcome);
        Assert.Equal(ScheduledMessageReasons.CampaignNeedsConsent, refused.Code);
        Assert.Empty(rig.Sent);
        var skipped = await rig.RowAsync(withoutConsent.Id);
        Assert.Equal(ScheduledMessageStatus.Skipped, skipped.Status);
        Assert.Equal(ScheduledMessageReasons.CampaignNeedsConsent, skipped.SkipReason);
        Assert.Null(skipped.SkippedByUserId);
        Assert.Equal(0, skipped.Attempts);
        Assert.Empty(await rig.BellAsync());
        Assert.Empty(rig.Push.Queued);

        var sent = await DispatchAsync(rig, withConsent.Id, mode, userId);

        Assert.Equal(ScheduledMessageDispatchOutcome.Sent, sent.Outcome);
        var message = Assert.Single(rig.Sent);
        Assert.Equal("Un'offerta per te, Giulia! (TEST)", message.Text);
        Assert.Equal(ScheduledMessageStatus.Sent, (await rig.RowAsync(withConsent.Id)).Status);
    }

    /// <summary>
    /// The case the review found: the routine fired and the engine BLOCKED the campaign (the guardrail), so the row stayed planned; the sweep then reaches it after the catch-up time. The consent that the
    /// guardrail read is read again, and the campaign for the guest who did not agree does not go; the guest who agreed later gets none either, because the row ended.
    /// </summary>
    [Fact]
    public async Task ACampaignTheEngineBlocked_IsNotSentBy_TheSweepThatReachesTheRowAfterTheCatchUp()
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var (booking, row) = await SeedCampaignAsync(rig, property, consent: false);

        await using (var db = rig.NewDb())
            Assert.Equal(1, await rig.Dispatcher(db).SweepAsync(Now));

        Assert.Empty(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Skipped, (await rig.RowAsync(row.Id)).Status);

        await rig.SetMarketingConsentAsync(booking, true);
        await using (var db = rig.NewDb())
            Assert.Equal(0, await rig.Dispatcher(db).SweepAsync(Now.AddMinutes(30)));

        Assert.Empty(rig.Sent);
    }
}
