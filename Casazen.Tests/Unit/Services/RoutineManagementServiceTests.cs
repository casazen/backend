using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E01/F04/US01/T03 (NF-21, ADR-022): the routines of an org. A routine of another org is never read, changed or deleted (404); what is saved is what the
/// engine can run (a stable code for everything that is not); an active routine outside test mode holds only actions that are available; at most 50
/// routines for an org.
/// </summary>
public class RoutineManagementServiceTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;
    private static readonly Guid OrgB = RoutineRig.OrgB;

    private static JsonElement J(string json) => RoutineRig.Json(json);

    private static RoutineActionInput Act(string? type, string? config = null) => new(type, config is null ? null : J(config));

    private static readonly RoutineActionInput CreateTodo = Act("CreateActivity", """{"text":"Controlla le chiavi","dueInDays":1}""");

    private static RoutineCommand Command(
        string? name = "Controlla l'arrivo",
        string? eventType = "BeforeCheckin",
        string? eventConfig = """{"days":2}""",
        string? condition = null,
        IReadOnlyList<RoutineActionInput>? actions = null,
        bool? testMode = null,
        bool? active = null) =>
        new(
            name,
            eventType,
            eventConfig is null ? null : J(eventConfig),
            condition is null ? null : J(condition),
            actions ?? [CreateTodo],
            testMode,
            active);

    private static async Task<DomainRuleException> RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<DomainRuleException>(action);

    private static async Task<List<Routine>> RoutinesOfAsync(RoutineRig rig, Guid orgId)
    {
        await using var db = rig.NewDb();
        return await db.Routines.AsNoTracking().Where(r => r.OrgId == orgId).ToListAsync();
    }

    /// <summary>A routine of <see cref="OrgA"/> as it is saved, read in a context of its own (the context under test hands back the object it tracks).</summary>
    private static async Task<Routine> StoredAsync(RoutineRig rig, Guid id) =>
        (await RoutinesOfAsync(rig, OrgA)).Single(r => r.Id == id);

    // ─── The org (permission rule) ───────────────────────────────────────────────

    /// <summary>
    /// Proof (permission rule): a routine of another org cannot be read, changed, switched, deleted or have its log read: every one is a 404
    /// <c>routine_not_found</c>, the same answer as a routine that does not exist, and the routine stays exactly as its org left it.
    /// </summary>
    [Fact]
    public async Task RoutineManagement_OtherOrgRoutine_NotFound_Test()
    {
        var rig = new RoutineRig();
        var foreign = await rig.SeedRoutineAsync(OrgB, RoutineEventType.BookingConfirmed, RoutineRig.Actions(("CreateActivity", """{"text":"Di B"}""")));
        await using var db = rig.NewDb();
        var service = rig.Management(db);

        var attempts = new Func<Task>[]
        {
            () => service.GetAsync(OrgA, foreign.Id),
            () => service.UpdateAsync(OrgA, foreign.Id, Command(name: "Presa di A")),
            () => service.DeleteAsync(OrgA, foreign.Id),
            () => service.SetActiveAsync(OrgA, foreign.Id, false),
            () => service.SetTestModeAsync(OrgA, foreign.Id, true),
            () => service.ListExecutionsAsync(OrgA, foreign.Id, 1, 50),
        };
        foreach (var attempt in attempts)
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal(RoutineCodes.NotFound, error.Code);
            Assert.Equal("RoutineNotFound", error.MessageKey);
        }

        Assert.Empty(await service.ListAsync(OrgA));
        var unchanged = Assert.Single(await RoutinesOfAsync(rig, OrgB));
        Assert.Equal(foreign.Name, unchanged.Name);
        Assert.Equal(foreign.Active, unchanged.Active);
        Assert.Equal(foreign.TestMode, unchanged.TestMode);
        Assert.Equal(foreign.ActionsJson, unchanged.ActionsJson);
    }

    [Fact]
    public async Task List_IsTheRoutinesOfTheOrg_ByName()
    {
        var rig = new RoutineRig();
        var zeta = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        var alfa = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        await rig.SeedRoutineAsync(OrgB, RoutineEventType.BookingConfirmed, "[]");
        await using (var seed = rig.NewDb())
        {
            (await seed.Routines.SingleAsync(r => r.Id == zeta.Id)).Name = "Zeta";
            (await seed.Routines.SingleAsync(r => r.Id == alfa.Id)).Name = "Alfa";
            await seed.SaveChangesAsync();
        }

        await using var db = rig.NewDb();
        var listed = await rig.Management(db).ListAsync(OrgA);

        Assert.Equal(["Alfa", "Zeta"], listed.Select(r => r.Name));
        Assert.All(listed, r => Assert.Equal(OrgA, r.OrgId));
    }

    // ─── Active means it can run (data rule) ─────────────────────────────────────

    /// <summary>
    /// Proof (data rule): a routine that holds an action that is not available cannot be made active outside test mode, whichever way it is tried: it would
    /// fail at every event, and the host would think it works. In test mode it may hold any action.
    /// </summary>
    [Fact]
    public async Task RoutineManagement_ActivateWithUnavailableAction_Rejected_Test()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var routine = await service.CreateAsync(
            OrgA,
            Command(actions: [CreateTodo, Act("NotifyOwner"), Act("AddTag", """{"tag":"vip"}"""), Act("NotifyOwner")], active: false));

        var byTheSwitch = await RefusedAsync(() => service.SetActiveAsync(OrgA, routine.Id, true));
        var byACreate = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [Act("RequestReview")], active: true)));
        // A change that only says "active" keeps the actions the routine has, so it is judged with them.
        var byAnUpdate = await RefusedAsync(() => service.UpdateAsync(OrgA, routine.Id, new RoutineCommand(null, null, null, null, null, null, true)));

        Assert.All(new[] { byTheSwitch, byACreate, byAnUpdate }, error =>
        {
            Assert.Equal(RoutineCodes.ActionNotAvailable, error.Code);
            Assert.Equal("RoutineActionNotAvailable", error.MessageKey);
        });
        Assert.Equal(["NotifyOwner", "AddTag"], Assert.IsAssignableFrom<IEnumerable<string>>(byTheSwitch.Extensions["actions"]));
        Assert.Equal("NotifyOwner, AddTag", Assert.Single(byTheSwitch.MessageArgs));
        Assert.False((await RoutinesOfAsync(rig, OrgA)).Single(r => r.Id == routine.Id).Active);

        // In test mode it may hold any action, and be active.
        await service.SetTestModeAsync(OrgA, routine.Id, true);
        await service.SetActiveAsync(OrgA, routine.Id, true);
        var inTest = await StoredAsync(rig, routine.Id);
        Assert.True(inTest.TestMode);
        Assert.True(inTest.Active);

        // And it cannot leave test mode while it holds one.
        var leaving = await RefusedAsync(() => service.SetTestModeAsync(OrgA, routine.Id, false));
        Assert.Equal(RoutineCodes.ActionNotAvailable, leaving.Code);
        Assert.True((await StoredAsync(rig, routine.Id)).TestMode);
    }

    [Fact]
    public async Task Activation_OfARoutineWithOnlyAvailableActions_IsAccepted_AndAnActiveRoutineCannotGainAnUnavailableOne()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var routine = await service.CreateAsync(OrgA, Command(actions: [CreateTodo, Act("Wait", """{"hours":2}""")]));
        Assert.False(routine.Active);

        var active = await service.SetActiveAsync(OrgA, routine.Id, true);
        var gaining = await RefusedAsync(() => service.UpdateAsync(OrgA, routine.Id, Command(actions: [CreateTodo, Act("NotifyStaff")])));

        Assert.True(active.Active);
        Assert.Equal(RoutineCodes.ActionNotAvailable, gaining.Code);
        Assert.Equal(2, JsonDocument.Parse((await RoutinesOfAsync(rig, OrgA)).Single().ActionsJson).RootElement.GetArrayLength());
    }

    // ─── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_SavesTheRoutineOff_NotInTestMode_WithTheShapeTheEngineReads()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var routine = await rig.Management(db).CreateAsync(
            OrgA,
            Command(
                name: "  Controlla l'arrivo  ",
                condition: """{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"in","value":["Airbnb","Direct"]}]}]}""",
                actions: [Act("waIT", """{"hours":2}"""), CreateTodo]));

        var saved = Assert.Single(await RoutinesOfAsync(rig, OrgA));
        Assert.Equal(routine.Id, saved.Id);
        Assert.Equal(OrgA, saved.OrgId);
        Assert.Equal("Controlla l'arrivo", saved.Name);
        Assert.Equal(RoutineEventType.BeforeCheckin, saved.EventType);
        Assert.Equal("""{"days":2}""", saved.EventConfig);
        Assert.False(saved.Active);
        Assert.False(saved.TestMode);
        Assert.Contains("\"channel\"", saved.ConditionJson, StringComparison.Ordinal);
        Assert.True(RoutineAction.TryParseList(saved.ActionsJson, out var actions));
        Assert.Equal([RoutineActionType.Wait, RoutineActionType.CreateActivity], actions.Select(a => a.Type));
        Assert.True(actions[0].TryGetInt("hours", out var hours));
        Assert.Equal(2, hours);
    }

    [Fact]
    public async Task Create_ConditionEmptyOrAbsent_IsNoCondition_AndAnEventWithoutConfigurationTakesNone()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);

        var empty = await service.CreateAsync(OrgA, Command(eventType: "BookingConfirmed", eventConfig: null, condition: "{}"));
        var absent = await service.CreateAsync(OrgA, Command(eventType: "bookingconfirmed", eventConfig: "{}", condition: null));

        Assert.All(new[] { empty, absent }, r =>
        {
            Assert.Equal("{}", r.ConditionJson);
            Assert.Equal("{}", r.EventConfig);
            Assert.Equal(RoutineEventType.BookingConfirmed, r.EventType);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_ANameThatIsEmpty_IsRefused(string? name)
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(name: name)));

        Assert.Equal((RoutineCodes.NameInvalid, "RoutineNameInvalid"), (error.Code, error.MessageKey));
        Assert.Equal(RoutineLimits.MaxNameLength, Assert.Single(error.MessageArgs));
        Assert.Empty(await RoutinesOfAsync(rig, OrgA));
    }

    [Fact]
    public async Task Create_ANameOf200Characters_IsAccepted_And201IsNot()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);

        var longest = await service.CreateAsync(OrgA, Command(name: new string('n', 200)));
        var error = await RefusedAsync(() => service.CreateAsync(OrgA, Command(name: new string('n', 201))));

        Assert.Equal(200, longest.Name.Length);
        Assert.Equal(RoutineCodes.NameInvalid, error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nope")]
    [InlineData("3")]
    [InlineData("BookingNew,BookingConfirmed")]
    public async Task Create_AnEventTypeThatIsNotAMember_IsRefused(string? eventType)
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(eventType: eventType, eventConfig: null)));

        Assert.Equal((RoutineCodes.EventTypeInvalid, "RoutineEventTypeInvalid"), (error.Code, error.MessageKey));
    }

    /// <summary>The weekly recurrence has no job that writes it, so a routine on it would never run: it is not saved (the editor does not offer it).</summary>
    [Fact]
    public async Task Create_WeeklyRecurrence_IsNotAvailable()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(eventType: "WeeklyRecurrence", eventConfig: """{"dayOfWeek":1}""")));

        Assert.Equal((RoutineCodes.EventNotAvailable, "RoutineEventNotAvailable"), (error.Code, error.MessageKey));
    }

    [Theory]
    [InlineData("BeforeCheckin", """{"days":2}""")]
    [InlineData("AfterCheckin", """{"hours":48}""")]
    [InlineData("BeforeCheckout", """{"days":1,"hours":3}""")]
    [InlineData("AfterCheckout", """{"Days":0,"HOURS":0}""")]
    [InlineData("AfterCheckout", "{}")]
    [InlineData("BeforeCheckin", """{"days":365}""")]
    [InlineData("BeforeCheckin", """{"hours":365}""")]
    [InlineData("BookingNew", "{}")]
    [InlineData("PaymentFailed", "{}")]
    public async Task Create_AnEventConfigurationThatIsValidForItsType_IsAccepted(string eventType, string eventConfig)
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var routine = await rig.Management(db).CreateAsync(OrgA, Command(eventType: eventType, eventConfig: eventConfig));

        Assert.Equal(eventConfig, routine.EventConfig);
    }

    [Theory]
    [InlineData("BeforeCheckin", """{"days":-1}""")]
    [InlineData("BeforeCheckin", """{"days":366}""")]
    [InlineData("BeforeCheckin", """{"hours":366}""")]
    [InlineData("BeforeCheckin", """{"days":"2"}""")]
    [InlineData("BeforeCheckin", """{"days":1.5}""")]
    [InlineData("BeforeCheckin", """{"minutes":3}""")]
    [InlineData("BeforeCheckin", """{"days":365,"hours":365}""")]
    [InlineData("BeforeCheckin", "[2]")]
    [InlineData("BeforeCheckin", "\"2 days\"")]
    [InlineData("BookingConfirmed", """{"days":2}""")]
    [InlineData("GuestMessageReceived", """{"hours":1}""")]
    public async Task Create_AnEventConfigurationThatIsNotValidForItsType_IsRefused(string eventType, string eventConfig)
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(eventType: eventType, eventConfig: eventConfig)));

        Assert.Equal((RoutineCodes.EventConfigInvalid, "RoutineEventConfigInvalid"), (error.Code, error.MessageKey));
        Assert.Empty(await RoutinesOfAsync(rig, OrgA));
    }

    // ─── The condition (AC-NF21-2) ───────────────────────────────────────────────

    [Theory]
    [InlineData("""{"version":"1.0","groups":[{"operator":"XOR","rules":[]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"nope","operator":"equals","value":"x"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"between","value":[1,2]}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"equals"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"paymentStatus","operator":"equals","value":"paid"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"totalStays","operator":"greaterThan","value":2}]}]}""")]
    [InlineData("[]")]
    [InlineData("\"channel = Airbnb\"")]
    public async Task Create_AConditionTheEvaluatorOrTheRoutinesRefuse_IsRefusedWithItsErrors(string condition)
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(condition: condition)));

        Assert.Equal((RoutineCodes.ConditionInvalid, "RoutineConditionInvalid"), (error.Code, error.MessageKey));
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<string>>(error.Extensions["errors"]));
        Assert.Empty(await RoutinesOfAsync(rig, OrgA));
    }

    [Fact]
    public async Task Create_ACondition_IsSavedAsItWasWritten()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        const string condition = """{"version":"1.0","groups":[{"operator":"OR","rules":[{"fieldId":"stayDuration","operator":"between","value":[2,5]},{"fieldId":"guestCount","operator":"greaterThan","value":3}]}]}""";

        var routine = await rig.Management(db).CreateAsync(OrgA, Command(condition: condition));

        Assert.Equal(condition, routine.ConditionJson);
    }

    // ─── The actions (AC-NF21-1) ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_NoActions_OrAnEmptyList_OrMoreThanTen_IsRefused()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var absent = Command() with { Actions = null };

        var none = await RefusedAsync(() => service.CreateAsync(OrgA, absent));
        var empty = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [])));
        var eleven = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [.. Enumerable.Repeat(CreateTodo, 11)], testMode: true)));
        var ten = await service.CreateAsync(OrgA, Command(actions: [.. Enumerable.Repeat(CreateTodo, 10)]));

        Assert.All(new[] { none, empty, eleven }, error => Assert.Equal((RoutineCodes.ActionsInvalid, "RoutineActionsInvalid"), (error.Code, error.MessageKey)));
        Assert.Equal(10, JsonDocument.Parse(ten.ActionsJson).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Create_AnActionOfAnUnknownType_OrWithAConfigurationThatIsNotAnObject_IsRefused()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);

        var unknown = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [CreateTodo, Act("DoSomething")])));
        var numeric = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [Act("3")])));
        var noType = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [Act(null)])));
        var array = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [Act("Wait", "[1]")])));
        var text = await RefusedAsync(() => service.CreateAsync(OrgA, Command(actions: [Act("Wait", "\"1 hour\"")])));

        Assert.All(new[] { unknown, numeric, noType }, e => Assert.Equal(RoutineCodes.ActionsInvalid, e.Code));
        Assert.Equal(1, unknown.Extensions["actionIndex"]);
        Assert.All(new[] { array, text }, e => Assert.Equal(RoutineCodes.ActionConfigInvalid, e.Code));
        Assert.Empty(await RoutinesOfAsync(rig, OrgA));
    }

    [Fact]
    public async Task Create_AConfigurationOfMoreThan4Kilobytes_IsRefused()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var big = $$"""{"tag":"{{new string('x', 4100)}}"}""";

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(actions: [Act("AddTag", big)], testMode: true)));

        Assert.Equal((RoutineCodes.ActionConfigInvalid, "RoutineActionConfigInvalid"), (error.Code, error.MessageKey));
        Assert.Equal(0, error.Extensions["actionIndex"]);
        Assert.Equal("AddTag", error.Extensions["type"]);
    }

    [Theory]
    [InlineData("Wait", "{}")]
    [InlineData("Wait", """{"days":0}""")]
    [InlineData("Wait", """{"days":366}""")]
    [InlineData("Wait", """{"hours":-2}""")]
    [InlineData("Wait", """{"days":"1"}""")]
    [InlineData("CreateActivity", "{}")]
    [InlineData("CreateActivity", """{"text":""}""")]
    [InlineData("CreateActivity", """{"text":"   "}""")]
    [InlineData("CreateActivity", """{"text":"Ok","dueInDays":61}""")]
    [InlineData("CreateActivity", """{"text":"Ok","dueInDays":-1}""")]
    [InlineData("CreateActivity", """{"text":"Ok","dueInDays":"2"}""")]
    public async Task Create_AConfigurationOutOfTheRulesOfItsType_IsRefused(string type, string config)
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(() => rig.Management(db).CreateAsync(OrgA, Command(actions: [CreateTodo, Act(type, config)])));

        Assert.Equal((RoutineCodes.ActionConfigInvalid, "RoutineActionConfigInvalid"), (error.Code, error.MessageKey));
        Assert.Equal(1, error.Extensions["actionIndex"]);
        Assert.Equal([2, type], error.MessageArgs);
    }

    [Fact]
    public async Task Create_ATextOf141Characters_IsRefused()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();

        var error = await RefusedAsync(
            () => rig.Management(db).CreateAsync(OrgA, Command(actions: [Act("CreateActivity", $$"""{"text":"{{new string('t', 141)}}"}""")])));

        Assert.Equal(RoutineCodes.ActionConfigInvalid, error.Code);
    }

    // ─── The limit ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_TheFiftyFirstRoutineOfAnOrg_IsRefused_AndAnotherOrgIsNotAffected()
    {
        var rig = new RoutineRig();
        await using (var seed = rig.NewDb())
        {
            for (var i = 0; i < RoutineLimits.MaxRoutinesPerOrg; i++)
                seed.Routines.Add(new Routine { OrgId = OrgA, Name = $"Routine {i}", EventType = RoutineEventType.BookingConfirmed, ActionsJson = "[]" });

            await seed.SaveChangesAsync();
        }

        await using var db = rig.NewDb();
        var service = rig.Management(db);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateAsync(OrgA, Command()));
        var other = await service.CreateAsync(OrgB, Command());

        Assert.Equal((RoutineCodes.LimitReached, "RoutineLimitReached"), (error.Code, error.MessageKey));
        Assert.Equal(RoutineLimits.MaxRoutinesPerOrg, Assert.Single(error.MessageArgs));
        Assert.Equal(RoutineLimits.MaxRoutinesPerOrg, (await RoutinesOfAsync(rig, OrgA)).Count);
        Assert.Equal(OrgB, other.OrgId);
    }

    // ─── Update ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_LeavesOutWhatTheCommandDoesNotSay()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var routine = await service.CreateAsync(
            OrgA,
            Command(condition: """{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"guestCount","operator":"greaterThan","value":1}]}]}""", testMode: true));

        // The context hands back the very object it tracks, so what the routine was is read before the change.
        var (eventType, eventConfig, conditionJson, actionsJson) = (routine.EventType, routine.EventConfig, routine.ConditionJson, routine.ActionsJson);

        await service.UpdateAsync(OrgA, routine.Id, new RoutineCommand("Nuovo nome", null, null, null, null, null, null));

        var changed = await StoredAsync(rig, routine.Id);
        Assert.Equal("Nuovo nome", changed.Name);
        Assert.Equal(eventType, changed.EventType);
        Assert.Equal(eventConfig, changed.EventConfig);
        Assert.Equal(conditionJson, changed.ConditionJson);
        Assert.Equal(actionsJson, changed.ActionsJson);
        Assert.True(changed.TestMode);
        Assert.False(changed.Active);
    }

    [Fact]
    public async Task Update_ValidatesTheWholeRoutineThatResults()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var routine = await service.CreateAsync(OrgA, Command());

        // The event type changes and the configuration of the time trigger stays: it is not valid for the new type.
        var kept = await RefusedAsync(() => service.UpdateAsync(OrgA, routine.Id, new RoutineCommand(null, "BookingConfirmed", null, null, null, null, null)));
        // Said together with its own configuration, it is.
        var together = await service.UpdateAsync(OrgA, routine.Id, new RoutineCommand(null, "BookingConfirmed", J("{}"), null, null, null, null));
        var badName = await RefusedAsync(() => service.UpdateAsync(OrgA, routine.Id, new RoutineCommand(" ", null, null, null, null, null, null)));

        Assert.Equal(RoutineCodes.EventConfigInvalid, kept.Code);
        Assert.Equal(RoutineEventType.BookingConfirmed, together.EventType);
        Assert.Equal("{}", together.EventConfig);
        Assert.Equal(RoutineCodes.NameInvalid, badName.Code);
        Assert.Equal("{}", (await RoutinesOfAsync(rig, OrgA)).Single().EventConfig);
    }

    [Fact]
    public async Task Update_ANewConditionOrNewActions_ReplaceTheOldOnes()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var routine = await service.CreateAsync(OrgA, Command());

        await service.UpdateAsync(
            OrgA,
            routine.Id,
            new RoutineCommand(
                null,
                null,
                null,
                J("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"equals","value":"Airbnb"}]}]}"""),
                [Act("Wait", """{"days":1}""")],
                null,
                null));

        var changed = await StoredAsync(rig, routine.Id);
        Assert.True(RoutineAction.TryParseList(changed.ActionsJson, out var actions));
        Assert.Equal([RoutineActionType.Wait], actions.Select(a => a.Type));
        Assert.Contains("Airbnb", changed.ConditionJson, StringComparison.Ordinal);

        // An empty condition takes the condition away: the routine runs always again, and its actions stay.
        await service.UpdateAsync(OrgA, routine.Id, new RoutineCommand(null, null, null, J("{}"), null, null, null));

        var cleared = await StoredAsync(rig, routine.Id);
        Assert.Equal("{}", cleared.ConditionJson);
        Assert.Equal(changed.ActionsJson, cleared.ActionsJson);
    }

    // ─── The switches and the delete ─────────────────────────────────────────────

    [Fact]
    public async Task SetActive_AndTestMode_SwitchTheRoutine_AndLeaveTheRestAsItWas()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var routine = await service.CreateAsync(OrgA, Command());

        var (name, actionsJson) = (routine.Name, routine.ActionsJson);

        await service.SetActiveAsync(OrgA, routine.Id, true);
        var on = await StoredAsync(rig, routine.Id);
        await service.SetTestModeAsync(OrgA, routine.Id, true);
        var test = await StoredAsync(rig, routine.Id);
        await service.SetTestModeAsync(OrgA, routine.Id, false);
        var live = await StoredAsync(rig, routine.Id);
        await service.SetActiveAsync(OrgA, routine.Id, false);
        var off = await StoredAsync(rig, routine.Id);

        Assert.True(on.Active);
        Assert.False(on.TestMode);
        Assert.True(test.TestMode);
        Assert.True(test.Active);
        Assert.False(live.TestMode);
        Assert.True(live.Active);
        Assert.False(off.Active);
        Assert.False(off.TestMode);
        Assert.Equal(name, off.Name);
        Assert.Equal(actionsJson, off.ActionsJson);
    }

    [Fact]
    public async Task Delete_RemovesTheRoutine_AndOnlyThatOne()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var doomed = await service.CreateAsync(OrgA, Command(name: "Da cancellare"));
        var kept = await service.CreateAsync(OrgA, Command(name: "Da tenere"));

        await service.DeleteAsync(OrgA, doomed.Id);

        Assert.Equal([kept.Id], (await RoutinesOfAsync(rig, OrgA)).Select(r => r.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(OrgA, doomed.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(OrgA, doomed.Id));
    }

    // ─── The log ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListExecutions_IsTheLogOfTheRoutine_NewestFirst_PagedWithItsTotal()
    {
        var rig = new RoutineRig();
        var routine = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        var other = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        await using (var seed = rig.NewDb())
        {
            for (var i = 0; i < 7; i++)
            {
                seed.RoutineExecutions.Add(new RoutineExecution
                {
                    OrgId = OrgA,
                    RoutineId = routine.Id,
                    EventId = $"event-{i}",
                    StartedAt = RoutineRig.Now.UtcDateTime.AddMinutes(i),
                    Outcome = RoutineExecutionOutcomes.Success,
                });
            }

            seed.RoutineExecutions.Add(new RoutineExecution { OrgId = OrgA, RoutineId = other.Id, EventId = "of-another-routine", StartedAt = RoutineRig.Now.UtcDateTime });
            await seed.SaveChangesAsync();
        }

        await using var db = rig.NewDb();
        var service = rig.Management(db);
        var first = await service.ListExecutionsAsync(OrgA, routine.Id, 1, 3);
        var last = await service.ListExecutionsAsync(OrgA, routine.Id, 3, 3);
        var beyond = await service.ListExecutionsAsync(OrgA, routine.Id, 9, 3);

        Assert.Equal(7, first.TotalCount);
        Assert.Equal(["event-6", "event-5", "event-4"], first.Items.Select(e => e.EventId));
        Assert.Equal(["event-0"], last.Items.Select(e => e.EventId));
        Assert.Empty(beyond.Items);
        Assert.Equal((1, 3), (first.Page, first.PageSize));
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-4, 1000, 1, 100)]
    [InlineData(2, 50, 2, 50)]
    public async Task ListExecutions_APageOrSizeOutOfRange_IsBroughtIn(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var rig = new RoutineRig();
        var routine = await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, "[]");
        await using var db = rig.NewDb();

        var result = await rig.Management(db).ListExecutionsAsync(OrgA, routine.Id, page, pageSize);

        Assert.Equal((expectedPage, expectedSize), (result.Page, result.PageSize));
    }
}
