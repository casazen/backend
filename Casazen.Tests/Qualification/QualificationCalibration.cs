using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Identity.Statistics;
using Casazen.Core.Options;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T02 (NF-198, D12): the calibration output. From the results of a run on the CALIBRATION split the toolkit proposes the thresholds the policy freezes: the acceptance of the
// similarity at the target false match rate of qualification-targets.json, the floor of the review under it, and the least liveness at the target rate of attacks accepted. The proposal is a
// NEW policy file, identity-policy-<yyyy.MM>-<n>.json, not provisional, written in the output folder: a person reviews it, commits it and pins its canonical hash (the runbook), and only
// then is the configuration frozen and the final test run. The toolkit NEVER edits a committed policy file: a qualified file is never changed, a new calibration is a new file.
// Nothing here reads a picture or a document: the inputs are the scores and the codes of anonymous cases.

/// <summary>What the proposal rests on, in numbers: the comparisons of the two kinds and the attacks of each class (and how many of them got as far as the liveness).</summary>
public sealed record QualificationCalibrationEvidence(
    long ImpostorComparisons,
    long GenuineComparisons,
    IReadOnlyDictionary<string, int> AttacksByClass,
    IReadOnlyDictionary<string, int> AttacksReachingLiveness);

/// <summary>
/// The outcome of a calibration: either a proposal (the thresholds, the file written and its canonical hash, which members of the policy changed) or the codes of what the evidence lacks.
/// A proposal is never an approval: a person reviews the file.
/// </summary>
public sealed record QualificationCalibrationProposal(
    bool Proposed,
    IReadOnlyList<string> Codes,
    string? PolicyVersion,
    string? PolicyFile,
    string? PolicyCanonicalHash,
    QualificationThresholds? Thresholds,
    IReadOnlyList<string> ChangedMembers,
    QualificationCalibrationEvidence Evidence);

/// <summary>The codes of a calibration that cannot propose a policy (never renamed or reused; a new one goes at the end).</summary>
public static class QualificationCalibrationCodes
{
    /// <summary>The results are not those of the calibration split: nothing is calibrated on the development split (it is for looking) or on the test split (it is for the final test).</summary>
    public const string WrongSplit = "calibration_wrong_split";

    /// <summary>Too few impostor comparisons to show the false match rate of the target: even with no false match its upper bound would be above it.</summary>
    public const string ImpostorTrialsInsufficient = "calibration_impostor_trials_insufficient";

    /// <summary>The acceptance that reaches the target would be above 1: some impostor scores as high as it can be.</summary>
    public const string AcceptUnreachable = "calibration_accept_unreachable";

    /// <summary>No genuine comparison with a similarity: the floor of the review cannot be told.</summary>
    public const string GenuineTrialsMissing = "calibration_genuine_trials_missing";

    /// <summary>Too few attacks of a class (the code is followed by <c>:</c> and the class) to show the limit of the target for it, or none of a class the specification names.</summary>
    public const string AttacksInsufficient = "calibration_attacks_insufficient";

    /// <summary>The least liveness that reaches the target would be above 1: some attacks score as high as it can be.</summary>
    public const string LivenessUnreachable = "calibration_liveness_unreachable";

    /// <summary>The thresholds that were found do not make a valid policy (the policy file is not written).</summary>
    public const string PolicyInvalid = "calibration_policy_invalid";
}

/// <summary>The calibration of the thresholds of the policy from the results of the calibration split (see the comments at the top of the file).</summary>
public static class QualificationCalibration
{
    /// <summary>The name of the summary of the proposal in the output folder (written whether or not a policy could be proposed).</summary>
    public const string ProposalFileName = "calibration-proposal.json";

    private static readonly Regex VersionPattern = new(@"^identity-policy-(?<month>\d{4}\.\d{2})-(?<number>\d+)(\.json)?$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions PolicyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Proposes the thresholds of a new policy from <paramref name="calibration"/> (the results of the calibration split) and the targets, as a copy of the policy <paramref name="active"/> in
    /// which only <c>version</c>, <c>provisional</c> (false) and the thresholds change, and writes it to <paramref name="outDirectory"/> as <c>identity-policy-&lt;yyyy.MM&gt;-&lt;n&gt;.json</c> (the
    /// month of the clock in Rome, <c>n</c> above every number of the month that the active policy, <paramref name="protectedFolders"/> and the output folder already use). The rules:
    /// <list type="bullet">
    /// <item><description><b>accept</b>: the lowest threshold of the decision score (the aggregate similarity, or 0 when a frame is under its minimum) that lets at most as many impostor comparisons through as the
    /// target allows: the largest number of false matches whose Clopper-Pearson upper bound is still under <c>fmrMax</c> at the confidence of the targets. With no impostor score above 0 the active acceptance is kept.</description></item>
    /// <item><description><b>review floor</b>: the percentile of the genuine similarities that leaves <c>1 - confidence</c> of them under it (nearest rank), never above the acceptance.</description></item>
    /// <item><description><b>minimum of each frame</b>: the active one, never above the acceptance.</description></item>
    /// <item><description><b>least liveness</b>: the lowest score that lets at most as many attacks of every class through (those that were valid, had their frames chosen and judged) as the target allows,
    /// each class with at least <c>minAttacksPerClass</c> attacks; no attack that got as far as the liveness leaves the active minimum as it is.</description></item>
    /// </list>
    /// Not enough evidence gives the codes of <see cref="QualificationCalibrationCodes"/> and no file. <b>A committed policy is never touched</b>: the file is created (never replaced) in the output
    /// folder, and an output folder that is one of <paramref name="protectedFolders"/> (where the committed policy files are) is refused with <see cref="QualificationRunCodes.OutputFolderInvalid"/>.
    /// </summary>
    public static QualificationCalibrationProposal Propose(
        QualificationResults calibration,
        QualificationTargets targets,
        IdentityDecisionPolicy active,
        IReadOnlyList<string> protectedFolders,
        string outDirectory,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(protectedFolders);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        if (protectedFolders.Any(folder => SamePlace(folder, outDirectory)))
            throw new QualificationRunException(QualificationRunCodes.OutputFolderInvalid, "policy_folder");

        var impostor = QualificationMetrics.ImpostorDecisionScores(calibration.Cases, calibration.ImpostorTrials, active.Match.MinEachFrame);
        var genuine = calibration.Cases
            .Where(QualificationMetrics.IsGenuineOwn)
            .Select(candidate => candidate.Presence?.Similarity)
            .OfType<double>()
            .Where(similarity => similarity > 0)
            .ToList();
        var attacks = calibration.Cases.Where(candidate => candidate.Kind == QualificationCaseKinds.Attack && candidate.AttackClass is not null && candidate.Presence is not null).ToList();
        var evidence = new QualificationCalibrationEvidence(
            impostor.Count,
            genuine.Count,
            CountBy(attacks, _ => true),
            CountBy(attacks, ReachedLiveness));

        var codes = new List<string>();
        if (!string.Equals(calibration.Split, QualificationSplits.Calibration, StringComparison.Ordinal))
            codes.Add(QualificationCalibrationCodes.WrongSplit);

        var accept = Accept(impostor, targets, active, codes);
        var floor = ReviewFloor(genuine, targets, accept, codes);
        var liveness = LivenessMinimum(attacks, targets, active, codes);
        if (codes.Count > 0 || accept is null || floor is null || liveness is null)
            return Finish(outDirectory, new QualificationCalibrationProposal(false, codes, null, null, null, null, [], evidence));

        // The structure of a policy is not a calibration: the thresholds go into a copy of the active policy, which has to be valid as a whole.
        var minEachFrame = Math.Min(active.Match.MinEachFrame, accept.Value);
        var month = clock.TodayInRomeAsDateOnly().ToString("yyyy.MM", CultureInfo.InvariantCulture);
        var version = NextVersion(month, active.Version, protectedFolders.Append(outDirectory));
        var proposed = active with
        {
            Version = version,
            Provisional = false,
            Match = active.Match with { Accept = accept.Value, ReviewFloor = floor.Value, MinEachFrame = minEachFrame },
            Presence = active.Presence with { Liveness = active.Presence.Liveness with { MinScore = liveness.Value } },
        };
        if (IdentityPolicyProvider.Validate(proposed).Count > 0)
            return Finish(outDirectory, new QualificationCalibrationProposal(false, [QualificationCalibrationCodes.PolicyInvalid], null, null, null, null, [], evidence));

        Directory.CreateDirectory(outDirectory);
        var path = Path.Combine(outDirectory, version + ".json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, proposed, PolicyJson);
        }

        // The file has to be one the application reads: its canonical hash is what a person pins, and a provider pinned to it must find a valid, calibrated policy.
        var hash = IdentityPolicyProvider.CanonicalSha256(File.ReadAllBytes(path));
        var options = Options.Create(new IdentityVerificationOptions { PolicyFile = path, PolicySha256 = hash ?? string.Empty });
        var snapshot = new IdentityPolicyProvider(options, NullLogger<IdentityPolicyProvider>.Instance, outDirectory).Snapshot;
        if (hash is null || !snapshot.IsReady || snapshot.Provisional || !string.Equals(snapshot.Hash, hash, StringComparison.Ordinal) || !SameThresholds(snapshot.Policy, proposed))
        {
            File.Delete(path);
            return Finish(outDirectory, new QualificationCalibrationProposal(false, [QualificationCalibrationCodes.PolicyInvalid], null, null, null, null, [], evidence));
        }

        var changed = new List<string> { "version", "provisional" };
        AddIfChanged(changed, "match.accept", active.Match.Accept, proposed.Match.Accept);
        AddIfChanged(changed, "match.reviewFloor", active.Match.ReviewFloor, proposed.Match.ReviewFloor);
        AddIfChanged(changed, "match.minEachFrame", active.Match.MinEachFrame, proposed.Match.MinEachFrame);
        AddIfChanged(changed, "presence.liveness.minScore", active.Presence.Liveness.MinScore, proposed.Presence.Liveness.MinScore);
        return Finish(
            outDirectory,
            new QualificationCalibrationProposal(true, [], version, Path.GetFileName(path), hash, QualificationThresholds.From(proposed), changed, evidence));
    }

    /// <summary>
    /// The largest number of errors <paramref name="n"/> independent trials may show with the upper bound of the rate still at most <paramref name="upperMax"/>: -1 when even no error does
    /// not (too few trials). The upper bound grows with the errors, so the largest number is found by halving.
    /// </summary>
    public static long MaxErrorsAllowed(long n, double upperMax, double confidence)
    {
        if (n <= 0 || IdentityStatistics.ClopperPearsonUpper(0, n, confidence) > upperMax)
            return -1;

        // With every trial wrong the bound is 1, above any limit: the answer is below n.
        long low = 0;
        var high = n - 1;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (IdentityStatistics.ClopperPearsonUpper(middle, n, confidence) <= upperMax)
                low = middle;
            else
                high = middle - 1;
        }

        return low;
    }

    // ───── the thresholds ─────

    private static double? Accept(List<double> impostor, QualificationTargets targets, IdentityDecisionPolicy active, List<string> codes)
    {
        var allowed = MaxErrorsAllowed(impostor.Count, targets.FmrMax, targets.Confidence);
        if (allowed < 0)
        {
            codes.Add(QualificationCalibrationCodes.ImpostorTrialsInsufficient);
            return null;
        }

        // The first impostor that must be kept out is the one after the number the target allows, from the highest score down: the threshold is just above its score. With no score above 0 the
        // impostors are kept out by the minimum of each frame already, and the acceptance of the active policy stays as it is.
        var descending = impostor.OrderByDescending(score => score).ToList();
        var first = descending[(int)allowed];
        if (first <= 0)
            return active.Match.Accept;

        var accept = Math.BitIncrement(first);
        if (accept > 1.0)
        {
            codes.Add(QualificationCalibrationCodes.AcceptUnreachable);
            return null;
        }

        return accept;
    }

    private static double? ReviewFloor(List<double> genuine, QualificationTargets targets, double? accept, List<string> codes)
    {
        if (genuine.Count == 0)
        {
            codes.Add(QualificationCalibrationCodes.GenuineTrialsMissing);
            return null;
        }

        var floor = IdentityStatistics.Percentile(genuine, (1.0 - targets.Confidence) * 100.0)!.Value;
        return accept is { } limit ? Math.Min(floor, limit) : floor;
    }

    private static double? LivenessMinimum(List<QualificationCaseResult> attacks, QualificationTargets targets, IdentityDecisionPolicy active, List<string> codes)
    {
        double? constraint = null;
        var insufficient = false;
        var classes = QualificationMetrics.CoreAttackClasses.Concat(attacks.Select(attack => attack.AttackClass!)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var attackClass in classes)
        {
            var ofClass = attacks.Where(attack => attack.AttackClass == attackClass).ToList();
            var allowed = ofClass.Count < targets.MinAttacksPerClass ? -1 : MaxErrorsAllowed(ofClass.Count, targets.ApcerUpperMax, targets.Confidence);
            if (allowed < 0)
            {
                codes.Add(QualificationCalibrationCodes.AttacksInsufficient + ":" + attackClass);
                insufficient = true;
                continue;
            }

            // An attack that was not valid, or whose frames could not be chosen, never reached the liveness: it is kept out whatever the minimum is.
            var scores = ofClass.Where(ReachedLiveness).Select(attack => attack.Presence!.LivenessScores.Min()).OrderByDescending(score => score).ToList();
            if (scores.Count > allowed)
                constraint = Math.Max(constraint ?? 0.0, Math.BitIncrement(scores[(int)allowed]));
        }

        if (insufficient)
            return null;

        if (constraint is > 1.0)
        {
            codes.Add(QualificationCalibrationCodes.LivenessUnreachable);
            return null;
        }

        return constraint ?? active.Presence.Liveness.MinScore;
    }

    private static bool ReachedLiveness(QualificationCaseResult attack)
    {
        return attack.Presence is { ValidationPassed: true, SelectionFailure: null, LivenessScores.Count: > 0 };
    }

    private static Dictionary<string, int> CountBy(IReadOnlyList<QualificationCaseResult> attacks, Func<QualificationCaseResult, bool> keep)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var attack in attacks.Where(keep))
            counts[attack.AttackClass!] = counts.GetValueOrDefault(attack.AttackClass!) + 1;

        return new Dictionary<string, int>(counts, StringComparer.Ordinal);
    }

    // ───── the file ─────

    /// <summary>The version of the next policy: <c>identity-policy-&lt;month&gt;-&lt;n&gt;</c> with <c>n</c> above every number of that month in the names of the files of <paramref name="folders"/> and in <paramref name="activeVersion"/>.</summary>
    private static string NextVersion(string month, string activeVersion, IEnumerable<string> folders)
    {
        var names = new List<string> { activeVersion };
        foreach (var folder in folders.Where(Directory.Exists))
            names.AddRange(Directory.EnumerateFiles(folder, "*.json").Select(file => Path.GetFileName(file)));

        var highest = 0;
        foreach (var name in names)
        {
            var match = VersionPattern.Match(name);
            if (match.Success && match.Groups["month"].Value == month && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                highest = Math.Max(highest, number);
        }

        return string.Create(CultureInfo.InvariantCulture, $"identity-policy-{month}-{highest + 1}");
    }

    private static QualificationCalibrationProposal Finish(string outDirectory, QualificationCalibrationProposal proposal)
    {
        Directory.CreateDirectory(outDirectory);
        File.WriteAllText(Path.Combine(outDirectory, ProposalFileName), JsonSerializer.Serialize(proposal, QualificationResults.JsonOptions));
        return proposal;
    }

    private static void AddIfChanged(List<string> changed, string member, double before, double after)
    {
        if (before != after)
            changed.Add(member);
    }

    /// <summary>Whether the policy the application read from the file is the one that was written: the version and every threshold of the calibration (a policy holds lists, which a record compares by reference).</summary>
    private static bool SameThresholds(IdentityDecisionPolicy read, IdentityDecisionPolicy written)
    {
        return read.Version == written.Version
            && read.Provisional == written.Provisional
            && read.Match.Accept == written.Match.Accept
            && read.Match.ReviewFloor == written.Match.ReviewFloor
            && read.Match.MinEachFrame == written.Match.MinEachFrame
            && read.Presence.Liveness.MinScore == written.Presence.Liveness.MinScore;
    }

    private static bool SamePlace(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
