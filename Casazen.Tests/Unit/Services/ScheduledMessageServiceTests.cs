using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F07/US01/T02 (NF-71, MSG-11): the plan of the messages of the routines and the timeline of a booking. One test for each rule that costs a host or a guest if it is wrong: nothing is planned without an
/// active routine and the module, a stay gets one row for each message of each routine and never two, a row follows the dates of its booking but a sent, skipped or edited one is never overwritten, a stay
/// that does not meet the condition of the routine gets nothing, a collaborator reaches only the messages of the properties it was given, and the host's edit, skip and «Send now» change one message at a time.
/// </summary>
public class ScheduledMessageServiceTests
{
    private static readonly Guid OrgA = ScheduledMessageRig.OrgA;

    private static readonly Guid OrgB = ScheduledMessageRig.OrgB;

    private static DateTime Now => ScheduledMessageRig.Now.UtcDateTime;

    /// <summary>The moment the plan gives the message of the stay of the rig (check-in on 20 November): the day before, at the check-in time of the property (15:00 in Rome, 14:00 UTC).</summary>
    private static readonly DateTime FirstMoment = new(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc);

    private const string StayOfMoreThanThreeNights = """{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"stayDuration","operator":"greaterThan","value":3}]}]}""";

    private static async Task<int> PlanAsync(ScheduledMessageRig rig, Guid bookingId)
    {
        await using var db = rig.NewDb();
        return await rig.Service(db).PlanBookingAsync(bookingId);
    }

    private static async Task ChangeBookingAsync(ScheduledMessageRig rig, Guid bookingId, Action<Booking> change)
    {
        await using var db = rig.NewDb();
        change(await db.Bookings.SingleAsync(b => b.Id == bookingId));
        await db.SaveChangesAsync();
    }

    private static async Task ChangeRowAsync(ScheduledMessageRig rig, Guid id, Action<ScheduledMessage> change)
    {
        await using var db = rig.NewDb();
        change(await db.ScheduledMessages.SingleAsync(m => m.Id == id));
        await db.SaveChangesAsync();
    }

    private static async Task ChangeRoutineAsync(ScheduledMessageRig rig, Guid id, Action<Routine> change)
    {
        await using var db = rig.NewDb();
        change(await db.Routines.SingleAsync(r => r.Id == id));
        await db.SaveChangesAsync();
    }

    /// <summary>A property, a stay of three nights (from 20 November), a template and nothing else: the routines are the test's to add.</summary>
    private static async Task<(Property Property, Booking Booking, MessageTemplate Template)> StayAsync(ScheduledMessageRig rig, string body = "Ciao {{guest_name}}! (TEST)")
    {
        var property = await rig.SeedPropertyAsync(OrgA);
        var booking = await rig.SeedBookingAsync(property);
        var template = await rig.SeedTemplateAsync(OrgA, body);
        return (property, booking, template);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The plan
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST: data rule (nothing is sent that nobody asked for). An organization with no active routine that sends a message gets no row: not with no routine at all, not with one that is switched off,
    /// in test mode or does something else, and not with an active one while the module of the messages or the one of the routines is closed. Planning a booking writes nothing then, and the hourly plan does
    /// not look at the organization.
    /// </summary>
    [Fact]
    public async Task ScheduledMessages_NothingPlannedWithoutActiveRoutine_Test()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);

        Assert.Equal(0, await PlanAsync(rig, booking.Id));
        await using (var db = rig.NewDb())
            Assert.DoesNotContain(OrgA, await rig.Service(db).OrgsWithMessageRoutinesAsync());

        await rig.SeedRoutineAsync(OrgA, template, active: false);
        await rig.SeedRoutineAsync(OrgA, template, testMode: true);
        await rig.SeedRoutineAsync(OrgA, template, actionsJson: """[{"type":"CreateActivity","config":{"text":"Controlla (TEST)","dueInDays":1}}]""");

        Assert.Equal(0, await PlanAsync(rig, booking.Id));
        Assert.Empty(await rig.RowsAsync(booking.Id));
        await using (var db = rig.NewDb())
            Assert.DoesNotContain(OrgA, await rig.Service(db).OrgsWithMessageRoutinesAsync());

        // An active routine that sends a message: the plan writes its row, unless one of the two modules is closed.
        await rig.SeedRoutineAsync(OrgA, template);
        foreach (var flag in new[] { FeatureFlags.ScheduledMessages, FeatureFlags.Routines })
        {
            rig.Flags.Set(flag, false);
            Assert.Equal(0, await PlanAsync(rig, booking.Id));
            Assert.Empty(await rig.RowsAsync(booking.Id));
            rig.Flags.Set(flag, true);
        }

        await using (var db = rig.NewDb())
            Assert.Contains(OrgA, await rig.Service(db).OrgsWithMessageRoutinesAsync());
        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        Assert.Single(await rig.RowsAsync(booking.Id));
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task AStayThatDoesNotHappen_GetsNoMessage(BookingStatus status)
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var booking = await rig.SeedBookingAsync(property, status);
        await rig.SeedRoutineAsync(OrgA, await rig.SeedTemplateAsync(OrgA, "Ciao (TEST)"));

        Assert.Equal(0, await PlanAsync(rig, booking.Id));
        Assert.Empty(await rig.RowsAsync(booking.Id));
    }

    [Fact]
    public async Task TheRoutineOfAnotherOrg_PlansNothingForTheBooking()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, _) = await StayAsync(rig);
        await rig.SeedRoutineAsync(OrgB, await rig.SeedTemplateAsync(OrgB, "Ciao (TEST)"));

        Assert.Equal(0, await PlanAsync(rig, booking.Id));
        Assert.Empty(await rig.RowsAsync(booking.Id));
    }

    [Fact]
    public async Task PlanningABooking_WritesOneRowForEachMessageOfEachActiveRoutine_AtTheMomentThePlannerGives_AndNeverTwice()
    {
        var rig = new ScheduledMessageRig();
        var (property, booking, template) = await StayAsync(rig);
        var two = $$$"""[{"type":"MessageToGuest","config":{"templateId":"{{{template.Id}}}"}},{"type":"Wait","config":{"hours":2}},{"type":"MessageToGuest","config":{"templateId":"{{{template.Id}}}"}}]""";
        var routine = await rig.SeedRoutineAsync(OrgA, template, actionsJson: two);
        var other = await rig.SeedRoutineAsync(OrgA, template, eventConfig: """{"days":2}""");

        var written = await PlanAsync(rig, booking.Id);
        var again = await PlanAsync(rig, booking.Id);

        Assert.Equal(3, written);
        Assert.Equal(0, again);
        var rows = await rig.RowsAsync(booking.Id);
        Assert.Equal(3, rows.Count);
        var first = Assert.Single(rows, r => r.RoutineId == routine.Id && r.ActionIndex == 0);
        var second = Assert.Single(rows, r => r.RoutineId == routine.Id && r.ActionIndex == 2);
        var earlier = Assert.Single(rows, r => r.RoutineId == other.Id);
        Assert.Equal(FirstMoment, first.PlannedAtUtc);
        Assert.Equal(FirstMoment.AddHours(2), second.PlannedAtUtc);
        Assert.Equal(FirstMoment.AddDays(-1), earlier.PlannedAtUtc);
        Assert.All(rows, r =>
        {
            Assert.Equal(ScheduledMessageStatus.Scheduled, r.Status);
            Assert.Equal(OrgA, r.OrgId);
            Assert.Equal(property.Id, r.PropertyId);
            Assert.Equal(template.Id.ToString(), r.TemplateRef);
            Assert.Equal("Messaggio di arrivo (TEST)", r.RoutineName);
            Assert.Null(r.TextOverride);
        });
    }

    [Fact]
    public async Task TheDatesOfTheBooking_MoveAPlannedMessage_AndItsEditIsKept()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        await rig.SeedRoutineAsync(OrgA, template);
        await PlanAsync(rig, booking.Id);
        var row = Assert.Single(await rig.RowsAsync(booking.Id));
        await ChangeRowAsync(rig, row.Id, r => r.TextOverride = "Solo per te (TEST)");

        await ChangeBookingAsync(rig, booking.Id, b =>
        {
            b.CheckInDate = b.CheckInDate.AddDays(2);
            b.CheckOutDate = b.CheckOutDate.AddDays(2);
        });
        var changed = await PlanAsync(rig, booking.Id);

        Assert.Equal(1, changed);
        var moved = await rig.RowAsync(row.Id);
        Assert.Equal(FirstMoment.AddDays(2), moved.PlannedAtUtc);
        Assert.Equal("Solo per te (TEST)", moved.TextOverride);
        Assert.Equal(ScheduledMessageStatus.Scheduled, moved.Status);
    }

    [Theory]
    [InlineData(ScheduledMessageStatus.Sent)]
    [InlineData(ScheduledMessageStatus.Skipped)]
    [InlineData(ScheduledMessageStatus.Expired)]
    public async Task AMessageThatEnded_IsNeverOverwrittenByTheDatesOfTheBooking(ScheduledMessageStatus status)
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        await rig.SeedRoutineAsync(OrgA, template);
        await PlanAsync(rig, booking.Id);
        var row = Assert.Single(await rig.RowsAsync(booking.Id));
        await ChangeRowAsync(rig, row.Id, r => r.Status = status);
        await ChangeBookingAsync(rig, booking.Id, b =>
        {
            b.CheckInDate = b.CheckInDate.AddDays(2);
            b.CheckOutDate = b.CheckOutDate.AddDays(2);
        });

        Assert.Equal(0, await PlanAsync(rig, booking.Id));

        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(status, saved.Status);
        Assert.Equal(FirstMoment, saved.PlannedAtUtc);
    }

    [Fact]
    public async Task ARoutineSwitchedOffOrDeleted_CancelsItsOpenMessages_AndSwitchedOnAgainBringsThemBack()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        await PlanAsync(rig, booking.Id);
        var row = Assert.Single(await rig.RowsAsync(booking.Id));

        await ChangeRoutineAsync(rig, routine.Id, r => r.Active = false);
        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        var off = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Cancelled, off.Status);
        Assert.Equal(ScheduledMessageReasons.RoutineInactive, off.SkipReason);

        await ChangeRoutineAsync(rig, routine.Id, r => r.Active = true);
        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        var on = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Scheduled, on.Status);
        Assert.Null(on.SkipReason);
        Assert.Equal(FirstMoment, on.PlannedAtUtc);

        await using (var db = rig.NewDb())
        {
            db.Routines.Remove(await db.Routines.SingleAsync(r => r.Id == routine.Id));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        Assert.Equal(ScheduledMessageStatus.Cancelled, (await rig.RowAsync(row.Id)).Status);
    }

    [Fact]
    public async Task AMomentThatHasPassed_IsNotPlanned_AndAPlannedOneThatMovesIntoThePastExpires()
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var template = await rig.SeedTemplateAsync(OrgA, "Ciao (TEST)");
        await rig.SeedRoutineAsync(OrgA, template);

        // The routine was switched on after the moment of its message: the guest who was welcomed long ago is not welcomed again.
        var late = await rig.SeedBookingAsync(property, checkIn: new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, await PlanAsync(rig, late.Id));
        Assert.Empty(await rig.RowsAsync(late.Id));

        // A planned message whose stay moves so that its moment is behind us missed it: it is not sent late.
        var moving = await rig.SeedBookingAsync(property);
        await PlanAsync(rig, moving.Id);
        await ChangeBookingAsync(rig, moving.Id, b =>
        {
            b.CheckInDate = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
            b.CheckOutDate = b.CheckInDate.AddDays(3);
        });

        Assert.Equal(1, await PlanAsync(rig, moving.Id));
        var row = Assert.Single(await rig.RowsAsync(moving.Id));
        Assert.Equal(ScheduledMessageStatus.Expired, row.Status);
        Assert.Equal(ScheduledMessageReasons.Missed, row.SkipReason);
    }

    /// <summary>
    /// The condition of a routine («only for stays of more than three nights») is read as the engine reads it: the plan writes nothing for a stay that does not meet it, writes the message when the stay grows
    /// to meet it, and cancels it with its reason when the stay shrinks again.
    /// </summary>
    [Fact]
    public async Task TheConditionOfTheRoutine_DecidesWhichStaysGetAMessage()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        await rig.SeedRoutineAsync(OrgA, template, conditionJson: StayOfMoreThanThreeNights);

        Assert.Equal(0, await PlanAsync(rig, booking.Id));
        Assert.Empty(await rig.RowsAsync(booking.Id));

        await ChangeBookingAsync(rig, booking.Id, b => b.CheckOutDate = b.CheckInDate.AddDays(5));
        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        var row = Assert.Single(await rig.RowsAsync(booking.Id));
        Assert.Equal(ScheduledMessageStatus.Scheduled, row.Status);

        await ChangeBookingAsync(rig, booking.Id, b => b.CheckOutDate = b.CheckInDate.AddDays(2));
        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        var cancelled = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageStatus.Cancelled, cancelled.Status);
        Assert.Equal(ScheduledMessageReasons.ConditionNotMet, cancelled.SkipReason);

        await ChangeBookingAsync(rig, booking.Id, b => b.CheckOutDate = b.CheckInDate.AddDays(6));
        Assert.Equal(1, await PlanAsync(rig, booking.Id));
        Assert.Equal(ScheduledMessageStatus.Scheduled, (await rig.RowAsync(row.Id)).Status);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"conditions":[]}""")]
    public async Task ACondition_NobodyCanRead_PlansNothing(string conditionJson)
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        await rig.SeedRoutineAsync(OrgA, template, conditionJson: conditionJson);

        Assert.Equal(0, await PlanAsync(rig, booking.Id));
        Assert.Empty(await rig.RowsAsync(booking.Id));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // A booking that is cancelled, and the plan of an organization
    // ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACancelledBooking_CancelsItsOpenMessages_AndClosesTheirTodo_ButNeverASentOne()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routines = new List<Routine>();
        for (var days = 1; days <= 4; days++)
            routines.Add(await rig.SeedRoutineAsync(OrgA, template, eventConfig: $"{{\"days\":{days}}}"));

        var scheduled = await rig.SeedRowAsync(booking, routines[0], template);
        var blocked = await rig.SeedRowAsync(booking, routines[1], template, ScheduledMessageStatus.Blocked, blockedReason: "variable_missing:check_in_method");
        var failed = await rig.SeedRowAsync(booking, routines[2], template, ScheduledMessageStatus.Failed, blockedReason: "octorate_request_rejected", attempts: 1);
        var sent = await rig.SeedRowAsync(booking, routines[3], template, ScheduledMessageStatus.Sent);
        await using (var db = rig.NewDb())
        {
            var activityId = await ScheduledMessageTodos.RaiseAsync(rig.Activities(db), blocked, blocked.BlockedReason!, NullLogger.Instance, CancellationToken.None);
            Assert.NotNull(activityId);
        }

        Assert.Single(await rig.OpenTodosAsync(booking.Id));
        await ChangeBookingAsync(rig, booking.Id, b => b.Status = BookingStatus.Cancelled);

        await using (var db = rig.NewDb())
            Assert.Equal(3, await rig.Service(db).CancelForBookingAsync(booking.Id, ScheduledMessageReasons.BookingCancelled));
        await using (var db = rig.NewDb())
            Assert.Equal(0, await rig.Service(db).CancelForBookingAsync(booking.Id, ScheduledMessageReasons.BookingCancelled));

        foreach (var id in new[] { scheduled.Id, blocked.Id, failed.Id })
        {
            var row = await rig.RowAsync(id);
            Assert.Equal(ScheduledMessageStatus.Cancelled, row.Status);
            Assert.Equal(ScheduledMessageReasons.BookingCancelled, row.SkipReason);
        }

        Assert.Equal(ScheduledMessageStatus.Sent, (await rig.RowAsync(sent.Id)).Status);
        Assert.Empty(await rig.OpenTodosAsync(booking.Id));
    }

    [Fact]
    public async Task ACancellationThatWasUndoneSince_CancelsNothing()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var row = await rig.SeedRowAsync(booking, routine, template);

        // The event «cancelled» arrives after the booking was confirmed again: what the event says is done only if the stay is still so.
        await using var db = rig.NewDb();
        Assert.Equal(0, await rig.Service(db).CancelForBookingAsync(booking.Id, ScheduledMessageReasons.BookingCancelled));

        Assert.Equal(ScheduledMessageStatus.Scheduled, (await rig.RowAsync(row.Id)).Status);
    }

    [Fact]
    public async Task PlanningAnOrg_PlansTheStaysThatAreNear_NotTheFarOnes_AndSettlesTheCancelledStaysThatStillHaveAMessage()
    {
        var rig = new ScheduledMessageRig();
        var property = await rig.SeedPropertyAsync(OrgA);
        var template = await rig.SeedTemplateAsync(OrgA, "Ciao (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var near = await rig.SeedBookingAsync(property);
        var far = await rig.SeedBookingAsync(property, checkIn: new DateTime(2027, 1, 25, 0, 0, 0, DateTimeKind.Utc));
        var cancelled = await rig.SeedBookingAsync(property, BookingStatus.Cancelled);
        var open = await rig.SeedRowAsync(cancelled, routine, template);

        // The same routine in another organization, with a stay of its own: this organization's pass never touches it.
        var otherProperty = await rig.SeedPropertyAsync(OrgB);
        var otherStay = await rig.SeedBookingAsync(otherProperty);
        await rig.SeedRoutineAsync(OrgB, await rig.SeedTemplateAsync(OrgB, "Ciao (TEST)"));

        await using (var db = rig.NewDb())
            Assert.Equal(2, await rig.Service(db).ReplanOrgAsync(OrgA));

        Assert.Single(await rig.RowsAsync(near.Id));
        Assert.Empty(await rig.RowsAsync(far.Id));
        Assert.Empty(await rig.RowsAsync(otherStay.Id));
        var settled = await rig.RowAsync(open.Id);
        Assert.Equal(ScheduledMessageStatus.Cancelled, settled.Status);
        Assert.Equal(ScheduledMessageReasons.BookingCancelled, settled.SkipReason);

        await using (var db = rig.NewDb())
            Assert.Equal(0, await rig.Service(db).ReplanOrgAsync(OrgA));
    }

    [Fact]
    public async Task TheOrgsTheHourlyPlanLooksAt_AreThoseWithAnActiveRoutineThatSendsAMessage_OrAnOpenMessageToSettle()
    {
        var rig = new ScheduledMessageRig();
        var (_, _, templateA) = await StayAsync(rig);
        await rig.SeedRoutineAsync(OrgA, templateA);

        // Org B: its only routine is off, but a message of it is still planned (switching it off must cancel it).
        var propertyB = await rig.SeedPropertyAsync(OrgB);
        var stayB = await rig.SeedBookingAsync(propertyB);
        var templateB = await rig.SeedTemplateAsync(OrgB, "Ciao (TEST)");
        var routineB = await rig.SeedRoutineAsync(OrgB, templateB, active: false);
        await rig.SeedRowAsync(stayB, routineB, templateB);

        // Org C: nothing open and no routine.
        var orgC = Guid.NewGuid();
        var propertyC = await rig.SeedPropertyAsync(orgC);
        var stayC = await rig.SeedBookingAsync(propertyC);
        var templateC = await rig.SeedTemplateAsync(orgC, "Ciao (TEST)");
        var routineC = await rig.SeedRoutineAsync(orgC, templateC, active: false);
        await rig.SeedRowAsync(stayC, routineC, templateC, ScheduledMessageStatus.Sent);

        await using var db = rig.NewDb();
        var orgs = await rig.Service(db).OrgsWithMessageRoutinesAsync();

        Assert.Contains(OrgA, orgs);
        Assert.Contains(OrgB, orgs);
        Assert.DoesNotContain(orgC, orgs);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The timeline
    // ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTimeline_ListsTheMessagesInTimeOrder_WithTheTextAsItWillRead_AndWhatTheBookingHasNoValueFor_AndIssuesAndReleasesNothing()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig, "Ciao {{guest_name}}! Check-in: {{check_in_link}}. Metodo: {{check_in_method}} (TEST)");
        var later = await rig.SeedRoutineAsync(OrgA, template);
        var earlier = await rig.SeedRoutineAsync(OrgA, template, eventConfig: """{"days":2}""");
        var open = await rig.SeedRowAsync(booking, later, template, plannedAt: Now.AddDays(1));
        var sent = await rig.SeedRowAsync(booking, earlier, template, ScheduledMessageStatus.Sent, plannedAt: Now.AddDays(-1));

        IReadOnlyList<ScheduledMessageView> views;
        await using (var db = rig.NewDb())
            views = await rig.Service(db).ListForBookingAsync(new HostScope(OrgA), booking.Id);

        Assert.Equal([sent.Id, open.Id], views.Select(v => v.Id));
        var planned = views[1];
        Assert.Equal(ScheduledMessageStatus.Scheduled, planned.Status);
        Assert.Equal(DateTimeKind.Utc, planned.PlannedAtUtc.Kind);
        Assert.Equal("Europe/Rome", planned.TimeZone);
        Assert.Equal(MessageContactChannel.Direct, planned.Channel);
        Assert.Equal("Ciao Giulia! Check-in: [check_in_link]. Metodo: (TEST)", planned.Text);
        Assert.Equal(["check_in_method"], planned.MissingVariables);
        Assert.False(planned.Edited);
        Assert.True(planned.CanEdit);
        Assert.True(planned.CanSkip);
        Assert.True(planned.CanSendNow);

        // A message that went out is history: no text (the conversation holds it), no way to act on it.
        var history = views[0];
        Assert.Equal(ScheduledMessageStatus.Sent, history.Status);
        Assert.Null(history.Text);
        Assert.Equal(MessageContactChannel.None, history.Channel);
        Assert.False(history.CanEdit);
        Assert.False(history.CanSkip);
        Assert.False(history.CanSendNow);

        // A preview is a read: no link of the check-in was issued, no code was released, nothing was sent.
        rig.CheckIn.Verify(c => c.IssueLinkAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
        Assert.Empty(rig.Released);
        Assert.Empty(rig.Sent);
    }

    /// <summary>
    /// PROOF TEST: permission rule (TN-3, «Solo alcuni» immobili). A collaborator given only some properties reaches the messages of the bookings of those properties and of no other: for a booking or a message of
    /// a property it was not given, or of another organization, the timeline, the edit, the skip and «Send now» answer 404 exactly as they do for one that does not exist, and the message is left as it was.
    /// </summary>
    [Fact]
    public async Task ScheduledMessages_Collaborator_OnlyReachableBookings_Test()
    {
        var rig = new ScheduledMessageRig();
        var granted = await rig.SeedPropertyAsync(OrgA, "Casa data (TEST)");
        var hidden = await rig.SeedPropertyAsync(OrgA, "Casa nascosta (TEST)");
        var template = await rig.SeedTemplateAsync(OrgA, "Ciao {{guest_name}}! (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var grantedStay = await rig.SeedBookingAsync(granted);
        var hiddenStay = await rig.SeedBookingAsync(hidden);
        var grantedRow = await rig.SeedRowAsync(grantedStay, routine, template);
        var hiddenRow = await rig.SeedRowAsync(hiddenStay, routine, template);
        await using (var db = rig.NewDb())
        {
            db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = OrgA, UserId = "auth0|collab", PropertyId = granted.Id });
            await db.SaveChangesAsync();
        }

        var collaborator = new HostScope(OrgA, GrantedToUserId: "auth0|collab");
        await using var scoped = rig.NewDb();
        var service = rig.Service(scoped);

        Assert.Single(await service.ListForBookingAsync(collaborator, grantedStay.Id));
        Assert.Equal(grantedStay.Id, await service.FindBookingIdAsync(collaborator, grantedRow.Id));
        Assert.Null(await service.FindBookingIdAsync(collaborator, hiddenRow.Id));

        var noBooking = await Assert.ThrowsAsync<NotFoundException>(() => service.ListForBookingAsync(collaborator, hiddenStay.Id));
        Assert.Equal("booking_not_found", noBooking.Code);
        foreach (var attempt in new Func<Task>[]
                 {
                     () => service.EditAsync(collaborator, hiddenRow.Id, "Cambiato (TEST)", ScheduledMessageRig.Owner),
                     () => service.SkipAsync(collaborator, hiddenRow.Id, ScheduledMessageRig.Owner),
                     () => service.SendNowAsync(collaborator, hiddenRow.Id, ScheduledMessageRig.Owner),
                 })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal(ScheduledMessageErrors.NotFound, refused.Code);
        }

        // Another organization reaches neither of them, with the whole of its own org behind it.
        var stranger = new HostScope(OrgB);
        Assert.Null(await service.FindBookingIdAsync(stranger, grantedRow.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListForBookingAsync(stranger, grantedStay.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SkipAsync(stranger, grantedRow.Id, ScheduledMessageRig.Owner));

        // The whole org reaches both.
        Assert.Equal(hiddenStay.Id, await service.FindBookingIdAsync(new HostScope(OrgA), hiddenRow.Id));

        Assert.Empty(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Scheduled, (await rig.RowAsync(hiddenRow.Id)).Status);
        Assert.Null((await rig.RowAsync(hiddenRow.Id)).TextOverride);
        Assert.Equal(ScheduledMessageStatus.Scheduled, (await rig.RowAsync(grantedRow.Id)).Status);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // What the host does with a message
    // ------------------------------------------------------------------------------------------------------------------------------------

    private static async Task<(Booking Booking, ScheduledMessage Row)> OneOpenMessageAsync(ScheduledMessageRig rig, string body = "Ciao {{guest_name}}! (TEST)", ScheduledMessageStatus status = ScheduledMessageStatus.Scheduled, DateTime? plannedAt = null)
    {
        var (_, booking, template) = await StayAsync(rig, body);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        return (booking, await rig.SeedRowAsync(booking, routine, template, status, plannedAt));
    }

    [Fact]
    public async Task EditingAMessage_ChangesItsTextForThisBookingOnly_AndAnEmptyTextGoesBackToTheTemplate()
    {
        var rig = new ScheduledMessageRig();
        var (_, row) = await OneOpenMessageAsync(rig);
        var scope = new HostScope(OrgA);
        await using var db = rig.NewDb();
        var service = rig.Service(db);

        var edited = await service.EditAsync(scope, row.Id, "Solo per te, {{guest_name}}! (TEST)", ScheduledMessageRig.Owner);

        Assert.True(edited.Edited);
        Assert.Equal("Solo per te, Giulia! (TEST)", edited.Text);
        Assert.Equal("Solo per te, {{guest_name}}! (TEST)", (await rig.RowAsync(row.Id)).TextOverride);

        var cleared = await service.EditAsync(scope, row.Id, "   ", ScheduledMessageRig.Owner);

        Assert.False(cleared.Edited);
        Assert.Equal("Ciao Giulia! (TEST)", cleared.Text);
        Assert.Null((await rig.RowAsync(row.Id)).TextOverride);
    }

    /// <summary>
    /// PROOF TEST: data rule (what is not valid is not saved). The edit of a message goes through the rules of the composer: a text that is too long, and a name that is not a variable, are refused with the
    /// codes of the hub, and the message is left as it was.
    /// </summary>
    [Fact]
    public async Task AnEditTheComposerRefuses_IsRefused_AndChangesNothing()
    {
        var rig = new ScheduledMessageRig();
        var (_, row) = await OneOpenMessageAsync(rig);
        await using var db = rig.NewDb();
        var service = rig.Service(db);

        var tooLong = await Assert.ThrowsAsync<DomainRuleException>(() => service.EditAsync(new HostScope(OrgA), row.Id, new string('a', GuestMessageRules.TextMaxLength + 1), ScheduledMessageRig.Owner));
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(() => service.EditAsync(new HostScope(OrgA), row.Id, "Ciao {{nome}} (TEST)", ScheduledMessageRig.Owner));

        Assert.Equal(MessageChannelRules.TextInvalidCode, tooLong.Code);
        Assert.Equal(MessageTemplateRenderer.UnknownCode, unknown.Code);
        Assert.Null((await rig.RowAsync(row.Id)).TextOverride);
    }

    [Theory]
    [InlineData(ScheduledMessageStatus.Sent)]
    [InlineData(ScheduledMessageStatus.Skipped)]
    [InlineData(ScheduledMessageStatus.Cancelled)]
    [InlineData(ScheduledMessageStatus.Expired)]
    public async Task AMessageThatEnded_CanNotBeEditedSkippedOrSentNow(ScheduledMessageStatus status)
    {
        var rig = new ScheduledMessageRig();
        var (_, row) = await OneOpenMessageAsync(rig, status: status);
        var scope = new HostScope(OrgA);
        await using var db = rig.NewDb();
        var service = rig.Service(db);

        var edit = await Assert.ThrowsAsync<DomainConflictException>(() => service.EditAsync(scope, row.Id, "Altro (TEST)", ScheduledMessageRig.Owner));
        var skip = await Assert.ThrowsAsync<DomainConflictException>(() => service.SkipAsync(scope, row.Id, ScheduledMessageRig.Owner));
        var sendNow = await Assert.ThrowsAsync<DomainConflictException>(() => service.SendNowAsync(scope, row.Id, ScheduledMessageRig.Owner));

        Assert.Equal(ScheduledMessageErrors.NotEditable, edit.Code);
        Assert.Equal(ScheduledMessageErrors.NotEditable, skip.Code);
        Assert.Equal(ScheduledMessageErrors.NotSendable, sendNow.Code);
        Assert.Empty(rig.Sent);
        Assert.Equal(status, (await rig.RowAsync(row.Id)).Status);
    }

    [Fact]
    public async Task SkippingAMessage_EndsIt_ClosesItsTodo_AndTellsTheOthers_NotThePersonWhoSkipped()
    {
        var rig = new ScheduledMessageRig();
        var (booking, row) = await OneOpenMessageAsync(rig, status: ScheduledMessageStatus.Blocked);
        await using (var db = rig.NewDb())
            Assert.NotNull(await ScheduledMessageTodos.RaiseAsync(rig.Activities(db), row, "variable_missing:check_in_method", NullLogger.Instance, CancellationToken.None));
        Assert.Single(await rig.OpenTodosAsync(booking.Id));

        ScheduledMessageView view;
        await using (var db = rig.NewDb())
            view = await rig.Service(db).SkipAsync(new HostScope(OrgA), row.Id, ScheduledMessageRig.Owner);

        Assert.Equal(ScheduledMessageStatus.Skipped, view.Status);
        Assert.Equal(ScheduledMessageReasons.Person, view.SkipReason);
        Assert.False(view.CanEdit);
        var saved = await rig.RowAsync(row.Id);
        Assert.Equal(ScheduledMessageRig.Owner, saved.SkippedByUserId);
        Assert.NotNull(saved.SkippedAt);
        Assert.Empty(await rig.OpenTodosAsync(booking.Id));

        // The owner skipped it and needs no alert about its own action: the manager is told, through the bell and the push it chose.
        Assert.Equal(ScheduledMessageRig.Manager, Assert.Single(await rig.BellAsync()).UserId);
        Assert.Equal(ScheduledMessageRig.Manager, Assert.Single(rig.Push.Queued).Audience.UserId);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public async Task SendingNowAMessage_SendsItAtOnce_WhateverItsTime_AsAnAutomaticMessage_AndTheViewSaysItWasSent()
    {
        var rig = new ScheduledMessageRig();
        var (booking, row) = await OneOpenMessageAsync(rig, plannedAt: Now.AddDays(1));

        ScheduledMessageView view;
        await using (var db = rig.NewDb())
            view = await rig.Service(db).SendNowAsync(new HostScope(OrgA), row.Id, ScheduledMessageRig.Owner);

        Assert.Equal(ScheduledMessageStatus.Sent, view.Status);
        Assert.Equal(Now, view.SentAt);
        Assert.Equal($"direct:{booking.Id:D}", view.SentThreadKey);
        Assert.False(view.OutsideHours);
        Assert.False(view.CanSendNow);
        var sent = Assert.Single(rig.Sent);
        Assert.Equal("Ciao Giulia! (TEST)", sent.Text);
        Assert.Equal(MessageOrigin.Automation, sent.Origin);
        Assert.Null(sent.UserId);
    }

    [Fact]
    public async Task SendingNowABlockedMessage_IsRefusedWithWhatItWaitsFor_AndAMessageUnderWayIsAConflict()
    {
        var rig = new ScheduledMessageRig();
        var (_, blockedRow) = await OneOpenMessageAsync(rig, "Come arrivare: {{check_in_method}} (TEST)");
        var scope = new HostScope(OrgA);
        await using (var db = rig.NewDb())
        {
            var blocked = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service(db).SendNowAsync(scope, blockedRow.Id, ScheduledMessageRig.Owner));
            Assert.Equal(ScheduledMessageErrors.Blocked, blocked.Code);
            Assert.Equal(["variable_missing:check_in_method"], Assert.IsType<string[]>(blocked.Extensions["missing"]));
        }

        Assert.Empty(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Blocked, (await rig.RowAsync(blockedRow.Id)).Status);

        var held = new ScheduledMessageRig();
        var (_, heldRow) = await OneOpenMessageAsync(held);
        await ChangeRowAsync(held, heldRow.Id, r => r.LastCheckedAt = Now.AddSeconds(-30));
        await using (var db = held.NewDb())
        {
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => held.Service(db).SendNowAsync(scope, heldRow.Id, ScheduledMessageRig.Owner));
            Assert.Equal(ScheduledMessageErrors.InProgress, conflict.Code);
        }

        Assert.Empty(held.Sent);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The door of the routine engine
    // ------------------------------------------------------------------------------------------------------------------------------------

    private static async Task<RoutineActionResult> FireAsync(ScheduledMessageRig rig, Routine routine, Guid bookingId, TimeProvider? clock = null)
    {
        await using var db = rig.NewDb();
        return await rig.Service(db, clock).OnRoutineActionAsync(routine.Id, 0, bookingId);
    }

    [Fact]
    public async Task TheRoutineThatFiresAtTheMomentOfAPlannedMessage_SendsIt_ThroughTheOneDispatcher_AndOnceWhateverTheNumberOfFirings()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var row = await rig.SeedRowAsync(booking, routine, template);

        var first = await FireAsync(rig, routine, booking.Id);
        var again = await FireAsync(rig, routine, booking.Id);

        Assert.Equal(RoutineActionOutcomes.Done, first.Outcome);
        Assert.Equal(RoutineActionOutcomes.Done, again.Outcome);
        Assert.Single(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Sent, (await rig.RowAsync(row.Id)).Status);
    }

    [Fact]
    public async Task TheRoutineThatFiresForAMessageThePlanHasNotWritten_WritesItForItsMoment_AndLeavesItToTheSweepWhenItsTimeHasNotCome()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template);

        var result = await FireAsync(rig, routine, booking.Id);

        // The moment of the message (the day before the check-in) is ahead: the row is written for it, nothing goes out now, and the action is in hand.
        Assert.Equal(RoutineActionOutcomes.Done, result.Outcome);
        Assert.Empty(rig.Sent);
        var row = Assert.Single(await rig.RowsAsync(booking.Id));
        Assert.Equal(ScheduledMessageStatus.Scheduled, row.Status);
        Assert.Equal(FirstMoment, row.PlannedAtUtc);
    }

    [Fact]
    public async Task TheRoutineOfAnEventThePlanCannotForesee_SendsItsMessageWhenItFires_InsideTheSendWindow_AndWaitsForTheMorningOutsideIt()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template, eventType: RoutineEventType.PaymentSucceeded, eventConfig: "{}");

        var inside = await FireAsync(rig, routine, booking.Id);

        Assert.Equal(RoutineActionOutcomes.Done, inside.Outcome);
        Assert.Single(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Sent, Assert.Single(await rig.RowsAsync(booking.Id)).Status);

        // The same event at 22:30 in Rome: the row is written for 08:00 the next morning and nothing goes out in the night.
        var night = new ScheduledMessageRig();
        var (_, nightStay, nightTemplate) = await StayAsync(night);
        var nightRoutine = await night.SeedRoutineAsync(OrgA, nightTemplate, eventType: RoutineEventType.PaymentSucceeded, eventConfig: "{}");

        var outside = await FireAsync(night, nightRoutine, nightStay.Id, new FixedTimeProvider(new DateTimeOffset(2026, 11, 18, 21, 30, 0, TimeSpan.Zero)));

        Assert.Equal(RoutineActionOutcomes.Done, outside.Outcome);
        Assert.Empty(night.Sent);
        var waiting = Assert.Single(await night.RowsAsync(nightStay.Id));
        Assert.Equal(ScheduledMessageStatus.Scheduled, waiting.Status);
        Assert.Equal(new DateTime(2026, 11, 19, 7, 0, 0, DateTimeKind.Utc), waiting.PlannedAtUtc);
    }

    [Theory]
    [InlineData(ScheduledMessageStatus.Skipped)]
    [InlineData(ScheduledMessageStatus.Cancelled)]
    [InlineData(ScheduledMessageStatus.Expired)]
    public async Task TheRoutineThatFiresForAMessageThatDoesNotGoOut_IsToldSo_AndSendsNothing(ScheduledMessageStatus status)
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var row = await rig.SeedRowAsync(booking, routine, template, status);
        await ChangeRowAsync(rig, row.Id, r => r.SkipReason = ScheduledMessageReasons.Person);

        var result = await FireAsync(rig, routine, booking.Id);

        Assert.Equal(RoutineActionOutcomes.Skipped, result.Outcome);
        Assert.Equal(ScheduledMessageReasons.Person, result.Code);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public async Task TheRoutineThatFiresForAMessageWithAMissingVariable_IsBlockedWithTheCode_AndTheRowIsHeldBack()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig, "Come arrivare: {{check_in_method}} (TEST)");
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        await rig.SeedRowAsync(booking, routine, template);

        var result = await FireAsync(rig, routine, booking.Id);

        Assert.Equal(RoutineActionOutcomes.Blocked, result.Outcome);
        Assert.Equal("variable_missing:check_in_method", result.Code);
        Assert.Empty(rig.Sent);
        Assert.Equal(ScheduledMessageStatus.Blocked, Assert.Single(await rig.RowsAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task TheDoorOfTheEngine_RefusesWhatItCannotRead_ABookingOfAnotherOrg_ARoutineWithNoSuchAction_AndTheModuleClosed()
    {
        var rig = new ScheduledMessageRig();
        var (_, booking, template) = await StayAsync(rig);
        var routine = await rig.SeedRoutineAsync(OrgA, template);
        var foreignProperty = await rig.SeedPropertyAsync(OrgB);
        var foreign = await rig.SeedBookingAsync(foreignProperty);

        var otherOrg = await FireAsync(rig, routine, foreign.Id);
        await using (var db = rig.NewDb())
        {
            var noAction = await rig.Service(db).OnRoutineActionAsync(routine.Id, 3, booking.Id);
            Assert.Equal(RoutineActionOutcomes.Failed, noAction.Outcome);
        }

        rig.Flags.Set(FeatureFlags.ScheduledMessages, false);
        var closed = await FireAsync(rig, routine, booking.Id);

        Assert.Equal(RoutineActionOutcomes.Blocked, otherOrg.Outcome);
        Assert.Equal("booking_not_found", otherOrg.Code);
        Assert.Equal(RoutineActionOutcomes.Failed, closed.Outcome);
        Assert.Empty(rig.Sent);
        Assert.Empty(await rig.RowsAsync(foreign.Id));
    }
}
