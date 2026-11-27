using Xunit;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12): the opt-in runs of the qualification toolkit. A real run of the pipeline on a dataset takes hours and needs a dataset that is not in the
// repository (it is public, and no photo of a person or of a document is ever committed), so those runs are Facts that skip with their reason unless the person who runs
// them says where the dataset is. These variables belong to test runs only: they are not a configuration key of the application, and CI sets none of them, so CI runs
// the smoke test and the unit tests only.

/// <summary>
/// The environment variables of the opt-in qualification runs, and the rule that turns them into a skip reason. <see cref="ManifestVariable"/> is the dataset manifest,
/// <see cref="SplitVariable"/> the split to run, <see cref="OutVariable"/> the folder the results are written to; <see cref="LockVariable"/> (optional, <c>lock.json</c> in
/// the output folder by default) is the frozen lock of the final test, and <see cref="FreezeVariable"/> (<c>1</c>) is the explicit act that writes that lock instead of
/// running the final test. Nothing here reads a file, and nothing here is printed but names.
/// </summary>
public static class QualificationEnvironment
{
    /// <summary>The path of the manifest of the dataset (<c>QualificationDatasetManifest</c>): required by every run.</summary>
    public const string ManifestVariable = "CASAZEN_QUALIFICATION_MANIFEST";

    /// <summary>The split to run, a code of <see cref="QualificationSplits"/>: the development or calibration runs and the final test each take their own.</summary>
    public const string SplitVariable = "CASAZEN_QUALIFICATION_SPLIT";

    /// <summary>The folder the results (<c>results.json</c>, the proposed policy, the lock, the report) are written to: required by every run.</summary>
    public const string OutVariable = "CASAZEN_QUALIFICATION_OUT";

    /// <summary>The path of the lock the final test checks (<c>lock.json</c> in the output folder when it is not set).</summary>
    public const string LockVariable = "CASAZEN_QUALIFICATION_LOCK";

    /// <summary><c>1</c> or <c>true</c> turns the final test into the act that freezes the configuration into the lock (and keeps the final test from running).</summary>
    public const string FreezeVariable = "CASAZEN_QUALIFICATION_FREEZE";

    /// <summary>
    /// Why a run must be skipped, or null when it can run. <paramref name="read"/> reads a variable (null or blank when it is not set); <paramref name="splits"/> are the splits the
    /// run serves (none: it takes no split); <paramref name="freeze"/> is true for the run that writes the lock, which runs only when <see cref="FreezeVariable"/> is set, and
    /// no other run does then.
    /// </summary>
    public static string? SkipReason(Func<string, string?> read, IReadOnlyList<string> splits, bool freeze)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(splits);

        const string OptIn = "A real qualification run is opt-in: it needs a local dataset and takes hours, so CI never runs it.";
        if (IsBlank(read(ManifestVariable)))
            return $"{OptIn} Set {ManifestVariable} (the path of the dataset manifest), {SplitVariable} and {OutVariable} to run it.";

        if (IsBlank(read(OutVariable)))
            return $"{OptIn} Set {OutVariable} (the folder the results are written to) to run it.";

        var freezing = IsFreeze(read(FreezeVariable));
        if (freeze != freezing)
        {
            return freeze
                ? $"Freezing the configuration into the lock is an explicit act: set {FreezeVariable}=1 to run it."
                : $"{FreezeVariable} is set: only the run that writes the lock runs now, not the final test.";
        }

        if (splits.Count == 0)
            return null;

        var split = read(SplitVariable)?.Trim();
        if (IsBlank(split))
            return $"{OptIn} Set {SplitVariable} to one of: {string.Join(", ", splits)}.";

        return splits.Contains(split!, StringComparer.Ordinal)
            ? null
            : $"{SplitVariable} names a split this run does not serve: it takes {string.Join(", ", splits)} (the split of the final test belongs to the final test, and the others to the runner).";
    }

    /// <summary>True when <paramref name="value"/> asks for the freeze: <c>1</c> or <c>true</c>, whatever the letter case.</summary>
    public static bool IsFreeze(string? value)
    {
        var text = value?.Trim();
        return string.Equals(text, "1", StringComparison.Ordinal) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The manifest of the run (the variable must be set: the attribute skips the run when it is not).</summary>
    public static string ManifestPath => Required(ManifestVariable);

    /// <summary>The split of the run (the variable must be set).</summary>
    public static string Split => Required(SplitVariable);

    /// <summary>The folder the results are written to (the variable must be set).</summary>
    public static string OutDirectory => Required(OutVariable);

    /// <summary>The lock the final test checks: the variable when it is set, else <c>lock.json</c> in the output folder.</summary>
    public static string LockPath => Read(LockVariable) is { } path && !IsBlank(path) ? path.Trim() : Path.Combine(OutDirectory, "lock.json");

    /// <summary>The value of a variable of the process; null when it is not set.</summary>
    internal static string? Read(string variable) => Environment.GetEnvironmentVariable(variable);

    private static string Required(string variable)
    {
        var value = Read(variable);
        return IsBlank(value) ? throw new InvalidOperationException($"{variable} is not set.") : value!.Trim();
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>
/// A fact of a real qualification run: reported as skipped, with the reason, unless <c>CASAZEN_QUALIFICATION_MANIFEST</c> and <c>CASAZEN_QUALIFICATION_OUT</c> are set and the
/// split the run serves is the one <c>CASAZEN_QUALIFICATION_SPLIT</c> names (see <see cref="QualificationEnvironment"/>). It is the opt-in of the long runs: CI sets none of the
/// variables, so the long runs never run there and are never reported as passed either.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class QualificationFactAttribute : FactAttribute
{
    /// <summary>A run that serves the <paramref name="splits"/> (codes of <see cref="QualificationSplits"/>); none for a run that takes no split.</summary>
    public QualificationFactAttribute(params string[] splits)
    {
        Splits = splits;
        if (QualificationEnvironment.SkipReason(QualificationEnvironment.Read, splits, freeze: false) is { } reason)
            Skip = reason;
    }

    /// <summary>The splits the run serves.</summary>
    public IReadOnlyList<string> Splits { get; }
}

/// <summary>
/// The fact of the explicit act that freezes the configuration into the lock (<c>lock.json</c>): it runs only when <c>CASAZEN_QUALIFICATION_FREEZE</c> is set (and then the
/// final test, which shares the folder, does not), so that no run of the final test can write the lock it is about to be checked against.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class QualificationFreezeFactAttribute : FactAttribute
{
    public QualificationFreezeFactAttribute()
    {
        if (QualificationEnvironment.SkipReason(QualificationEnvironment.Read, [], freeze: true) is { } reason)
            Skip = reason;
    }
}
