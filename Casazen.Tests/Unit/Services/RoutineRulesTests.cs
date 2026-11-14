using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E01/F04/US01/T01: the pure parts of the routine engine. How a condition is read and met (ADR-014), how the actions and the result of an execution
/// are read and written, how long a Wait lasts, how a payload is read.
/// </summary>
public class RoutineRulesTests
{
    private static readonly Guid Property = Guid.Parse("3c1d5f52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private static RoutineConditionFacts Facts() =>
        RoutineConditionFacts.OfBooking(
            Property,
            BookingSource.Airbnb,
            new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc),
            2,
            new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc));

    private static ConditionRule Rule(string fieldId, string op, object? value) => new() { FieldId = fieldId, Operator = op, Value = value };

    private static object Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ─── The facts of a booking ──────────────────────────────────────────────────

    [Fact]
    public void Facts_OfBooking_AreTheFieldsTheRoutinesRead()
    {
        var facts = Facts();

        Assert.True(facts.TryGet(RoutineConditions.PropertyId, out var property));
        Assert.Equal(Property.ToString("D"), property);
        Assert.True(facts.TryGet(RoutineConditions.Channel, out var channel));
        Assert.Equal("Airbnb", channel);
        Assert.True(facts.TryGet(RoutineConditions.StayDuration, out var nights));
        Assert.Equal(3d, nights);
        Assert.True(facts.TryGet(RoutineConditions.GuestCount, out var guests));
        Assert.Equal(2d, guests);
        Assert.True(facts.TryGet(RoutineConditions.BookingLeadTime, out var lead));
        Assert.Equal(10d, lead);
        Assert.False(facts.TryGet("paymentStatus", out _));
        Assert.False(RoutineConditionFacts.None.TryGet(RoutineConditions.Channel, out _));
    }

    /// <summary>The day a booking was made is the Rome day: 23:30 UTC on the 9th is half past midnight on the 10th in Rome.</summary>
    [Fact]
    public void Facts_BookingLeadTime_CountsFromTheRomeDayOfTheBooking_AndNeverGoesBelowZero()
    {
        var late = RoutineConditionFacts.OfBooking(
            Property,
            BookingSource.Direct,
            new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc),
            1,
            new DateTime(2026, 11, 9, 23, 30, 0, DateTimeKind.Utc));
        var afterTheArrival = RoutineConditionFacts.OfBooking(
            Property,
            BookingSource.Direct,
            new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc),
            1,
            new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc));

        Assert.True(late.TryGet(RoutineConditions.BookingLeadTime, out var days));
        Assert.Equal(10d, days);
        Assert.True(afterTheArrival.TryGet(RoutineConditions.BookingLeadTime, out var none));
        Assert.Equal(0d, none);
    }

    // ─── Reading a condition ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoCondition_IsAlways(string? json)
    {
        var parsed = RoutineConditions.Parse(json);

        Assert.True(parsed.Valid);
        Assert.Null(parsed.Condition);
    }

    [Fact]
    public void Parse_EmptyObject_IsAConditionWithNoGroup()
    {
        var parsed = RoutineConditions.Parse("{}");

        Assert.True(parsed.Valid);
        Assert.Empty(parsed.Condition!.Groups);
    }

    [Fact]
    public void Parse_TheShapeOfTheEditor_IsReadInAnyCase()
    {
        var camel = RoutineConditions.Parse(
            """{"version":"1.0","groups":[{"operator":"OR","rules":[{"fieldId":"channel","operator":"in","value":["Airbnb","Direct"]}]}]}""");
        var pascal = RoutineConditions.Parse(
            """{"Version":"1.0","Groups":[{"Operator":"AND","Rules":[{"FieldId":"guestCount","Operator":"equals","Value":2}]}]}""");

        var rule = Assert.Single(Assert.Single(camel.Condition!.Groups).Rules);
        Assert.Equal("channel", rule.FieldId);
        Assert.Equal("in", rule.Operator);
        Assert.Equal("OR", camel.Condition.Groups[0].Operator);
        Assert.Equal("guestCount", Assert.Single(Assert.Single(pascal.Condition!.Groups).Rules).FieldId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("12")]
    [InlineData("null")]
    public void Parse_NotACondition_IsInvalid(string json)
    {
        Assert.False(RoutineConditions.Parse(json).Valid);
    }

    [Fact]
    public void UnsupportedFieldErrors_NamesTheFieldsTheRoutinesDoNotRead()
    {
        var condition = RoutineConditions.Parse(
            """
            {"groups":[{"operator":"AND","rules":[
              {"fieldId":"channel","operator":"equals","value":"Airbnb"},
              {"fieldId":"paymentStatus","operator":"equals","value":"paid"},
              {"fieldId":"totalStays","operator":"greaterThan","value":2},
              {"fieldId":"nope","operator":"equals","value":"x"}]}]}
            """).Condition!;

        var errors = RoutineConditions.UnsupportedFieldErrors(condition);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("paymentStatus", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("totalStays", StringComparison.Ordinal));
        Assert.All(RoutineConditions.SupportedFields, field => Assert.True(FieldCatalog.IsValidField(field)));
    }

    /// <summary>A member nobody reads is an error, never "no condition": the rules that were meant would otherwise be dropped and the routine run for every booking.</summary>
    [Theory]
    [InlineData("""{"rules":[{"fieldId":"channel","operator":"equals","value":"Airbnb"}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","conditions":[{"field":"channel","operator":"equals","value":"Airbnb"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"channel","operator":"equals","value":"Airbnb","label":"x"}]}]}""")]
    [InlineData("""{"version":"1.0","groups":[],"extra":1}""")]
    public void Parse_AMemberNobodyReads_IsInvalid_NeverNoCondition(string json)
    {
        Assert.False(RoutineConditions.Parse(json).Valid);
    }

    /// <summary>The kind of value each operator of a field the routines read takes: what is refused is what <see cref="RoutineConditions.Satisfies"/> could never meet.</summary>
    [Theory]
    [InlineData("channel", "equals", "\"airbnb\"", true)]
    [InlineData("channel", "equals", "\"Booking.com\"", false)]
    [InlineData("channel", "equals", "[\"Airbnb\"]", false)]
    [InlineData("channel", "equals", "5", false)]
    [InlineData("channel", "notEquals", "\"Direct\"", true)]
    [InlineData("channel", "notEquals", "[\"Direct\"]", false)]
    [InlineData("channel", "in", "[\"Direct\",\"Airbnb\"]", true)]
    [InlineData("channel", "in", "\"Airbnb\"", false)]
    [InlineData("channel", "in", "[]", false)]
    [InlineData("channel", "in", "[\"Airbnb\",3]", false)]
    [InlineData("channel", "in", "[\"Airbnb\",\"Nowhere\"]", false)]
    [InlineData("channel", "notIn", "[\"Airbnb\"]", true)]
    [InlineData("channel", "notIn", "[]", false)]
    [InlineData("channel", "notIn", "[1,2]", false)]
    [InlineData("propertyId", "in", "[\"3c1d5f52-9f0e-4b8a-8d65-2c4e1f0b9a11\"]", true)]
    [InlineData("propertyId", "in", "[\"3c1d5f529f0e4b8a8d652c4e1f0b9a11\"]", false)]
    [InlineData("propertyId", "notIn", "[\"nope\"]", false)]
    [InlineData("propertyId", "notIn", "[]", false)]
    [InlineData("stayDuration", "equals", "3", true)]
    [InlineData("stayDuration", "equals", "2.5", false)]
    [InlineData("stayDuration", "equals", "\"3\"", false)]
    [InlineData("guestCount", "greaterThan", "2.5", true)]
    [InlineData("guestCount", "greaterThan", "\"2\"", false)]
    [InlineData("guestCount", "lessThan", "[2]", false)]
    [InlineData("bookingLeadTime", "between", "[0,30]", true)]
    [InlineData("bookingLeadTime", "between", "[3,3]", true)]
    [InlineData("bookingLeadTime", "between", "[30,0]", false)]
    [InlineData("bookingLeadTime", "between", "[1]", false)]
    [InlineData("bookingLeadTime", "between", "[1,2,3]", false)]
    [InlineData("bookingLeadTime", "between", "[\"a\",\"z\"]", false)]
    [InlineData("bookingLeadTime", "between", "5", false)]
    public void ValueErrors_AreTheValuesTheOperatorDoesNotTake(string fieldId, string op, string valueJson, bool valid)
    {
        var condition = RoutineConditions
            .Parse($$"""{"version":"1.0","groups":[{"operator":"AND","rules":[{"fieldId":"{{fieldId}}","operator":"{{op}}","value":{{valueJson}}}]}]}""")
            .Condition!;

        var errors = RoutineConditions.ValueErrors(condition);

        Assert.Equal(valid, errors.Count == 0);
        Assert.All(errors, error => Assert.Contains(fieldId, error, StringComparison.Ordinal));
    }

    /// <summary>What is not the business of this check (no value, a field the routines do not read, an operator the field does not take) is left to the generic validator.</summary>
    [Fact]
    public void ValueErrors_LeaveToTheGenericValidator_WhatIsNotAboutTheKindOfValue()
    {
        var condition = RoutineConditions.Parse(
            """
            {"version":"1.0","groups":[{"operator":"AND","rules":[
              {"fieldId":"channel","operator":"equals"},
              {"fieldId":"paymentStatus","operator":"equals","value":5},
              {"fieldId":"channel","operator":"between","value":5},
              {"fieldId":"nope","operator":"equals","value":5}]}]}
            """).Condition!;

        Assert.Empty(RoutineConditions.ValueErrors(condition));
    }

    /// <summary>A value of another kind is never "different": <c>notEquals</c> and <c>notIn</c> would otherwise be met for every booking.</summary>
    [Theory]
    [InlineData("notEquals", "[\"Direct\"]")]
    [InlineData("notEquals", "5")]
    [InlineData("notEquals", "true")]
    [InlineData("notIn", "[1,2]")]
    [InlineData("notIn", "[\"Direct\",1]")]
    [InlineData("notIn", "\"Direct\"")]
    public void Satisfies_NotEqualsAndNotIn_WithAValueOfAnotherKind_AreNotMet(string op, string valueJson)
    {
        Assert.False(RoutineConditions.Satisfies(Rule("channel", op, Json(valueJson)), Facts()));
    }

    // ─── Meeting a rule ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("equals", "airbnb", true)]
    [InlineData("equals", " Airbnb ", true)]
    [InlineData("equals", "Direct", false)]
    [InlineData("notEquals", "Direct", true)]
    [InlineData("notEquals", "AIRBNB", false)]
    public void Satisfies_TextIsComparedWithoutRegardToCase(string op, string value, bool expected)
    {
        Assert.Equal(expected, RoutineConditions.Satisfies(Rule("channel", op, value), Facts()));
        Assert.Equal(expected, RoutineConditions.Satisfies(Rule("channel", op, Json($"\"{value}\"")), Facts()));
    }

    [Theory]
    [InlineData("in", "[\"Direct\",\"Airbnb\"]", true)]
    [InlineData("in", "[\"Direct\"]", false)]
    [InlineData("in", "[]", false)]
    [InlineData("in", "\"Airbnb\"", false)]
    [InlineData("notIn", "[\"Direct\"]", true)]
    [InlineData("notIn", "[\"Airbnb\"]", false)]
    [InlineData("notIn", "[]", false)]
    public void Satisfies_Lists_AreNeverEmptyMeansEverything(string op, string valueJson, bool expected)
    {
        Assert.Equal(expected, RoutineConditions.Satisfies(Rule("channel", op, Json(valueJson)), Facts()));
    }

    [Theory]
    [InlineData("equals", "3", true)]
    [InlineData("equals", "3.5", false)]
    [InlineData("equals", "\"3\"", false)]
    [InlineData("greaterThan", "2", true)]
    [InlineData("greaterThan", "3", false)]
    [InlineData("lessThan", "4", true)]
    [InlineData("lessThan", "3", false)]
    [InlineData("between", "[3,3]", true)]
    [InlineData("between", "[1,2]", false)]
    [InlineData("between", "[5,1]", false)]
    [InlineData("between", "[1]", false)]
    [InlineData("between", "[1,2,3]", false)]
    [InlineData("between", "[\"a\",\"z\"]", false)]
    public void Satisfies_Numbers(string op, string valueJson, bool expected)
    {
        Assert.Equal(expected, RoutineConditions.Satisfies(Rule("stayDuration", op, Json(valueJson)), Facts()));
    }

    [Fact]
    public void Satisfies_ValuesThatAreNotJson_AreReadAsTheyAre()
    {
        Assert.True(RoutineConditions.Satisfies(Rule("stayDuration", "equals", 3), Facts()));
        Assert.True(RoutineConditions.Satisfies(Rule("stayDuration", "between", new[] { 1, 5 }), Facts()));
        Assert.True(RoutineConditions.Satisfies(Rule("channel", "in", new List<string> { "Direct", "Airbnb" }), Facts()));
        Assert.False(RoutineConditions.Satisfies(Rule("stayDuration", "equals", new object()), Facts()));
    }

    [Theory]
    [InlineData("equals", null)]
    [InlineData("notEquals", null)]
    [InlineData("in", null)]
    [InlineData("greaterThan", null)]
    [InlineData("is", null)]
    [InlineData("before", "\"2026-11-01\"")]
    [InlineData("inLast", "30")]
    [InlineData("contains", "\"A\"")]
    public void Satisfies_WhatItCannotProve_IsNotMet(string op, string? valueJson)
    {
        object? value = valueJson is null ? null : Json(valueJson);

        Assert.False(RoutineConditions.Satisfies(Rule("channel", op, value), Facts()));
    }

    [Fact]
    public void Satisfies_AFieldWithNoFact_IsNotMet_WhateverTheOperator()
    {
        var none = RoutineConditionFacts.None;

        Assert.False(RoutineConditions.Satisfies(Rule("channel", "equals", "Airbnb"), none));
        Assert.False(RoutineConditions.Satisfies(Rule("channel", "notEquals", "Airbnb"), none));
        Assert.False(RoutineConditions.Satisfies(Rule("channel", "notIn", new[] { "Airbnb" }), none));
        Assert.False(RoutineConditions.Satisfies(Rule("paymentStatus", "equals", "paid"), Facts()));
    }

    // ─── Actions ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TryParseList_TheShapeOfTheEditor()
    {
        var read = RoutineAction.TryParseList(
            """[{"type":"Wait","config":{"Days":2,"hours":3}},{"Type":"AddTag"},{"type":"messagetoguest","config":{"isCampaign":true,"templateId":"welcome"}}]""",
            out var actions);

        Assert.True(read);
        Assert.Equal([RoutineActionType.Wait, RoutineActionType.AddTag, RoutineActionType.MessageToGuest], actions.Select(a => a.Type));
        Assert.True(actions[0].TryGetInt("days", out var days));
        Assert.Equal(2, days);
        Assert.True(actions[0].TryGetInt("HOURS", out var hours));
        Assert.Equal(3, hours);
        Assert.Empty(actions[1].Config);
        Assert.True(actions[2].GetFlag("IsCampaign"));
        Assert.Equal("welcome", actions[2].GetString("templateId"));
        Assert.False(actions[2].GetFlag("templateId"));
        Assert.Null(actions[2].GetString("isCampaign"));
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    [InlineData("{}", false)]
    [InlineData("[1]", false)]
    [InlineData("[{\"config\":{}}]", false)]
    [InlineData("[{\"type\":3}]", false)]
    [InlineData("[{\"type\":\"7\"}]", false)]
    [InlineData("[{\"type\":\"Nope\"}]", false)]
    [InlineData("[{\"type\":\"Wait\",\"config\":[1]}]", false)]
    [InlineData("[{\"type\":\"Wait\",\"config\":null}]", true)]
    public void TryParseList_OnlyAListOfKnownActionsIsRead(string json, bool expected)
    {
        Assert.Equal(expected, RoutineAction.TryParseList(json, out _));
    }

    [Fact]
    public void TryGetInt_OnlyAWholeNumber()
    {
        RoutineAction.TryParseList("""[{"type":"Wait","config":{"a":1,"b":1.5,"c":"1","d":3000000000,"e":null}}]""", out var actions);
        var config = actions[0];

        Assert.True(config.TryGetInt("a", out var a));
        Assert.Equal(1, a);
        Assert.False(config.TryGetInt("b", out _));
        Assert.False(config.TryGetInt("c", out _));
        Assert.False(config.TryGetInt("d", out _));
        Assert.False(config.TryGetInt("e", out _));
        Assert.False(config.TryGetInt("missing", out _));
    }

    [Theory]
    [InlineData("""{"hours":1}""", 1)]
    [InlineData("""{"days":1}""", 24)]
    [InlineData("""{"days":2,"hours":3}""", 51)]
    [InlineData("""{"days":365}""", 365 * 24)]
    [InlineData("""{"days":364,"hours":24}""", 365 * 24)]
    [InlineData("""{"days":365,"hours":1}""", null)]
    [InlineData("""{"days":0,"hours":0}""", null)]
    [InlineData("{}", null)]
    [InlineData("""{"days":-1,"hours":30}""", null)]
    [InlineData("""{"days":1.5}""", null)]
    [InlineData("""{"hours":"1"}""", null)]
    [InlineData("""{"hours":null}""", null)]
    public void WaitDelay_IsDaysAndHours_FromAnHourToAYear(string configJson, int? expectedHours)
    {
        RoutineAction.TryParseList($"[{{\"type\":\"Wait\",\"config\":{configJson}}}]", out var actions);

        var delay = RoutineActionRules.WaitDelay(actions[0]);

        TimeSpan? expected = expectedHours is { } hours ? TimeSpan.FromHours(hours) : null;
        Assert.Equal(expected, delay);
    }

    // ─── The result of an execution ──────────────────────────────────────────────

    [Fact]
    public void Result_RoundTripsThroughItsJson()
    {
        var result = new RoutineExecutionResult(
            [
                new RoutineActionResult(0, RoutineActionType.MessageToGuest, RoutineActionOutcomes.Blocked, RoutineCodes.AirbnbNotConfirmed),
                new RoutineActionResult(1, RoutineActionType.Wait, RoutineActionOutcomes.Waiting, null),
            ],
            """{"bookingId":"6c1f2f0e-8f7e-4f42-9c1d-0d6b2f3e4a5b"}""",
            2,
            RoutineCodes.Inactive);

        var parsed = RoutineExecutionResult.Parse(result.ToJson());

        Assert.Equal(result.Actions, parsed.Actions);
        Assert.Equal(result.PayloadJson, parsed.PayloadJson);
        Assert.Equal(2, parsed.NextActionIndex);
        Assert.Equal(RoutineCodes.Inactive, parsed.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"ExecutedActions":["Wait: resume at 2026-11-12"],"Errors":[]}""")]
    [InlineData("""{"actions":[{"index":0,"type":"NoSuchAction","outcome":"done"}]}""")]
    public void Parse_WhatIsNotAResult_IsEmpty_NeverAnError(string? json)
    {
        var parsed = RoutineExecutionResult.Parse(json);

        Assert.Empty(parsed.Actions);
        Assert.Null(parsed.PayloadJson);
        Assert.Equal(0, parsed.NextActionIndex);
        Assert.Null(parsed.Code);
    }

    /// <summary>The saved payload is the engine's state for the resume: the log the host reads never shows it.</summary>
    [Fact]
    public void WithoutPayload_LeavesTheSavedPayloadOut_AndKeepsTheRest()
    {
        var saved = new RoutineExecutionResult(
            [new RoutineActionResult(0, RoutineActionType.Wait, RoutineActionOutcomes.Waiting, null)],
            """{"bookingId":"6c1f2f0e-8f7e-4f42-9c1d-0d6b2f3e4a5b"}""",
            1).ToJson();

        var shown = RoutineExecutionResult.WithoutPayload(saved);

        Assert.DoesNotContain("bookingId", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadJson", shown, StringComparison.Ordinal);
        var parsed = RoutineExecutionResult.Parse(shown);
        Assert.Single(parsed.Actions);
        Assert.Equal(1, parsed.NextActionIndex);
        Assert.Equal("{\"actions\":[],\"nextActionIndex\":0}", RoutineExecutionResult.WithoutPayload("not json"));
    }

    // ─── The payload ─────────────────────────────────────────────────────────────

    [Fact]
    public void Payload_RoundTripsThroughItsJson_NamesInAnyCase()
    {
        var bookingId = Guid.NewGuid();
        var payload = RoutinePayload.Normalize(new { BookingId = bookingId, Source = "Airbnb", Nights = 3, Nested = new { A = 1 } });

        var restored = RoutinePayload.FromJson(RoutinePayload.ToJson(payload));

        Assert.True(RoutinePayload.TryGetId(restored, "bookingid", out var id));
        Assert.Equal(bookingId, id);
        Assert.Equal(payload.Count, restored.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a text")]
    [InlineData(5)]
    public void Payload_ThatIsNotAnObject_IsEmpty(object? payload)
    {
        Assert.Empty(RoutinePayload.Normalize(payload));
        Assert.Empty(RoutinePayload.FromJson(payload as string));
    }

    [Fact]
    public void TryGetId_OnlyARealId()
    {
        var id = Guid.NewGuid();
        var payload = RoutinePayload.Normalize(new Dictionary<string, object?>
        {
            ["guid"] = id,
            ["text"] = id.ToString(),
            ["json"] = Json($"\"{id}\""),
            ["empty"] = Guid.Empty,
            ["number"] = 5,
            ["nothing"] = null,
            ["garbage"] = "not-a-guid",
        });

        foreach (var name in new[] { "guid", "text", "json" })
        {
            Assert.True(RoutinePayload.TryGetId(payload, name, out var read));
            Assert.Equal(id, read);
        }

        foreach (var name in new[] { "empty", "number", "nothing", "garbage", "missing" })
            Assert.False(RoutinePayload.TryGetId(payload, name, out _));
    }
}
