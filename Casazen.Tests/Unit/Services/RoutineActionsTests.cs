using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E01/F04/US01/T02 (NF-21, AC-NF21-1): the action <c>CreateActivity</c> is real (a To do for the booking of the event, once), every action that has
/// no implementation says so instead of pretending, and the catalog of the actions is the one place that knows which are available.
/// </summary>
public class RoutineActionsTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;
    private static readonly Guid OrgB = RoutineRig.OrgB;

    private static string CreateActivityConfig(string text, int? dueInDays = null) =>
        dueInDays is { } days
            ? $$"""{"text":"{{text}}","dueInDays":{{days}}}"""
            : $$"""{"text":"{{text}}"}""";

    private static string CreateActivity(string text = "Controlla le chiavi", int? dueInDays = null) =>
        RoutineRig.Actions(("CreateActivity", CreateActivityConfig(text, dueInDays)));

    private static async Task<List<InternalActivity>> TodosAsync(RoutineRig rig)
    {
        await using var db = rig.NewDb();
        return await db.InternalActivities.AsNoTracking().Where(a => a.ActivityType == nameof(ActivityType.RoutineTask)).ToListAsync();
    }

    // ─── CreateActivity ──────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data rule): two executions for the same booking leave one open To do. The To do is one for each booking while it is open, so the host is not
    /// asked twice for the same thing by two events, two routines or a delivery that comes again.
    /// </summary>
    [Fact]
    public async Task RoutineActions_CreateActivity_Idempotent_Test()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity("Controlla le chiavi"));
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity("Un'altra routine, la stessa prenotazione"));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var executions = await rig.ExecutionsAsync();
        Assert.Equal(4, executions.Count);
        Assert.All(executions, e => Assert.Equal(RoutineExecutionOutcomes.Success, e.Outcome));
        var todo = Assert.Single(await TodosAsync(rig));
        Assert.Equal(booking.Id, todo.EntityId);
        Assert.Null(todo.CompletedAt);
    }

    [Fact]
    public async Task CreateActivity_RaisesAToDoForTheBooking_WithTheTextOfTheHost()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity("Controlla le chiavi", dueInDays: 3));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var todo = Assert.Single(await TodosAsync(rig));
        Assert.Equal(OrgA, todo.OrgId);
        Assert.Equal(booking.PropertyId, todo.PropertyId);
        Assert.Equal(ActivityAction.ReviewBooking, todo.Action);
        Assert.Equal(InternalActivity.BookingEntityType, todo.EntityType);
        Assert.Equal(booking.Id, todo.EntityId);
        Assert.Equal(ActivityUrgency.Medium, todo.Urgency);
        Assert.Equal(InternalActivity.StatusPending, todo.Status);
        Assert.Equal("Controlla le chiavi", todo.Reason);
        Assert.Equal(RoutineRig.Now.UtcDateTime.AddDays(3), todo.Deadline);
        var action = Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions);
        Assert.Equal(new RoutineActionResult(0, RoutineActionType.CreateActivity, RoutineActionOutcomes.Done, null), action);
    }

    /// <summary>No <c>dueInDays</c> is no deadline (the To do is scheduled when it is raised); 0 and 60 are the two ends of the range.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60)]
    public async Task CreateActivity_DueInDays_IsADeadlineFromNow_OrNone(int? dueInDays)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity("Una scadenza", dueInDays));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var todo = Assert.Single(await TodosAsync(rig));
        DateTime? expected = dueInDays is { } days ? RoutineRig.Now.UtcDateTime.AddDays(days) : null;
        Assert.Equal(expected, todo.Deadline);
        Assert.Equal(expected ?? RoutineRig.Now.UtcDateTime, todo.ScheduledAt);
        Assert.Equal(RoutineExecutionOutcomes.Success, (await rig.SingleExecutionAsync()).Outcome);
    }

    [Fact]
    public async Task CreateActivity_TextOfAHundredAndFortyCharacters_IsAccepted()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var text = new string('a', 140);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity(text));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Equal(text, Assert.Single(await TodosAsync(rig)).Reason);
    }

    [Fact]
    public async Task CreateActivity_ARedeliveredEvent_RaisesNoSecondToDo()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity());

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking), eventId: "event-1");
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking), eventId: "event-1");

        Assert.Single(await rig.ExecutionsAsync());
        Assert.Single(await TodosAsync(rig));
    }

    /// <summary>A CreateActivity after a Wait raises its To do for the booking the event named (the saved payload), once, however many jobs resume it.</summary>
    [Fact]
    public async Task CreateActivity_AfterAWait_RaisesOneToDoForTheBookingOfTheEvent()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("Wait", """{"hours":1}"""), ("CreateActivity", CreateActivityConfig("Dopo l'attesa"))));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var waiting = await rig.SingleExecutionAsync();
        Assert.Empty(await TodosAsync(rig));
        var later = new FixedTimeProvider(RoutineRig.Now.AddHours(1));

        await rig.ResumeAsync(waiting.Id, later);
        await rig.ResumeAsync(waiting.Id, later);

        var todo = Assert.Single(await TodosAsync(rig));
        Assert.Equal(booking.Id, todo.EntityId);
        Assert.Equal("Dopo l'attesa", todo.Reason);
        Assert.Equal(RoutineExecutionOutcomes.Success, (await rig.ExecutionAsync(waiting.Id)).Outcome);
    }

    [Fact]
    public async Task CreateActivity_OnceTheToDoIsClosed_TheNextEventRaisesANewOne()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity());
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));
        var first = Assert.Single(await TodosAsync(rig));
        await using (var db = rig.NewDb())
        {
            var activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, new FixedTimeProvider(RoutineRig.Now));
            await activities.CloseAsync(first.Id, InternalActivity.ClosedBySystem, CancellationToken.None);
        }

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var todos = await TodosAsync(rig);
        Assert.Equal(2, todos.Count);
        Assert.Single(todos, t => t.CompletedAt is null);
    }

    [Fact]
    public async Task CreateActivity_NoBookingInTheEvent_FailsWithItsCode()
    {
        var rig = new RoutineRig();
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity());

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, new { Nothing = "to see" });

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.CreateActivity, RoutineActionOutcomes.Failed, RoutineCodes.ActivityNeedsBooking),
            Assert.Single(RoutineRig.ResultOf(execution).Actions));
        Assert.Empty(await TodosAsync(rig));
    }

    /// <summary>The booking is read in the org of the routine: the id of another org's booking is no booking, and no To do is raised in either org.</summary>
    [Fact]
    public async Task CreateActivity_ABookingOfAnotherOrg_FailsAndRaisesNothing()
    {
        var rig = new RoutineRig();
        var foreign = await rig.SeedBookingAsync(OrgB);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity());

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(foreign));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineCodes.ActivityNeedsBooking, Assert.Single(RoutineRig.ResultOf(execution).Actions).Code);
        Assert.Empty(await TodosAsync(rig));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"text":""}""")]
    [InlineData("""{"text":"   "}""")]
    [InlineData("""{"text":5}""")]
    [InlineData("""{"dueInDays":3}""")]
    [InlineData("""{"text":"Ok","dueInDays":-1}""")]
    [InlineData("""{"text":"Ok","dueInDays":61}""")]
    [InlineData("""{"text":"Ok","dueInDays":"3"}""")]
    [InlineData("""{"text":"Ok","dueInDays":1.5}""")]
    [InlineData("""{"text":"Ok","dueInDays":null}""")]
    public async Task CreateActivity_ConfigurationTheEngineRefuses_FailsAndRaisesNothing(string config)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions(("CreateActivity", config)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(RoutineCodes.ActionsInvalid, Assert.Single(RoutineRig.ResultOf(execution).Actions).Code);
        Assert.Empty(await TodosAsync(rig));
    }

    [Fact]
    public async Task CreateActivity_TextOfMoreThanAHundredAndFortyCharacters_Fails()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity(new string('a', 141)));

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Equal(RoutineCodes.ActionsInvalid, Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions).Code);
        Assert.Empty(await TodosAsync(rig));
    }

    /// <summary>The property of the booking is gone: the failure is the activity service's own code, recorded and not thrown (a retry would find the same).</summary>
    [Fact]
    public async Task CreateActivity_ThePropertyIsGone_FailsWithTheCodeOfTheActivityService()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, withProperty: false);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity());

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        var execution = await rig.SingleExecutionAsync();
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal("not_found", Assert.Single(RoutineRig.ResultOf(execution).Actions).Code);
        Assert.Empty(await TodosAsync(rig));
    }

    [Fact]
    public async Task CreateActivity_InTestMode_IsSimulated_AndRaisesNothing()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, CreateActivity(), testMode: true);

        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.PayloadOf(booking));

        Assert.Equal(RoutineActionOutcomes.Simulated, Assert.Single(RoutineRig.ResultOf(await rig.SingleExecutionAsync()).Actions).Outcome);
        Assert.Empty(await TodosAsync(rig));
    }

    /// <summary>The number of a member is never stored (the rows keep the name) but two members never share one: 100 is the number of its own (90 is taken by the tourist tax refunds of E25).</summary>
    [Fact]
    public void ActivityType_RoutineTask_HasANumberOfItsOwn()
    {
        Assert.Equal(100, (int)ActivityType.RoutineTask);
        var numbers = Enum.GetValues<ActivityType>().Select(type => (int)type).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    // ─── Actions with no implementation ──────────────────────────────────────────

    [Fact]
    public async Task Simulate_ShowsWhichActionsCannotRunYet()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        var routine = await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.BookingConfirmed,
            RoutineRig.Actions(("CreateActivity", CreateActivityConfig("Ok")), ("NotifyOwner", null), ("Wait", """{"hours":1}""")));

        await using var db = rig.NewDb();
        var result = await rig.Service(db).SimulateAsync(OrgA, routine.Id, RoutineRig.PayloadOf(booking));

        Assert.Equal([false, true, false], result.Actions.Select(a => a.WouldBlock));
        Assert.Equal([null, RoutineCodes.ActionNotAvailable, null], result.Actions.Select(a => a.Code));
        Assert.Empty(await TodosAsync(rig));
    }

    // ─── The catalog ─────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ListsEveryAction_OnceWithItsAvailabilityAndCode()
    {
        var all = RoutineActionCatalog.All(new TestFeatureFlags());

        Assert.Equal(Enum.GetValues<RoutineActionType>(), all.Select(a => a.Type));
        Assert.Equal(
            [RoutineActionType.CreateActivity, RoutineActionType.Wait],
            all.Where(a => a.Available).Select(a => a.Type).Order());
        Assert.All(all.Where(a => a.Available), a => Assert.Null(a.Code));
        Assert.All(all.Where(a => !a.Available), a => Assert.Equal(RoutineCodes.ActionNotAvailable, a.Code));
        Assert.Equal(
            [
                RoutineActionType.MessageToGuest,
                RoutineActionType.NotifyOwner,
                RoutineActionType.NotifyStaff,
                RoutineActionType.NotifyTeam,
                RoutineActionType.SendAccessCode,
                RoutineActionType.RequestReview,
                RoutineActionType.AddTag,
            ],
            all.Where(a => !a.Available).Select(a => a.Type));
    }

    [Fact]
    public void Catalog_Of_AndIsAvailable_AgreeWithAll()
    {
        var flags = new TestFeatureFlags();

        foreach (var entry in RoutineActionCatalog.All(flags))
        {
            Assert.Equal(entry, RoutineActionCatalog.Of(entry.Type, flags));
            Assert.Equal(entry.Available, RoutineActionCatalog.IsAvailable(entry.Type, flags));
        }
    }

    /// <summary>Today no action waits on a flag: every flag on changes nothing (E05/F07 makes MessageToGuest depend on its own).</summary>
    [Fact]
    public void Catalog_DoesNotChangeWithTheFlagsOfOtherFeatures()
    {
        var off = RoutineActionCatalog.All(new TestFeatureFlags());
        var on = RoutineActionCatalog.All(new TestFeatureFlags([.. FeatureFlags.All]));

        Assert.Equal(off, on);
    }

    [Fact]
    public void Catalog_AnActionFromTheEnumThatIsNotListed_IsNotAvailable()
    {
        Assert.All(
            Enum.GetValues<RoutineActionType>().Where(t => t is not (RoutineActionType.Wait or RoutineActionType.CreateActivity)),
            type => Assert.False(RoutineActionCatalog.IsAvailable(type, new TestFeatureFlags())));
    }

    // ─── No pretend texts ────────────────────────────────────────────────────────

    /// <summary>The texts the old engine recorded for actions it did not perform exist nowhere in the code of the application.</summary>
    [Fact]
    public void PretendTexts_OfTheOldEngine_ExistNowhereInTheCode()
    {
        string[] pretend = ["(email sent)", "(notification created)", "(code sent)", "(review requested)", "(tag added)", "(activity created)"];
        var root = FindRepositoryRoot();
        var offending = new List<string>();

        foreach (var project in new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal)
                    || relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/Migrations/", StringComparison.Ordinal))
                    continue;

                var text = File.ReadAllText(path);
                offending.AddRange(pretend.Where(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(p => $"{relative}: {p}"));
            }
        }

        Assert.True(offending.Count == 0, "A routine action must not record a success it did not perform:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void NotImplementedException_IsNotUsedByTheRoutineEngine()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "RoutineService.cs"));

        Assert.DoesNotContain("NotImplementedException", engine, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
