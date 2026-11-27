using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Identity.Statistics;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T02 (NF-198, NF-199, D12): the report of a candidate version, the paper a qualification ends with: which weights (their SHA-256, licence and provenance), which policy (its
// version and canonical hash), which thresholds and aggregation, on which dataset (counts, never persons), what was measured with its intervals, how each target of the release criteria
// came out, and how a candidate model compares with the reference. It ends with its own SHA-256, the value a platform administrator gives to the qualification endpoint of the
// segments (E70/F02/US01/T02) to say that a segment was qualified on THIS report. It is a function of what it is given (the results, the lock, the targets): the same input is the same text,
// to the byte, and the same hash.
// PRIVACY: the report is an aggregate. It names no capture, no person, no path, no document value, no picture: only versions, hashes, counts, rates, scores of the sets and times.

public sealed record QualificationTargetVerdict(string Target, string Criterion, string Measured, bool Pass, string? Note = null);

public sealed record CandidateVersionReportDocument(
    string DatasetId,
    string DatasetVersion,
    string DatasetManifestHash,
    string Split,
    DateTimeOffset RunAt,
    DateOnly ReferenceDay,
    double Confidence,
    QualificationLock Lock,
    IReadOnlyList<string> RunDiffersFromLock,
    QualificationPolicyInfo Policy,
    QualificationThresholds Thresholds,
    IReadOnlyList<QualificationModelInfo> Models,
    QualificationDatasetSummary Dataset,
    QualificationImpostorSampling ImpostorSampling,
    QualificationMetricsBlock Metrics,
    IReadOnlyList<QualificationTargetVerdict> Targets,
    bool AllTargetsPassed,
    string ReportSha256);

public sealed class CandidateVersionReport
{
    public const string MarkdownFileName = "candidate-version-report.md";

    public const string JsonFileName = "candidate-version-report.json";

    public const string HashLinePrefix = "report-sha256: ";

    private readonly CandidateVersionReportDocument _document;

    private CandidateVersionReport(string markdown, string sha256, IReadOnlyList<QualificationTargetVerdict> verdicts, CandidateVersionReportDocument document)
    {
        Markdown = markdown;
        Sha256 = sha256;
        Verdicts = verdicts;
        _document = document;
    }

    public string Markdown { get; }

    public string Sha256 { get; }

    public IReadOnlyList<QualificationTargetVerdict> Verdicts { get; }

    public bool AllTargetsPassed => Verdicts.All(verdict => verdict.Pass);

    public static IReadOnlyList<string> CoreAttackClasses => QualificationMetrics.CoreAttackClasses;

    public static CandidateVersionReport Build(QualificationResults results, QualificationLock frozen, QualificationTargets targets)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(targets);

        var metrics = QualificationMetrics.Rebound(results.Metrics, targets.Confidence);
        var verdicts = Evaluate(results, metrics, targets);
        var differs = frozen.Verify(QualificationLock.FromResults(results)).DifferingMembers;
        var body = Render(results, metrics, frozen, differs, verdicts, targets);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(body)));
        var document = new CandidateVersionReportDocument(
            results.DatasetId,
            results.DatasetVersion,
            results.DatasetManifestHash,
            results.Split,
            results.RunAt,
            results.ReferenceDay,
            targets.Confidence,
            frozen,
            differs,
            results.Policy,
            results.Thresholds,
            results.Models,
            results.Dataset,
            results.ImpostorSampling,
            metrics,
            verdicts,
            verdicts.All(verdict => verdict.Pass),
            sha256);
        return new CandidateVersionReport(body + HashLinePrefix + sha256 + "\n", sha256, verdicts, document);
    }

    public static IReadOnlyList<QualificationTargetVerdict> Evaluate(QualificationResults results, QualificationTargets targets)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(targets);
        return Evaluate(results, QualificationMetrics.Rebound(results.Metrics, targets.Confidence), targets);
    }

    public string ToJson() => JsonSerializer.Serialize(_document, QualificationResults.JsonOptions);

    public void Write(string outDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        if (ExistsIn(outDirectory))
            throw new QualificationRunException(QualificationRunCodes.FinalAlreadyRun);

        Directory.CreateDirectory(outDirectory);
        WriteNew(Path.Combine(outDirectory, MarkdownFileName), Markdown);
        WriteNew(Path.Combine(outDirectory, JsonFileName), ToJson());
    }

    public static bool ExistsIn(string outDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        return File.Exists(Path.Combine(outDirectory, MarkdownFileName)) || File.Exists(Path.Combine(outDirectory, JsonFileName));
    }

    public static string? ReadHash(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var start = LastLineStart(markdown);
        if (start < 0)
            return null;

        var line = markdown[start..].TrimEnd('\n', '\r');
        if (!line.StartsWith(HashLinePrefix, StringComparison.Ordinal))
            return null;

        var hash = line[HashLinePrefix.Length..];
        return hash.Length == 64 && hash.All(char.IsAsciiHexDigitLower) ? hash : null;
    }

    public static bool VerifyHash(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (ReadHash(markdown) is not { } stated)
            return false;

        var body = markdown[..LastLineStart(markdown)];
        return string.Equals(stated, Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(body))), StringComparison.Ordinal);
    }

    private static int LastLineStart(string markdown)
    {
        var trimmed = markdown.TrimEnd('\n', '\r');
        return trimmed.Length == 0 ? -1 : trimmed.LastIndexOf('\n') + 1;
    }

    private static void WriteNew(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static List<QualificationTargetVerdict> Evaluate(QualificationResults results, QualificationMetricsBlock metrics, QualificationTargets targets)
    {
        var verdicts = new List<QualificationTargetVerdict>();
        var confidence = targets.Confidence;
        var confidenceText = Percent(confidence);

        // The false match rate between different persons: the upper bound must be under the limit, over the DISTINCT couples of persons (the captures of two persons compared again and again are
        // one piece of evidence, not as many, and a couple is a false match when any comparison of it is). With no error the bound is 1 - (1 - confidence)^(1 / n): a limit of 1 in 10,000 needs
        // about 30,000 independent couples of persons, and a smaller set cannot show it whatever it finds.
        var fmr = metrics.FmrCouples;
        var couplesNeeded = Math.Ceiling(Math.Log(1.0 - confidence) / Math.Log(1.0 - targets.FmrMax));
        verdicts.Add(new QualificationTargetVerdict(
            "fmr",
            string.Create(CultureInfo.InvariantCulture, $"false match rate between different persons, over the distinct couples of persons: upper bound at most {Number(targets.FmrMax)} at {confidenceText} confidence"),
            Bounded(fmr),
            fmr.Total > 0 && fmr.UpperBound <= targets.FmrMax,
            fmr.Total == 0
                ? "no impostor comparison was made"
                : fmr.UpperBound > targets.FmrMax && fmr.Count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"with no false match about {couplesNeeded:0} independent couples of persons are needed to show it")
                    : null));

        bool Reported(string candidate) =>
            CoreAttackClasses.Contains(candidate)
            || metrics.Apcer.Any(entry => entry.Class == candidate && entry.Rate.Total > 0)
            || metrics.AttacksNotJudged.Any(entry => entry.Class == candidate && entry.NotJudged > 0);

        foreach (var attackClass in QualificationAttackClasses.All.Where(Reported))
        {
            var rate = metrics.Apcer.FirstOrDefault(entry => entry.Class == attackClass)?.Rate ?? QualificationBoundedRate.Of(0, 0, confidence);
            var notJudged = metrics.AttacksNotJudged.FirstOrDefault(entry => entry.Class == attackClass) is { } faults ? faults.NotJudged : 0;
            var enough = rate.Total >= targets.MinAttacksPerClass;
            var note = rate.Total == 0 && notJudged == 0 ? "no attack of this class was tried" : rate.Total > 0 && !enough ? "too few attacks of this class to show the limit" : null;
            if (notJudged > 0)
            {
                note = (note is null ? string.Empty : note + "; ")
                    + string.Create(CultureInfo.InvariantCulture, $"{notJudged} attacks of this class could not be judged (a fault, a sequence that could not be read, a validation or a liveness that was not available) and are not counted as rejected");
            }

            verdicts.Add(new QualificationTargetVerdict(
                "apcer." + attackClass,
                string.Create(CultureInfo.InvariantCulture, $"attacks of the class accepted: upper bound at most {Number(targets.ApcerUpperMax)} at {confidenceText} confidence, from at least {targets.MinAttacksPerClass} attacks judged, none left without a judgement"),
                Bounded(rate) + (notJudged > 0 ? string.Create(CultureInfo.InvariantCulture, $", not judged {notJudged}") : string.Empty),
                enough && rate.UpperBound <= targets.ApcerUpperMax && notJudged == 0,
                note));
        }

        var automation = metrics.ShadowAutomation;
        var observedAtLeast = (double)targets.AutomationTarget / targets.AutomationOf;
        verdicts.Add(new QualificationTargetVerdict(
            "automation",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{targets.AutomationTarget} of {targets.AutomationOf} check-ins closed automatically (rate at least {Number(observedAtLeast)}), lower Wilson bound at least {Number(targets.AutomationWilsonLowMin)}, on at least {targets.AutomationOf} check-ins"),
            Wilson(automation),
            automation.Total >= targets.AutomationOf && (automation.Rate ?? 0) >= observedAtLeast && automation.Low >= targets.AutomationWilsonLowMin,
            automation.Total < targets.AutomationOf ? "fewer check-ins than the target asks for" : null));

        foreach (var field in results.Policy.IdentifyingFields)
        {
            var measured = new[] { field, field + QualificationMetrics.MrzFieldSuffix }.Select(key => metrics.OcrExactness.FirstOrDefault(entry => entry.Field == key)).OfType<QualificationFieldRate>().ToList();
            if (measured.Count == 0)
            {
                verdicts.Add(new QualificationTargetVerdict("ocr." + field, OcrCriterion(targets), "not measured", false, "no value of this field was read on a document of the ground truth"));
                continue;
            }

            foreach (var entry in measured)
            {
                verdicts.Add(new QualificationTargetVerdict(
                    "ocr." + entry.Field,
                    OcrCriterion(targets),
                    Wilson(entry.Rate),
                    entry.Rate.Total > 0 && entry.Rate.Low >= targets.IdentifyingFieldExactnessMin));
            }
        }

        // A false approval of the policy on a case that must never be approved suspends the combination. The criterion is measured on the cases the policy took a decision on, of EACH kind
        // (an attack, a person with the document of another, an altered document, a foreign image): a kind with none of them is not measured, and a target that is not measured fails (that
        // nothing was approved of a kind nobody decided on proves nothing).
        var critical = metrics.CriticalFalseApprovals;
        var notMeasured = critical.NotMeasured();
        verdicts.Add(new QualificationTargetVerdict(
            "critical_false_approvals",
            string.Create(
                CultureInfo.InvariantCulture,
                $"approvals of an attack, of a person with the document of another, of an altered or a foreign document: at most {targets.CriticalFalseApprovalsMax}, on at least one decided case of each kind"),
            CriticalMeasured(critical),
            notMeasured.Count == 0 && critical.Total <= targets.CriticalFalseApprovalsMax,
            notMeasured.Count == 0 ? null : "not measured: no case was decided for " + string.Join(", ", notMeasured)));
        return verdicts;
    }

    private static string CriticalMeasured(QualificationCriticalApprovals critical)
    {
        var kinds = string.Join(
            ", ",
            QualificationCriticalKinds.All.Select(kind => string.Create(CultureInfo.InvariantCulture, $"{kind} {critical.ByKind.GetValueOrDefault(kind)}/{critical.Decided.GetValueOrDefault(kind)}")));
        return string.Create(CultureInfo.InvariantCulture, $"{critical.Total} approvals; {kinds}");
    }

    private static string OcrCriterion(QualificationTargets targets)
    {
        return string.Create(CultureInfo.InvariantCulture, $"exact readings of an identifying field: lower Wilson bound at least {Number(targets.IdentifyingFieldExactnessMin)}");
    }

    private static string Render(
        QualificationResults results,
        QualificationMetricsBlock metrics,
        QualificationLock frozen,
        IReadOnlyList<string> differs,
        IReadOnlyList<QualificationTargetVerdict> verdicts,
        QualificationTargets targets)
    {
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');
        var invariant = CultureInfo.InvariantCulture;

        Line("# Candidate version report");
        Line();
        Line(string.Create(invariant, $"Dataset `{results.DatasetId}` version `{results.DatasetVersion}`, split `{results.Split}`, manifest SHA-256 `{results.DatasetManifestHash}`."));
        Line(string.Create(invariant, $"Run at {results.RunAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}, documents judged on {results.ReferenceDay:yyyy-MM-dd}. Lock created at {frozen.CreatedAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}."));
        Line(differs.Count == 0
            ? "Frozen configuration: the run used exactly the configuration of the lock."
            : "Frozen configuration: THE RUN DOES NOT USE THE CONFIGURATION OF THE LOCK, it differs in: " + string.Join(", ", differs) + ".");
        Line();

        Line("## Candidate version");
        Line();
        Line(string.Create(invariant, $"- Policy `{results.Policy.Version}`, canonical SHA-256 `{results.Policy.CanonicalHash}`, provisional: {YesNo(results.Policy.Provisional)}."));
        Line(string.Create(invariant, $"- Weights hash of the three models: `{frozen.WeightsHash}`."));
        Line(string.Create(invariant, $"- Aggregation: similarity of the chosen frames by `{results.Thresholds.MatchAggregation}` over {results.Thresholds.MatchFrames} frames; liveness by `{results.Thresholds.LivenessRule}` over {results.Thresholds.LivenessFrames} frames."));
        Line(string.Create(
            invariant,
            $"- Thresholds: accept {Number(results.Thresholds.Accept)}, review floor {Number(results.Thresholds.ReviewFloor)}, least similarity of each frame {Number(results.Thresholds.MinEachFrame)}, least liveness {Number(results.Thresholds.LivenessMinScore)}."));
        Line();
        Line("| Role | Model | Version | Licence | Provenance | Loaded | Weights SHA-256 |");
        Line("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var model in results.Models)
        {
            Line(string.Create(
                invariant,
                $"| {Cell(model.Role)} | {Cell(model.Name)} | {Cell(model.Version)} | {Cell(model.License)} | {Cell(model.Provenance)} | {(model.Loaded ? "yes" : "no" + (model.FailureCode is null ? string.Empty : " (" + model.FailureCode + ")"))} | {(model.WeightSha256 is { } sha ? "`" + sha + "`" : "-")} |"));
        }

        Line();

        Line("## Dataset");
        Line();
        foreach (var split in results.Dataset.Splits)
            Line("- " + split.Describe());

        Line();
        var evaluated = results.Dataset.Evaluated;
        Line(string.Create(invariant, $"Split `{results.Split}`: {evaluated.Captures} captures."));
        Line("- by kind: " + Counts(evaluated.ByKind));
        Line("- by attack class: " + Counts(evaluated.ByAttackClass));
        Line("- by document format: " + Counts(evaluated.ByFormat));
        Line("- by channel: " + Counts(evaluated.ByChannel));
        var sampling = results.ImpostorSampling;
        Line(string.Create(
            invariant,
            $"- impostor trials derived from the sequences: {sampling.PairsRun} run of {sampling.PairsAvailable} possible pairs (sample: {YesNo(sampling.Sampled)}, limit {sampling.Cap}); {sampling.SequencesWithoutFrames} sequences gave no frames to compare."));
        Line();

        Line(string.Create(invariant, $"## Metrics ({Percent(targets.Confidence)} confidence)"));
        Line();
        Line("- False match rate (FMR) at the policy: " + Bounded(metrics.Fmr) + " (every comparison: the comparisons of one couple of persons are not independent).");
        Line("- False match rate over the distinct couples of persons (the verdict; a couple is a false match when any comparison of it is): " + Bounded(metrics.FmrCouples) + ".");
        Line("- False non-match rate (FNMR) at the policy: " + Bounded(metrics.Fnmr) + ".");
        Line("- Attacks accepted as a person present (APCER), per class, over the attacks that were judged:");
        foreach (var entry in metrics.Apcer)
            Line("  - " + entry.Class + ": " + Bounded(entry.Rate) + ".");

        Line("  - maximum: " + (metrics.ApcerMax is { } max ? Number(max) : "n/a") + ".");
        var notJudged = metrics.AttacksNotJudged.Where(entry => entry.NotJudged > 0).ToList();
        Line(
            "- Attacks that could not be judged (a fault, a sequence that could not be read, a validation or a liveness that was not available; not counted as rejected above): "
            + (notJudged.Count == 0 ? "none" : string.Join(", ", notJudged.Select(entry => string.Create(invariant, $"{entry.Class} {entry.NotJudged} of {entry.Total}"))))
            + ".");
        Line("- Persons present not accepted (BPCER): " + Bounded(metrics.Bpcer) + ".");
        Line("- Exactness of the fields read on documents:");
        foreach (var entry in metrics.OcrExactness)
            Line("  - " + entry.Field + ": " + Wilson(entry.Rate) + ".");

        Line("- Shadow automation (check-ins the policy would close): " + Wilson(metrics.ShadowAutomation) + ".");
        Line("- Document stage: genuine documents refused " + Bounded(metrics.Document.FalseRejects) + "; altered or foreign documents let through " + Bounded(metrics.Document.HostileLetThrough) + ".");
        foreach (var format in metrics.Document.ByFormat)
        {
            Line(string.Create(
                invariant,
                $"  - format {format.Format}: {format.GenuineRefused} of {format.Genuine} genuine refused, {format.HostileLetThrough} of {format.Hostile} altered or foreign let through."));
        }

        Line(
            string.Create(
                invariant,
                $"- Critical false approvals: {metrics.CriticalFalseApprovals.Total} ({Counts(metrics.CriticalFalseApprovals.ByKind)}), out of the cases decided ({Counts(metrics.CriticalFalseApprovals.Decided)})."));
        Line("- Time of each stage (ms), median, 95th percentile (nearest rank), longest:");
        foreach (var stage in metrics.Latency)
            Line(string.Create(invariant, $"  - {stage.Stage}: {stage.Count} runs, {stage.P50Ms:0.0}, {stage.P95Ms:0.0}, {stage.MaxMs:0.0}."));

        Line();

        Line("## Targets");
        Line();
        Line("| Target | Criterion | Measured | Verdict |");
        Line("| --- | --- | --- | --- |");
        foreach (var verdict in verdicts)
            Line(string.Create(invariant, $"| {verdict.Target} | {verdict.Criterion} | {verdict.Measured} | {(verdict.Pass ? "pass" : "FAIL")}{(verdict.Note is null ? string.Empty : " (" + verdict.Note + ")")} |"));

        Line();
        Line(verdicts.All(verdict => verdict.Pass) ? "Every target holds." : string.Create(invariant, $"{verdicts.Count(verdict => !verdict.Pass)} of {verdicts.Count} verdicts do not hold."));
        Line();

        if (metrics.CandidateComparisons.Count > 0)
        {
            Line("## Candidate models");
            Line();
            Line("A candidate is compared with the reference at the same limit of false acceptances: the reference at the threshold of the policy, the candidate at the lowest threshold that accepts no more of the negatives.");
            Line();
            foreach (var comparison in metrics.CandidateComparisons)
            {
                var negative = comparison.Measure == "liveness" ? "attacks" : "impostor comparisons";
                var positive = comparison.Measure == "liveness" ? "persons present" : "genuine comparisons";
                Line(string.Create(invariant, $"- {comparison.Measure}: reference {comparison.Reference}, candidate {comparison.Candidate}."));
                Line(string.Create(
                    invariant,
                    $"  - reference at {Number(comparison.ReferencePoint.Threshold)}: {comparison.ReferencePoint.FalseAccepts} of {comparison.ReferencePoint.Negatives} {negative} accepted, {comparison.ReferencePoint.FalseRejects} of {comparison.ReferencePoint.Positives} {positive} rejected."));
                Line(string.Create(
                    invariant,
                    $"  - candidate at {Number(comparison.CandidatePoint.Threshold)}: {comparison.CandidatePoint.FalseAccepts} of {comparison.CandidatePoint.Negatives} {negative} accepted, {comparison.CandidatePoint.FalseRejects} of {comparison.CandidatePoint.Positives} {positive} rejected."));
                Line("  - the candidate rejects " + (comparison.CandidateRejectsFewer ? "fewer" : "no fewer") + " " + positive + " at the same limit.");
            }

            Line();
        }

        return text.ToString();
    }

    private static string Bounded(QualificationBoundedRate rate)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{rate.Count}/{rate.Total} = {(rate.Rate is { } value ? Number(value) : "n/a")}, upper bound {Number(rate.UpperBound)}");
    }

    private static string Wilson(QualificationWilsonRate rate)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{rate.Count}/{rate.Total} = {(rate.Rate is { } value ? Number(value) : "n/a")}, Wilson interval {Number(rate.Low)} to {Number(rate.High)}");
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts)
    {
        return counts.Count == 0 ? "none" : string.Join(", ", counts.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key}={entry.Value}")));
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Percent(double value) => (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>A text for a cell of a table: no pipe and no line break, so that a provenance note can never break the table.</summary>
    private static string Cell(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
}
