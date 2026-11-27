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

/// <summary>The verdict of one target of <c>qualification-targets.json</c> (or of one class or field of it): what is asked, what was measured, and whether it holds. A target that could not be measured fails.</summary>
public sealed record QualificationTargetVerdict(string Target, string Criterion, string Measured, bool Pass, string? Note = null);

/// <summary>The report of a candidate version as data (<c>candidate-version-report.json</c>): everything the markdown says, and its SHA-256.</summary>
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

/// <summary>
/// The report of a candidate version (see the comments at the top of the file). <see cref="Markdown"/> ends with its own SHA-256, <see cref="Sha256"/>: the hash of every line above that last
/// one, as UTF-8 with <c>\n</c> line endings, lower case hexadecimal; <see cref="VerifyHash"/> checks a text against it.
/// </summary>
public sealed class CandidateVersionReport
{
    /// <summary>The name of the human-readable report in the output folder.</summary>
    public const string MarkdownFileName = "candidate-version-report.md";

    /// <summary>The name of the report as data in the output folder.</summary>
    public const string JsonFileName = "candidate-version-report.json";

    /// <summary>What the last line of the report begins with: the SHA-256 of the lines above it follows.</summary>
    public const string HashLinePrefix = "report-sha256: ";

    private readonly CandidateVersionReportDocument _document;

    private CandidateVersionReport(string markdown, string sha256, IReadOnlyList<QualificationTargetVerdict> verdicts, CandidateVersionReportDocument document)
    {
        Markdown = markdown;
        Sha256 = sha256;
        Verdicts = verdicts;
        _document = document;
    }

    /// <summary>The report as text, ending with the line of its SHA-256.</summary>
    public string Markdown { get; }

    /// <summary>The SHA-256 of the report (the value the segment qualification takes), lower case hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>The verdict of every target and of every class and field they are measured on.</summary>
    public IReadOnlyList<QualificationTargetVerdict> Verdicts { get; }

    /// <summary>True when every target holds. A report with a target that fails is still a report: it is what the person who decides reads.</summary>
    public bool AllTargetsPassed => Verdicts.All(verdict => verdict.Pass);

    /// <summary>The attack classes the specification names (photos, screens, pre-recorded videos, synthetic input): a validation that does not cover one of them shows no verdict for it.</summary>
    public static IReadOnlyList<string> CoreAttackClasses => QualificationMetrics.CoreAttackClasses;

    /// <summary>The report of <paramref name="results"/> for the candidate version frozen in <paramref name="frozen"/>, judged by <paramref name="targets"/>. The intervals are those of the confidence of the targets.</summary>
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

    /// <summary>The verdict of every target of <paramref name="targets"/> on the metrics of <paramref name="results"/> (see <see cref="Build"/>).</summary>
    public static IReadOnlyList<QualificationTargetVerdict> Evaluate(QualificationResults results, QualificationTargets targets)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(targets);
        return Evaluate(results, QualificationMetrics.Rebound(results.Metrics, targets.Confidence), targets);
    }

    /// <summary>The report as data.</summary>
    public string ToJson() => JsonSerializer.Serialize(_document, QualificationResults.JsonOptions);

    /// <summary>
    /// Writes <see cref="MarkdownFileName"/> and <see cref="JsonFileName"/> to <paramref name="outDirectory"/>. The report of a final test is written once: a file that is there already is never
    /// replaced (<see cref="QualificationRunCodes.FinalAlreadyRun"/>).
    /// </summary>
    public void Write(string outDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        if (ExistsIn(outDirectory))
            throw new QualificationRunException(QualificationRunCodes.FinalAlreadyRun);

        Directory.CreateDirectory(outDirectory);
        WriteNew(Path.Combine(outDirectory, MarkdownFileName), Markdown);
        WriteNew(Path.Combine(outDirectory, JsonFileName), ToJson());
    }

    /// <summary>True when <paramref name="outDirectory"/> holds a report already (either file of it).</summary>
    public static bool ExistsIn(string outDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDirectory);
        return File.Exists(Path.Combine(outDirectory, MarkdownFileName)) || File.Exists(Path.Combine(outDirectory, JsonFileName));
    }

    /// <summary>The SHA-256 the last line of a report states; null when the text does not end with such a line.</summary>
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

    /// <summary>True when the last line of <paramref name="markdown"/> is the SHA-256 of the lines above it: the report was not changed after it was made.</summary>
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

    // ───── the verdicts ─────

    private static List<QualificationTargetVerdict> Evaluate(QualificationResults results, QualificationMetricsBlock metrics, QualificationTargets targets)
    {
        var verdicts = new List<QualificationTargetVerdict>();
        var confidence = targets.Confidence;
        var confidenceText = Percent(confidence);

        // The false match rate between different persons: the upper bound must be under the limit. With no error the bound is 1 - (1 - confidence)^(1 / n): a limit of 1 in 10,000 needs
        // about 30,000 independent comparisons, and a smaller set cannot show it whatever it finds.
        var fmr = metrics.Fmr;
        var trialsNeeded = Math.Ceiling(Math.Log(1.0 - confidence) / Math.Log(1.0 - targets.FmrMax));
        verdicts.Add(new QualificationTargetVerdict(
            "fmr",
            string.Create(CultureInfo.InvariantCulture, $"false match rate between different persons: upper bound at most {Number(targets.FmrMax)} at {confidenceText} confidence"),
            Bounded(fmr),
            fmr.Total > 0 && fmr.UpperBound <= targets.FmrMax,
            fmr.Total == 0
                ? "no impostor comparison was made"
                : fmr.UpperBound > targets.FmrMax && fmr.Count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"with no false match about {trialsNeeded:0} independent comparisons are needed to show it")
                    : null));

        // The attacks that are accepted as a person present, per class: every class that was tried, and the classes the specification names even when none was.
        foreach (var attackClass in QualificationAttackClasses.All.Where(candidate => CoreAttackClasses.Contains(candidate) || metrics.Apcer.Any(entry => entry.Class == candidate && entry.Rate.Total > 0)))
        {
            var rate = metrics.Apcer.FirstOrDefault(entry => entry.Class == attackClass)?.Rate ?? QualificationBoundedRate.Of(0, 0, confidence);
            var enough = rate.Total >= targets.MinAttacksPerClass;
            verdicts.Add(new QualificationTargetVerdict(
                "apcer." + attackClass,
                string.Create(CultureInfo.InvariantCulture, $"attacks of the class accepted: upper bound at most {Number(targets.ApcerUpperMax)} at {confidenceText} confidence, from at least {targets.MinAttacksPerClass} attacks"),
                Bounded(rate),
                enough && rate.UpperBound <= targets.ApcerUpperMax,
                rate.Total == 0 ? "no attack of this class was tried" : enough ? null : "too few attacks of this class to show the limit"));
        }

        // The shadow automation: the policy would have closed at least the target of the check-ins, and the lower Wilson bound of the rate is high enough, on at least that many check-ins.
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

        // The exactness of the fields that identify the holder, by source: the visual zone (the name of the field) and the MRZ (the name with .mrz).
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

        // A false approval of the policy on a case that must never be approved suspends the combination.
        var critical = metrics.CriticalFalseApprovals;
        verdicts.Add(new QualificationTargetVerdict(
            "critical_false_approvals",
            string.Create(CultureInfo.InvariantCulture, $"approvals of an attack, of a person with the document of another, of an altered or a foreign document: at most {targets.CriticalFalseApprovalsMax}"),
            critical.Total.ToString(CultureInfo.InvariantCulture),
            critical.Total <= targets.CriticalFalseApprovalsMax));
        return verdicts;
    }

    private static string OcrCriterion(QualificationTargets targets)
    {
        return string.Create(CultureInfo.InvariantCulture, $"exact readings of an identifying field: lower Wilson bound at least {Number(targets.IdentifyingFieldExactnessMin)}");
    }

    // ───── the text ─────

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
        Line("- False match rate (FMR) at the policy: " + Bounded(metrics.Fmr) + ".");
        Line("- False non-match rate (FNMR) at the policy: " + Bounded(metrics.Fnmr) + ".");
        Line("- Attacks accepted as a person present (APCER), per class:");
        foreach (var entry in metrics.Apcer)
            Line("  - " + entry.Class + ": " + Bounded(entry.Rate) + ".");

        Line("  - maximum: " + (metrics.ApcerMax is { } max ? Number(max) : "n/a") + ".");
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

        Line(string.Create(invariant, $"- Critical false approvals: {metrics.CriticalFalseApprovals.Total} ({Counts(metrics.CriticalFalseApprovals.ByKind)})."));
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
