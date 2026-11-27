using System.Reflection;
using Casazen.Core.Identity.Statistics;
using Casazen.Tests.Qualification;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US01/T02: the targets of the qualification are read from <c>Fixtures/Qualification/qualification-targets.json</c> (the proposals of the specification,
/// for the product owner to confirm), or from another file of the same shape, and never come from a number written in C#.
/// </summary>
public sealed class QualificationTargetsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-qualification-targets-" + Guid.NewGuid().ToString("N"));

    public QualificationTargetsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void QualificationTargets_Default_AreTheSpecificationProposalsReadFromTheFixture()
    {
        var targets = QualificationTargets.Load();

        Assert.True(File.Exists(QualificationTargets.DefaultPath));
        Assert.Equal(new QualificationTargets(0.0001, 0.95, 0.01, 300, 425, 500, 0.80, 0.98, 0), targets);
    }

    [Fact]
    public void QualificationTargets_AnotherFile_GivesItsOwnNumbers_SoNothingIsHardCoded()
    {
        var path = Path.Combine(_folder, "other-targets.json");
        File.WriteAllText(
            path,
            """
            { "fmrMax": 0.001, "confidence": 0.9, "apcerUpperMax": 0.05, "minAttacksPerClass": 60, "automationTarget": 90, "automationOf": 100,
              "automationWilsonLowMin": 0.7, "identifyingFieldExactnessMin": 0.9, "criticalFalseApprovalsMax": 2 }
            """);

        var targets = QualificationTargets.Load(path);

        Assert.Equal(new QualificationTargets(0.001, 0.9, 0.05, 60, 90, 100, 0.7, 0.9, 2), targets);
    }

    [Fact]
    public void QualificationTargets_TheType_HoldsNoNumberOfTheCriteria()
    {
        var statics = typeof(QualificationTargets).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(statics, field => field.FieldType == typeof(double) || field.FieldType == typeof(int) || field.FieldType == typeof(decimal) || field.FieldType == typeof(float));
    }

    [Fact]
    public void QualificationTargets_AMissingMember_IsRefusedByName_NotReadAsZero()
    {
        var exception = Assert.Throws<InvalidDataException>(() => QualificationTargets.Parse(TargetsJson(("fmrMax", null), ("criticalFalseApprovalsMax", null))));

        Assert.Equal("The qualification targets are not valid: fmrMax (missing), criticalFalseApprovalsMax (missing).", exception.Message);
        Assert.Throws<InvalidDataException>(() => QualificationTargets.Parse("{ }"));
    }

    [Fact]
    public void QualificationTargets_AValueOutOfItsRange_IsRefusedByNameAndNeverByValue()
    {
        var exception = Assert.Throws<InvalidDataException>(() => QualificationTargets.Parse(TargetsJson(
            ("fmrMax", "7.3125"),
            ("confidence", "1"),
            ("apcerUpperMax", "0"),
            ("minAttacksPerClass", "0"),
            ("criticalFalseApprovalsMax", "-4242"),
            ("automationTarget", "600"))));

        Assert.Equal(
            "The qualification targets are not valid: fmrMax (out of range), confidence (out of range), apcerUpperMax (out of range), minAttacksPerClass (out of range), "
            + "criticalFalseApprovalsMax (out of range), automationTarget (above automationOf).",
            exception.Message);
        Assert.DoesNotContain("7.3125", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("4242", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationTargets_AnUnknownMemberOrABrokenText_IsRefused_WithoutQuotingIt()
    {
        // The NAME of a member it does not know is text the author typed too, and the path of the parser holds it.
        string[] texts =
        [
            TargetsJson(("fmrMaxx", "0.5")),
            TargetsJson(("SECRET-MEMBER-NAME", "0.5")),
            TargetsJson(("SECRET MEMBER NAME", "0.5")),
            TargetsJson(("SECRET'] MEMBER[7]", "0.5")),
            "{ fmrMax: SECRET-TEXT }",
            "SECRET-TEXT",
            "null",
            string.Empty,
        ];

        foreach (var text in texts)
        {
            var exception = Assert.Throws<InvalidDataException>(() => QualificationTargets.Parse(text));

            Assert.DoesNotContain("SECRET", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QualificationTargets_ARefusedText_NamesOnlyKnownMembers_AndTheLineCountsFromOne()
    {
        // (text, message): a member the file knows is written as the model writes it, whatever the letter case; any other name is "?".
        (string Text, string Message)[] cases =
        [
            (TargetsJson(("fmrMaxx", "0.5")), "The qualification targets cannot be read (at $.?, line 1)."),
            (TargetsJson(("SECRET-MEMBER-NAME", "0.5")), "The qualification targets cannot be read (at $.?, line 1)."),
            (TargetsJson(("SECRET MEMBER NAME", "0.5")), "The qualification targets cannot be read (at $.?, line 1)."),
            ("{\n  \"fmrMax\": 0.0001,\n  \"SECRET-MEMBER-NAME\": 1\n}", "The qualification targets cannot be read (at $.?, line 3)."),
            ("""{ "FMRMAX": "x" }""", "The qualification targets cannot be read (at $.fmrMax, line 1)."),
            ("{\n  \"fmrMax\": 0.0001\n  \"confidence\": 0.95\n}", "The qualification targets cannot be read (at $, line 3)."),
            ("null", "The qualification targets cannot be read (at $, line unknown)."),
        ];

        foreach (var (text, message) in cases)
        {
            var exception = Assert.Throws<InvalidDataException>(() => QualificationTargets.Parse(text));

            Assert.Equal(message, exception.Message);
        }
    }

    [Fact]
    public void QualificationTargets_TheProposalsOfTheSpecification_CanBeMetByTheStatisticsThatJudgeThem()
    {
        var targets = QualificationTargets.Load();

        // 425 of 500 closed automatically: the lower Wilson bound clears the 80% (the example of the specification).
        Assert.True(IdentityStatistics.Wilson(targets.AutomationTarget, targets.AutomationOf).Low >= targets.AutomationWilsonLowMin);
        Assert.True(IdentityStatistics.Wilson(targets.AutomationTarget - 25, targets.AutomationOf).Low < targets.AutomationWilsonLowMin);

        // The minimum of attacks per class with none accepted is enough for the bound of a class; one accepted attack among them is not.
        Assert.True(IdentityStatistics.ClopperPearsonUpper(0, targets.MinAttacksPerClass, targets.Confidence) <= targets.ApcerUpperMax);
        Assert.True(IdentityStatistics.ClopperPearsonUpper(1, targets.MinAttacksPerClass, targets.Confidence) > targets.ApcerUpperMax);

        // About 30,000 independent impostor comparisons with no false match give the bound of the false match rate; far fewer do not.
        Assert.True(IdentityStatistics.ClopperPearsonUpper(0, 30_000, targets.Confidence) <= targets.FmrMax);
        Assert.True(IdentityStatistics.ClopperPearsonUpper(0, 29_000, targets.Confidence) > targets.FmrMax);

        // An identifying field read exactly 299 times out of 300 clears the lower bound of its exactness; 295 of 300 does not.
        Assert.True(IdentityStatistics.Wilson(299, 300).Low > targets.IdentifyingFieldExactnessMin);
        Assert.True(IdentityStatistics.Wilson(295, 300).Low < targets.IdentifyingFieldExactnessMin);
    }

    // The default targets as JSON members, with the given members replaced (a null value removes the member, a new name adds one).
    private static string TargetsJson(params (string Member, string? Value)[] changes)
    {
        var members = new Dictionary<string, string?>
        {
            ["fmrMax"] = "0.0001",
            ["confidence"] = "0.95",
            ["apcerUpperMax"] = "0.01",
            ["minAttacksPerClass"] = "300",
            ["automationTarget"] = "425",
            ["automationOf"] = "500",
            ["automationWilsonLowMin"] = "0.8",
            ["identifyingFieldExactnessMin"] = "0.98",
            ["criticalFalseApprovalsMax"] = "0",
        };

        foreach (var (member, value) in changes)
            members[member] = value;

        return "{ " + string.Join(", ", members.Where(member => member.Value is not null).Select(member => "\"" + member.Key + "\": " + member.Value)) + " }";
    }
}
