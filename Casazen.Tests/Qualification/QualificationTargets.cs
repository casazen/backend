using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Tests.Qualification;

// E74/F01/US01/T02 (NF-198, NF-199, D12): the numbers the qualification is judged against (the release criteria of the specification, "Criteri di rilascio
// proposti"). NOT ONE of them is written in C#: they are read from Fixtures/Qualification/qualification-targets.json, which holds the PROPOSALS of the
// specification for the product owner to confirm or replace (NF-199), or from another file of the same shape, so that a decision changes a file and never the
// code. Every member must be there and sensible: nothing falls back to a default, because a silent default would be a criterion nobody agreed.

/// <summary>
/// The criteria of the qualification. <see cref="FmrMax"/>: the highest acceptable upper bound of the false match rate between different persons, at
/// <see cref="Confidence"/> (one-sided, Clopper-Pearson). <see cref="ApcerUpperMax"/>: the same for the share of attacks accepted, PER attack class, with at
/// least <see cref="MinAttacksPerClass"/> independent attacks in the class. <see cref="AutomationTarget"/> automatic closures out of
/// <see cref="AutomationOf"/> consecutive check-ins, with a lower Wilson bound of at least <see cref="AutomationWilsonLowMin"/>.
/// <see cref="IdentifyingFieldExactnessMin"/>: the lowest acceptable lower Wilson bound of the exactness of an identifying field.
/// <see cref="CriticalFalseApprovalsMax"/>: how many false approvals on a critical case are tolerated before the combination is suspended (0).
/// </summary>
public sealed record QualificationTargets(
    double FmrMax,
    double Confidence,
    double ApcerUpperMax,
    int MinAttacksPerClass,
    int AutomationTarget,
    int AutomationOf,
    double AutomationWilsonLowMin,
    double IdentifyingFieldExactnessMin,
    int CriticalFalseApprovalsMax)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The targets file that ships with the tests (<c>Fixtures/Qualification/qualification-targets.json</c>, copied to the output folder).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Qualification", "qualification-targets.json");

    /// <summary>Reads the targets from <paramref name="path"/>, or from <see cref="DefaultPath"/> when none is given.</summary>
    public static QualificationTargets Load(string? path = null)
    {
        return Parse(File.ReadAllText(path ?? DefaultPath));
    }

    /// <summary>
    /// Reads the targets from JSON text. A missing member, a value out of its range or an unknown member throws <see cref="InvalidDataException"/>, which names
    /// the members (never their values).
    /// </summary>
    public static QualificationTargets Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        TargetsFile file;
        try
        {
            file = JsonSerializer.Deserialize<TargetsFile>(json, Json) ?? throw new InvalidDataException("The qualification targets cannot be read (at $).");
        }
        catch (JsonException exception)
        {
            // Only the place is kept: the message of the parser may quote the text.
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"The qualification targets cannot be read (at {exception.Path ?? "$"})."));
        }

        var problems = new List<string>();
        var targets = new QualificationTargets(
            Proportion(file.FmrMax, "fmrMax", problems),
            Proportion(file.Confidence, "confidence", problems),
            Proportion(file.ApcerUpperMax, "apcerUpperMax", problems),
            AtLeast(file.MinAttacksPerClass, 1, "minAttacksPerClass", problems),
            AtLeast(file.AutomationTarget, 1, "automationTarget", problems),
            AtLeast(file.AutomationOf, 1, "automationOf", problems),
            Proportion(file.AutomationWilsonLowMin, "automationWilsonLowMin", problems),
            Proportion(file.IdentifyingFieldExactnessMin, "identifyingFieldExactnessMin", problems),
            AtLeast(file.CriticalFalseApprovalsMax, 0, "criticalFalseApprovalsMax", problems));

        if (targets.AutomationTarget > targets.AutomationOf)
            problems.Add("automationTarget (above automationOf)");

        if (problems.Count > 0)
            throw new InvalidDataException("The qualification targets are not valid: " + string.Join(", ", problems) + ".");

        return targets;
    }

    // A rate, a bound or a confidence: strictly between 0 and 1 (a criterion of exactly 0 or 1 is no criterion).
    private static double Proportion(double? value, string name, List<string> problems)
    {
        if (value is null)
        {
            problems.Add(name + " (missing)");
            return 0.0;
        }

        if (!(value.Value > 0.0 && value.Value < 1.0))
            problems.Add(name + " (out of range)");

        return value.Value;
    }

    private static int AtLeast(int? value, int minimum, string name, List<string> problems)
    {
        if (value is null)
        {
            problems.Add(name + " (missing)");
            return 0;
        }

        if (value.Value < minimum)
            problems.Add(name + " (out of range)");

        return value.Value;
    }

    /// <summary>The members of the file, all optional so that a missing one is reported by name instead of read as 0. <c>source</c> is a note for the reader.</summary>
    internal sealed record TargetsFile(
        string? Source,
        double? FmrMax,
        double? Confidence,
        double? ApcerUpperMax,
        int? MinAttacksPerClass,
        int? AutomationTarget,
        int? AutomationOf,
        double? AutomationWilsonLowMin,
        double? IdentifyingFieldExactnessMin,
        int? CriticalFalseApprovalsMax);
}
