using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.OrgTeam;

/// <summary>
/// AM-02b: the perimeter of the activity log and the rules that keep personal data out of it. The log is read by people other
/// than the one who acted and kept for 12 months, so what it may hold is fixed in one catalog; these tests pin the codes that
/// are persisted, the closed list of the details, and the shape of the row, so that a free-text field cannot appear without a
/// test failing and somebody deciding.
/// </summary>
public class OrgActivityCatalogTests
{
    private static readonly Guid Org = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    // ─── What is persisted ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrgActivityType.MemberInvited, 1)]
    [InlineData(OrgActivityType.InvitationRevoked, 2)]
    [InlineData(OrgActivityType.InvitationAccepted, 3)]
    [InlineData(OrgActivityType.MemberRoleChanged, 4)]
    [InlineData(OrgActivityType.MemberDeactivated, 5)]
    [InlineData(OrgActivityType.MemberReactivated, 6)]
    [InlineData(OrgActivityType.MemberRemoved, 7)]
    [InlineData(OrgActivityType.MemberPropertyAccessChanged, 8)]
    [InlineData(OrgActivityType.AccessRequested, 9)]
    [InlineData(OrgActivityType.PlanChanged, 20)]
    [InlineData(OrgActivityType.OrgNameChanged, 21)]
    [InlineData(OrgActivityType.OrgSlugChanged, 22)]
    [InlineData(OrgActivityType.PropertyModeChangeScheduled, 40)]
    [InlineData(OrgActivityType.PropertyModeChangeCancelled, 41)]
    [InlineData(OrgActivityType.PropertyModeChanged, 42)]
    [InlineData(OrgActivityType.TrustedSupplierAdded, 60)]
    [InlineData(OrgActivityType.TrustedSupplierRemoved, 61)]
    public void Type_TheIntegerOfEveryEvent_IsPinned(OrgActivityType type, int value)
    {
        // The value is what the database holds: reordering or reusing it would rewrite history.
        Assert.Equal(value, (int)type);
    }

    [Fact]
    public void Type_EveryEventOfTheEnum_IsPinnedAbove()
    {
        Assert.Equal(17, Enum.GetValues<OrgActivityType>().Length);
    }

    [Fact]
    public void Area_AndSubjectType_TheirIntegersArePinned()
    {
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<OrgActivityArea>().Select(a => (int)a));
        Assert.Equal(
            ["Account", "ShortRent", "LongRent", "Supplier"],
            Enum.GetValues<OrgActivityArea>().Select(a => a.ToString()));
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<OrgActivitySubjectType>().Select(s => (int)s));
        Assert.Equal(
            ["Member", "Invitation", "Org", "Property", "Supplier"],
            Enum.GetValues<OrgActivitySubjectType>().Select(s => s.ToString()));
    }

    [Fact]
    public void Catalog_DescribesEveryEventOnce_AndTheRunbookStaysUnderTwentyEvents()
    {
        var described = OrgActivityCatalog.All.Select(e => e.Type).ToList();

        Assert.Equal(Enum.GetValues<OrgActivityType>().Order(), described.Order());
        Assert.Equal(described.Count, described.Distinct().Count());
        Assert.InRange(described.Count, 1, 20);
    }

    [Fact]
    public void Catalog_TheReservedEvents_AreTheFiveOfTheFeaturesThatDoNotExistYet()
    {
        var reserved = OrgActivityCatalog.All.Where(e => e.Reserved).Select(e => e.Type).Order().ToList();

        Assert.Equal(
            new[]
            {
                OrgActivityType.PropertyModeChangeScheduled,
                OrgActivityType.PropertyModeChangeCancelled,
                OrgActivityType.PropertyModeChanged,
                OrgActivityType.TrustedSupplierAdded,
                OrgActivityType.TrustedSupplierRemoved,
            }.Order(),
            reserved);
    }

    // ─── Areas and names the API speaks ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OrgActivityArea.Account, "account")]
    [InlineData(OrgActivityArea.ShortRent, "short-rent")]
    [InlineData(OrgActivityArea.LongRent, "long-rent")]
    [InlineData(OrgActivityArea.Supplier, "supplier")]
    public void AreaCode_IsTheKeyOfTheContext_AndParsesBack(OrgActivityArea area, string code)
    {
        Assert.Equal(code, OrgActivityCatalog.AreaCode(area));
        Assert.True(OrgActivityCatalog.TryParseArea(code, out var parsed));
        Assert.Equal(area, parsed);
        Assert.True(OrgActivityCatalog.TryParseArea($" {code.ToUpperInvariant()} ", out parsed));
        Assert.Equal(area, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("admin")]
    [InlineData("ShortRent")]
    public void TryParseArea_WhatIsNotACode_IsRefused(string? code)
    {
        Assert.False(OrgActivityCatalog.TryParseArea(code, out _));
    }

    [Fact]
    public void TryParseType_TheNameOfEveryEvent_ParsesCaseInsensitively()
    {
        foreach (var type in Enum.GetValues<OrgActivityType>())
        {
            Assert.True(OrgActivityCatalog.TryParseType(type.ToString(), out var parsed), type.ToString());
            Assert.Equal(type, parsed);
            Assert.True(OrgActivityCatalog.TryParseType(type.ToString().ToLowerInvariant(), out parsed));
            Assert.Equal(type, parsed);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("20")]
    [InlineData("MemberRoleChange")]
    [InlineData("Member, Invited")]
    public void TryParseType_ANumberOrAnUnknownName_IsRefused(string? name)
    {
        Assert.False(OrgActivityCatalog.TryParseType(name, out _));
    }

    // ─── No personal data ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DetailKeys_TheClosedList_HasNothingThatCouldHoldAPersonalDatum()
    {
        // A key that names a person, a contact, an amount or a text is the first step towards a log that keeps one.
        string[] forbidden =
        [
            "name", "email", "mail", "phone", "address", "note", "message", "text", "comment", "amount", "price", "iban",
            "fiscal", "slug", "token", "password", "reason", "description",
        ];

        Assert.All(OrgActivityDetailKeys.All, key =>
            Assert.DoesNotContain(forbidden, fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DetailKeys_EveryKeyOfEveryEvent_IsInTheClosedList_AndTheListHasNoDuplicates()
    {
        Assert.Equal(OrgActivityDetailKeys.All.Count, OrgActivityDetailKeys.All.Distinct().Count());
        Assert.All(
            OrgActivityCatalog.All.SelectMany(e => e.DetailKeys),
            key => Assert.Contains(key, OrgActivityDetailKeys.All));

        var declared = typeof(OrgActivityDetailKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order();
        Assert.Equal(declared, OrgActivityDetailKeys.All.Order());
    }

    [Fact]
    public void Entry_TheRowHasOnlyIdsCodesAndTheInstant_NoFreeText()
    {
        // The whole shape of the row, by name and by type. A new column (a note, a name, a message) fails here: the person who
        // adds it has to read this test, and the runbook, before the log starts keeping it.
        var columns = typeof(OrgActivityEntry).GetProperties()
            .ToDictionary(p => p.Name, p => p.PropertyType);

        Assert.Equal(
            new Dictionary<string, Type>
            {
                [nameof(OrgActivityEntry.Id)] = typeof(Guid),
                [nameof(OrgActivityEntry.OrgId)] = typeof(Guid),
                [nameof(OrgActivityEntry.When)] = typeof(DateTime),
                [nameof(OrgActivityEntry.ActorUserId)] = typeof(string),
                [nameof(OrgActivityEntry.Area)] = typeof(OrgActivityArea),
                [nameof(OrgActivityEntry.Type)] = typeof(OrgActivityType),
                [nameof(OrgActivityEntry.SubjectType)] = typeof(OrgActivitySubjectType),
                [nameof(OrgActivityEntry.SubjectId)] = typeof(string),
                [nameof(OrgActivityEntry.DetailsJson)] = typeof(string),
            }.OrderBy(c => c.Key),
            columns.OrderBy(c => c.Key));
    }

    [Fact]
    public void Entry_TheTwoIdColumns_AreBoundedIds()
    {
        foreach (var name in new[] { nameof(OrgActivityEntry.ActorUserId), nameof(OrgActivityEntry.SubjectId) })
        {
            var max = typeof(OrgActivityEntry).GetProperty(name)!.GetCustomAttribute<MaxLengthAttribute>();
            Assert.NotNull(max);
            Assert.Equal(OrgActivityCatalog.MaxIdLength, max.Length);
        }
    }

    // ─── Validate ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_EveryEventWithEveryDetailItAllows_IsAccepted()
    {
        foreach (var info in OrgActivityCatalog.All)
        {
            var details = info.DetailKeys.ToDictionary(k => k, _ => "Admin");
            var activity = new OrgActivity(Org, info.Type, "auth0|someone", Guid.NewGuid().ToString(), details);

            OrgActivityCatalog.Validate(activity);
        }
    }

    [Fact]
    public void Validate_AnActorlessEvent_IsAccepted()
    {
        OrgActivityCatalog.Validate(OrgActivity.Of(Org, OrgActivityType.PlanChanged, null, Org.ToString()));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("role ")]
    [InlineData("Role")]
    [InlineData("fromTier")]
    public void Validate_ADetailTheTypeDoesNotAllow_IsRefused(string key)
    {
        var activity = OrgActivity.Of(Org, OrgActivityType.MemberDeactivated, "auth0|a", "auth0|b", (key, "Admin"));

        Assert.Throws<ArgumentException>(() => OrgActivityCatalog.Validate(activity));
    }

    [Theory]
    [InlineData("Mario Rossi")]
    [InlineData("mario@example.com")]
    [InlineData("<b>Admin</b>")]
    [InlineData("Una nota molto lunga con molte parole che non e un codice")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-leading-dash")]
    [InlineData("accentàta")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void Validate_ADetailValueThatIsNotAShortCode_IsRefused(string value)
    {
        var activity = OrgActivity.Of(
            Org, OrgActivityType.MemberDeactivated, "auth0|a", "auth0|b", (OrgActivityDetailKeys.Role, value));

        Assert.False(OrgActivityCatalog.IsSafeDetailValue(value));
        Assert.Throws<ArgumentException>(() => OrgActivityCatalog.Validate(activity));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("short-rent")]
    [InlineData("2026-11-30")]
    [InlineData("12")]
    [InlineData("a.b_c-d")]
    public void IsSafeDetailValue_ShortCodes_AreAccepted(string value)
    {
        Assert.True(OrgActivityCatalog.IsSafeDetailValue(value));
    }

    [Fact]
    public void Validate_TheSubjectAndTheActor_AreBoundedIds()
    {
        var tooLong = new string('a', OrgActivityCatalog.MaxIdLength + 1);

        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(OrgActivity.Of(Org, OrgActivityType.MemberRemoved, "auth0|a", tooLong)));
        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(OrgActivity.Of(Org, OrgActivityType.MemberRemoved, "auth0|a", " ")));
        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(OrgActivity.Of(Org, OrgActivityType.MemberRemoved, tooLong, "auth0|b")));
        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(OrgActivity.Of(Org, OrgActivityType.MemberRemoved, string.Empty, "auth0|b")));
        OrgActivityCatalog.Validate(OrgActivity.Of(
            Org, OrgActivityType.MemberRemoved, new string('a', OrgActivityCatalog.MaxIdLength), new string('b', OrgActivityCatalog.MaxIdLength)));
    }

    [Fact]
    public void Validate_AnEventOfNoOrg_OrOfAnUnknownType_IsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(OrgActivity.Of(Guid.Empty, OrgActivityType.MemberRemoved, "auth0|a", "auth0|b")));
        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(OrgActivity.Of(Org, (OrgActivityType)999, "auth0|a", "auth0|b")));
        Assert.Throws<ArgumentException>(() =>
            OrgActivityCatalog.Validate(new OrgActivity(Org, OrgActivityType.MemberRemoved, "auth0|a", "auth0|b", Area: (OrgActivityArea)99)));
    }

    // ─── The details as the database holds them ─────────────────────────────────────────────────────────

    [Fact]
    public void Details_SerializeInKeyOrder_AndReadBack()
    {
        var json = OrgActivityDetails.Serialize(new Dictionary<string, string>
        {
            [OrgActivityDetailKeys.ToRole] = "Admin",
            [OrgActivityDetailKeys.FromRole] = "Collaborator",
        });

        Assert.Equal("{\"fromRole\":\"Collaborator\",\"toRole\":\"Admin\"}", json);
        Assert.Equal(
            new Dictionary<string, string> { ["fromRole"] = "Collaborator", ["toRole"] = "Admin" },
            OrgActivityDetails.Parse(json));
    }

    [Fact]
    public void Details_NoneAtAll_IsAnEmptyObject()
    {
        Assert.Equal("{}", OrgActivityDetails.Serialize(null));
        Assert.Equal("{}", OrgActivityDetails.Serialize(new Dictionary<string, string>()));
        Assert.Empty(OrgActivityDetails.Parse("{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"role\":{\"nested\":1}}")]
    public void Details_AStoredValueThatIsNotTheObjectWeWrite_ReadsAsNoDetails(string? json)
    {
        Assert.Empty(OrgActivityDetails.Parse(json));
    }

    [Fact]
    public void PlanChangeSource_EverySourceHasAShortCode()
    {
        Assert.Equal(["org", "staff", "subscription"], Enum.GetValues<PlanChangeSource>().Select(s => s.Code()));
        Assert.All(Enum.GetValues<PlanChangeSource>(), s => Assert.True(OrgActivityCatalog.IsSafeDetailValue(s.Code())));
    }

    // ─── The runbook lists the same events ──────────────────────────────────────────────────────────────

    [Fact]
    public void Runbook_ListsEveryEventOfTheCatalog_AndSaysWhichAreReserved()
    {
        var runbook = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "runbooks", "org-team.md"));

        foreach (var info in OrgActivityCatalog.All)
            Assert.Contains($"`{info.Type}`", runbook, StringComparison.Ordinal);

        Assert.Contains("reserved", runbook, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
