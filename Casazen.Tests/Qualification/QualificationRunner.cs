using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Identity.Statistics;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12, ADR-013-R17, R21): the offline evaluation runner on the REAL in-process pipeline. For every capture of a split it runs the document part (the geometry
// and the side each picture shows, the fields read by zone and compared with the ground truth through IdentityStatistics.FieldExactness, their coherence) and the presence part (the
// sequence read with the limits of the application, validated, its frames chosen by the server, their liveness, their comparison with the portrait of the document), takes the decision
// of the engine under the active policy with a segment qualified for the test, and measures the time of each stage. Impostors are made two ways: the captures of a person with the
// document of ANOTHER one, and the sequence of every person against the documents of the others of the same split (all the pairs, or a deterministic sample when they are too many).
//
// PRIVACY (ADR-013-R2, R17): the toolkit never commits, prints or logs an image, a frame, a ground-truth value, a name, a number of a document or an embedding. A picture is read
// into memory and zeroed when it has been used; a value read on a document and its true value meet in IdentityStatistics.FieldExactness and only the names of the fields and the counts
// come out; the only exception the runner makes is the TYPE of an exception, never its message; the output holds the ids of the manifest (anonymous), codes, counts, scores and times.

/// <summary>How a run goes: how many impostor trials derived from the sequences it makes at most, and where it says how far it is (ids and counts only).</summary>
public sealed record QualificationRunOptions
{
    /// <summary>
    /// The most impostor trials a run derives from the sequences and the documents of the other persons of the split. With more pairs than this a deterministic sample is taken (the
    /// output says how many there were and how many were run). A limit of the cost of a run, not a decision: about 30,000 independent trials are needed to show an upper bound of 0.01%.
    /// </summary>
    public int MaxImpostorTrials { get; init; } = 50_000;

    /// <summary>Told where the run is (<c>case 3/120: capture-id</c>): the ids of the manifest and counts, never a value.</summary>
    public Action<string>? Progress { get; init; }
}

/// <summary>The codes the document part of a run adds to the guidance codes of the application (<see cref="IdentityGuidanceCodes"/>): what a dataset can lack or break.</summary>
public static class QualificationDocumentCodes
{
    /// <summary>The format has a side the capture has no picture for.</summary>
    public const string SideMissing = "side_missing";

    /// <summary>A model or a reader failed on the picture of the side: the document is not complete.</summary>
    public const string ProcessingError = "processing_error";
}

/// <summary>
/// The offline evaluation runner of the qualification (E74): runs the REAL pipeline of the self-hosted identity verification, in this process, on a split of a dataset described by a
/// manifest, and writes <c>results.json</c> to the output folder. See the comments at the top of the file for what it does and what it never writes.
/// </summary>
public sealed class QualificationRunner(QualificationPipeline pipeline, QualificationTargets targets, QualificationRunOptions? options = null)
{
    // The segment the decision of a run is taken with: qualified for the policy and the weights in force, the automatic approval allowed, so that «the policy would approve» is the
    // outcome and a qualification can count the approvals it gives (a false one on an attack is a critical case). It exists in no database: it is the test segment of the run.
    private static readonly SegmentResolution QualifiedSegment = new(null, IdentitySegmentMode.Automatic, true, true, IdentityAssuranceLevels.Consistency, null);

    // The fields the MRZ gives (the ones of the coherence checker that have a value in it), and so the ones whose value read in the MRZ is measured on its own.
    private static readonly string[] MrzFields =
    [
        ZonedFieldNames.Surname, ZonedFieldNames.GivenNames, ZonedFieldNames.BirthDate, ZonedFieldNames.Sex, ZonedFieldNames.Nationality, ZonedFieldNames.DocumentNumber,
        ZonedFieldNames.ExpiryDate,
    ];

    private readonly QualificationRunOptions _options = options ?? new QualificationRunOptions();

    /// <summary>
    /// Runs the real pipeline on the captures of <paramref name="split"/> of the dataset of <paramref name="manifestPath"/> and writes <c>results.json</c> to
    /// <paramref name="outDir"/>. A manifest that does not pass its checks, a split with no capture, a ground-truth file that is not one, models or a policy that cannot be used stop
    /// the run with a <see cref="QualificationRunException"/> and its code before the first capture; a capture that breaks is a case with its error, and the run goes on.
    /// </summary>
    public async Task<QualificationResults> RunAsync(string manifestPath, string split, string outDir, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        if (!QualificationSplits.All.Contains(split, StringComparer.Ordinal))
            throw new QualificationRunException(QualificationRunCodes.SplitUnknown);

        // Where the results go is checked before anything of the dataset is read: not the folder of the committed policies, not a folder of the checkout (a run takes hours).
        pipeline.EnsureOutputAllowed(outDir);

        var preflight = Preflight(manifestPath, split);
        var (manifest, manifestHash, folder, validation) = (preflight.Manifest, preflight.ManifestHash, preflight.Folder, preflight.Validation);
        foreach (var warning in validation.Warnings.Where(candidate => string.Equals(candidate.Split, split, StringComparison.Ordinal)))
            _options.Progress?.Invoke("warning: " + warning);

        var policy = pipeline.ActivePolicy;
        var plans = Plan(manifest, split, folder);
        var day = pipeline.Clock.TodayInRomeAsDateOnly();

        // The documents of the split an impostor can present, and the sequences that are tried with them.
        var pool = plans
            .Where(plan => plan.Capture.Document is { Altered: false, Foreign: false, OwnerPerson: not null, Front: not null })
            .Select(plan => new PoolDocument(plan.Id, plan.Capture.Document!.OwnerPerson!, Resolve(folder, plan.Capture.Document.Front!)))
            .DistinctBy(document => (document.Owner, document.FrontPath))
            .ToList();
        var sources = plans.Where(plan => plan.Kind == QualificationCaseKinds.Genuine && plan.Capture.Sequence is not null).ToList();
        var documentsOf = pool.GroupBy(document => document.Owner, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var available = sources.Sum(source => (long)(pool.Count - documentsOf.GetValueOrDefault(source.Person)));
        var maximum = Math.Max(1, _options.MaxImpostorTrials);
        var sampled = available > maximum;
        var share = sampled ? (double)maximum / available : 1.0;

        var cases = new List<QualificationCaseResult>(plans.Count);
        var trials = new List<QualificationImpostorTrial>();
        var withoutFrames = 0;
        for (var index = 0; index < plans.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var plan = plans[index];
            _options.Progress?.Invoke(string.Create(CultureInfo.InvariantCulture, $"case {index + 1}/{plans.Count}: {plan.Id}"));
            using var run = await RunCaptureAsync(plan, folder, day, policy, ct);
            cases.Add(run.Result);
            if (plan.Kind != QualificationCaseKinds.Genuine || plan.Capture.Sequence is null)
                continue;

            if (run.Retained is not { } retained)
            {
                withoutFrames++;
                continue;
            }

            foreach (var document in pool)
            {
                if (string.Equals(document.Owner, plan.Person, StringComparison.Ordinal) || !IsSampled(plan.Id, document.CaptureId, share))
                    continue;

                ct.ThrowIfCancellationRequested();
                if (await RunImpostorTrialAsync(plan.Id, plan.Person, document, retained, policy, ct) is { } trial)
                {
                    trials.Add(trial);
                    if (trials.Count % 500 == 0)
                        _options.Progress?.Invoke(string.Create(CultureInfo.InvariantCulture, $"impostor trials: {trials.Count}"));
                }
            }
        }

        var models = pipeline.ModelInfos();
        var thresholds = QualificationThresholds.From(policy);
        var results = new QualificationResults(
            QualificationResults.CurrentSchemaVersion,
            manifest.DatasetId!,
            manifest.Version!,
            manifestHash,
            split,
            pipeline.Clock.GetUtcNow(),
            day,
            targets.Confidence,
            new QualificationPolicyInfo(policy.Version, pipeline.PolicySnapshot.Hash!, policy.Provisional, policy.Document.IdentifyingFields),
            thresholds,
            models,
            new QualificationDatasetSummary(validation.Splits, Summarize(plans)),
            new QualificationImpostorSampling(available, trials.Count, sampled, maximum, withoutFrames),
            cases,
            trials,
            QualificationMetrics.Compute(cases, trials, thresholds, targets.Confidence, models));

        Directory.CreateDirectory(outDir);
        results.Save(Path.Combine(outDir, QualificationResults.FileName));
        return results;
    }

    /// <summary>
    /// Whether the pair of the sequence of one capture and the document of another is one of the sample: every pair when there are few enough (<paramref name="share"/> is 1), else the
    /// pairs whose SHA-256 (of the two ids) falls in the first <paramref name="share"/> of the range, so the same dataset gives the same sample whatever the order and the machine.
    /// </summary>
    public static bool IsSampled(string sequenceCaseId, string documentCaseId, double share)
    {
        ArgumentNullException.ThrowIfNull(sequenceCaseId);
        ArgumentNullException.ThrowIfNull(documentCaseId);
        if (share >= 1.0)
            return true;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sequenceCaseId + "|" + documentCaseId));
        return BinaryPrimitives.ReadUInt64BigEndian(hash) / 18446744073709551616.0 < share;
    }

    // ───── the plan: the manifest, the captures of the split and their ground truth ─────

    private sealed record CapturePlan(QualificationCapture Capture, string Id, string Person, string Kind, IReadOnlyDictionary<string, string> Truth);

    private sealed record PoolDocument(string CaptureId, string Owner, string FrontPath);

    /// <summary>
    /// What a run of one split checks before it reads a picture: the manifest (read, with the canonical SHA-256 of its file), the folder its paths start from, its validation (it passed) and the
    /// counts of the split (it has persons and captures). Nothing of the captures is opened.
    /// </summary>
    public sealed record QualificationPreflight(QualificationDatasetManifest Manifest, string ManifestHash, string Folder, QualificationManifestReport Validation, QualificationSplitCounts Counts);

    /// <summary>
    /// Reads the manifest and checks that a run of <paramref name="split"/> can start: the split is one of the three, the manifest passes its checks and the split has persons and captures.
    /// Nothing of the captures is opened, so the final test can ask before it writes its marker: a manifest that is wrong must not use up the one run. Warnings (a kind of critical case the
    /// split has no whole check-in of) are in <see cref="QualificationPreflight.Validation"/>, not errors.
    /// </summary>
    /// <exception cref="QualificationRunException"><see cref="QualificationRunCodes.SplitUnknown"/>, <see cref="QualificationRunCodes.ManifestUnreadable"/>, <see cref="QualificationRunCodes.ManifestInvalid"/> or <see cref="QualificationRunCodes.SplitEmpty"/>.</exception>
    public static QualificationPreflight Preflight(string manifestPath, string split)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);
        if (!QualificationSplits.All.Contains(split, StringComparer.Ordinal))
            throw new QualificationRunException(QualificationRunCodes.SplitUnknown);

        var fullManifest = Path.GetFullPath(manifestPath);
        var folder = Path.GetDirectoryName(fullManifest)!;
        var (manifest, manifestHash) = ReadManifest(fullManifest);
        var validation = QualificationManifestValidator.Validate(manifest, folder);
        if (!validation.IsValid)
            throw new QualificationRunException(QualificationRunCodes.ManifestInvalid, validation.ToString());

        var counts = validation.Splits.First(candidate => string.Equals(candidate.Split, split, StringComparison.Ordinal));
        if (counts.Persons == 0 || counts.Captures == 0)
            throw new QualificationRunException(QualificationRunCodes.SplitEmpty, split);

        return new QualificationPreflight(manifest, manifestHash, folder, validation, counts);
    }

    /// <summary>The manifest of a dataset and the canonical SHA-256 of its file (the same for every line ending and indentation); <see cref="QualificationRunCodes.ManifestUnreadable"/> when it cannot be read.</summary>
    internal static (QualificationDatasetManifest Manifest, string Hash) ReadManifest(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new QualificationRunException(QualificationRunCodes.ManifestUnreadable);
        }

        QualificationDatasetManifest manifest;
        try
        {
            manifest = QualificationDatasetManifest.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (QualificationManifestFormatException ex)
        {
            // The message names the place of the failure by the members the manifest knows, never a word the author typed.
            throw new QualificationRunException(QualificationRunCodes.ManifestUnreadable, ex.Message);
        }

        var hash = IdentityPolicyProvider.CanonicalSha256(bytes) ?? throw new QualificationRunException(QualificationRunCodes.ManifestUnreadable);
        return (manifest, hash);
    }

    private static List<CapturePlan> Plan(QualificationDatasetManifest manifest, string split, string folder)
    {
        var persons = manifest.Persons!.Where(person => string.Equals(person.Split, split, StringComparison.Ordinal)).Select(person => person.Id!).ToHashSet(StringComparer.Ordinal);
        var plans = new List<CapturePlan>();
        foreach (var capture in manifest.Captures!.Where(capture => persons.Contains(capture.Person!)))
        {
            var kind = capture.Kind == QualificationCaptureKinds.Attack
                ? QualificationCaseKinds.Attack
                : capture.Document is { OwnerPerson: { } owner } && !string.Equals(owner, capture.Person, StringComparison.Ordinal)
                    ? QualificationCaseKinds.ImpostorPair
                    : QualificationCaseKinds.Genuine;

            // The ground truth is read before the first capture is run: a file that is not one stops the run at once and not after hours.
            var truth = capture.Document is { } document
                ? ReadGroundTruth(Resolve(folder, document.GroundTruthFile!), capture.Id!)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            plans.Add(new CapturePlan(capture, capture.Id!, capture.Person!, kind, truth));
        }

        if (plans.Count == 0)
            throw new QualificationRunException(QualificationRunCodes.SplitEmpty, split);

        return plans;
    }

    /// <summary>
    /// The true values of the fields of a document: a JSON object whose members are fields of <see cref="ZonedFieldNames"/> (dates as <c>yyyy-MM-dd</c>) with text values. The values are
    /// held in memory for the run and are never printed; a file that is anything else is a <see cref="QualificationRunCodes.GroundTruthInvalid"/> that names the capture, not the file or its content.
    /// </summary>
    private static Dictionary<string, string> ReadGroundTruth(string path, string captureId)
    {
        var truth = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new QualificationRunException(QualificationRunCodes.GroundTruthInvalid, captureId);

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (!ZonedFieldNames.All.Contains(member.Name) || member.Value.ValueKind != JsonValueKind.String || member.Value.GetString() is not { } value || !truth.TryAdd(member.Name, value))
                    throw new QualificationRunException(QualificationRunCodes.GroundTruthInvalid, captureId);
            }
        }
        catch (JsonException)
        {
            throw new QualificationRunException(QualificationRunCodes.GroundTruthInvalid, captureId);
        }

        return truth;
    }

    private static string Resolve(string folder, string relative) => Path.GetFullPath(Path.Combine(folder, relative.Replace('\\', '/')));

    private static QualificationEvaluatedSummary Summarize(IReadOnlyList<CapturePlan> plans)
    {
        static Dictionary<string, int> Count(IEnumerable<string?> values)
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in values.OfType<string>())
                counts[value] = counts.GetValueOrDefault(value) + 1;

            return new Dictionary<string, int>(counts, StringComparer.Ordinal);
        }

        return new QualificationEvaluatedSummary(
            plans.Count,
            Count(plans.Select(plan => plan.Kind)),
            Count(plans.Select(plan => plan.Capture.AttackClass)),
            Count(plans.Select(plan => plan.Capture.Document?.Format)),
            Count(plans.Select(plan => plan.Capture.Channel)));
    }

    // ───── one capture ─────

    /// <summary>The frames of a sequence that passed its validation and gave frames to compare, kept for the impostor trials of its person; zeroed with the run.</summary>
    private sealed record RetainedSequence(FrameSequence Sequence, IReadOnlyList<int> MatchIndices);

    private sealed class CaptureRun(QualificationCaseResult result, FrameSequence? sequence, IReadOnlyList<int> matchIndices) : IDisposable
    {
        public QualificationCaseResult Result { get; } = result;

        public RetainedSequence? Retained { get; } = sequence is null ? null : new RetainedSequence(sequence, matchIndices);

        public void Dispose() => sequence?.Dispose();
    }

    private async Task<CaptureRun> RunCaptureAsync(CapturePlan plan, string folder, DateOnly day, IdentityDecisionPolicy policy, CancellationToken ct)
    {
        var capture = plan.Capture;
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        DocumentStage? document = null;
        PresenceStage? presence = null;
        QualificationDecisionResult? decision = null;
        string? error = null;
        try
        {
            if (capture.Document is { } documentPart)
                document = await RunDocumentAsync(plan, documentPart, folder, day, policy, stages, ct);

            if (capture.Sequence is { } sequencePart)
                presence = await RunPresenceAsync(plan, sequencePart, capture.Document, folder, policy, stages, ct);

            if (document is not null && presence is not null)
            {
                var started = Stopwatch.GetTimestamp();
                decision = Decide(capture, document, presence, policy);
                Add(stages, QualificationStages.Decision, started);
            }
        }
        catch (OperationCanceledException)
        {
            presence?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // The type of the exception only: its message could carry what was being read.
            error = ex.GetType().Name;
        }

        // A part the capture has is in the result even when it broke, so that the denominators of the rates count it.
        document ??= capture.Document is null ? null : DocumentStage.Failed(capture.Document.Format);
        presence ??= capture.Sequence is null ? null : PresenceStage.Failed();
        var result = new QualificationCaseResult(
            plan.Id,
            plan.Kind,
            capture.AttackClass,
            capture.Channel,
            capture.Document?.Format,
            capture.Document?.Altered ?? false,
            capture.Document?.Foreign ?? false,
            document?.Result,
            presence?.Result,
            decision,
            presence?.PairedMatch,
            presence?.PairedLiveness,
            stages,
            error,
            plan.Kind == QualificationCaseKinds.ImpostorPair && capture.Document?.OwnerPerson is { } owner ? QualificationMetrics.CoupleKey(plan.Person, owner) : null);

        // The sequence stays in memory only for a genuine capture whose frames were chosen: it is what the impostor trials of its person are made with.
        var keep = plan.Kind == QualificationCaseKinds.Genuine && presence is { Sequence: not null, MatchIndices.Count: > 0 };
        if (!keep)
        {
            presence?.Dispose();
            return new CaptureRun(result, null, []);
        }

        return new CaptureRun(result, presence!.Sequence, presence.MatchIndices);
    }

    private QualificationDecisionResult Decide(QualificationCapture capture, DocumentStage document, PresenceStage presence, IdentityDecisionPolicy policy)
    {
        var evidence = presence.Evidence;
        var bundle = new IdentityEvidenceBundle(
            IdentityPolicyState.Ready,
            null,
            document.Complete,
            document.Coherence,
            document.Format?.Id,
            capture.Channel,
            pipeline.Formats.AssuranceFor(document.Format?.Id, capture.Channel),
            evidence?.Checks(policy) ?? [],
            evidence?.Match is { } match ? MatchSummary.From(match, policy.Match) : null,
            evidence?.ModelsFault ?? IdentityModelsFault.None,
            QualifiedSegment,
            0,
            policy.Closure.GuidedRetries);
        var decision = pipeline.Engine.Decide(bundle);
        return new QualificationDecisionResult(
            decision.Final switch
            {
                IdentityFinalDecision.AutoApproved => QualificationDecisionCodes.FinalAutoApproved,
                IdentityFinalDecision.GuidedRetry => QualificationDecisionCodes.FinalGuidedRetry,
                _ => QualificationDecisionCodes.FinalReview,
            },
            decision.Shadow == IdentityShadowDecision.Approve ? QualificationDecisionCodes.ShadowApprove : QualificationDecisionCodes.ShadowReview,
            decision.Mode.ToString().ToLowerInvariant(),
            decision.OutcomeCode,
            [.. decision.Reasons.Select(reason => reason.Check + ":" + reason.Result)],
            decision.Guidance);
    }

    // ───── the document part ─────

    private sealed record DocumentStage(QualificationDocumentResult Result, DocumentFormat? Format, bool Complete, IdentityDocumentCoherence Coherence)
    {
        public static DocumentStage Failed(string? format)
        {
            return new DocumentStage(
                new QualificationDocumentResult(format, false, QualificationDecisionCodes.CoherenceUnknown, null, [QualificationDocumentCodes.ProcessingError], [], []),
                null,
                false,
                IdentityDocumentCoherence.Unknown);
        }
    }

    /// <summary>What one side gave: its result, the fields read on it, the reading of its MRZ, the fingerprint of the picture, and whether the picture was clean (no guidance code).</summary>
    private sealed record SideOutcome(QualificationSideResult Result, IReadOnlyList<ZonedField> Fields, MrzReading Mrz, DocumentPictureFingerprint? Fingerprint, bool Clean, bool PortraitInZone);

    private async Task<DocumentStage> RunDocumentAsync(
        CapturePlan plan, QualificationDocument document, string folder, DateOnly day, IdentityDecisionPolicy policy, Dictionary<string, double> stages, CancellationToken ct)
    {
        var format = pipeline.Formats.Get(document.Format);
        if (format is null)
        {
            // A format the registry does not hold is a document a person decides, never approved.
            return new DocumentStage(
                new QualificationDocumentResult(null, false, QualificationDecisionCodes.CoherenceUnknown, null, [IdentityGuidanceCodes.DocumentFormatUnsupported], [], []),
                null,
                false,
                IdentityDocumentCoherence.Unknown);
        }

        var portraitSide = DocumentPictureAnalyzer.PortraitSideOf(format);
        var codes = new List<string>();
        var sides = new List<QualificationSideResult>();
        var fields = new List<ZonedField>();
        var cleanSides = new HashSet<string>(StringComparer.Ordinal);
        MrzReading? machine = null;
        var portraitFound = false;
        DocumentPictureFingerprint? previous = null;
        for (var index = 0; index < format.Sides.Count; index++)
        {
            var sideCode = format.Sides[index];
            DocumentSides.TryParse(sideCode, out var side);
            var relative = index == 0 ? document.Front : document.Back;
            if (string.IsNullOrWhiteSpace(relative))
            {
                codes.Add(QualificationDocumentCodes.SideMissing);
                sides.Add(new QualificationSideResult(sideCode, null, false, null, 0, [QualificationDocumentCodes.SideMissing]));
                continue;
            }

            var outcome = await ReadSideAsync(format, side, Resolve(folder, relative), previous, policy, stages, ct);
            sides.Add(outcome.Result);
            codes.AddRange(outcome.Result.Codes);
            fields.AddRange(outcome.Fields);
            previous ??= outcome.Fingerprint;
            machine ??= outcome.Mrz.Result is null ? null : outcome.Mrz;
            if (outcome.Clean)
                cleanSides.Add(sideCode);

            if (portraitSide is { } holder && holder == side && outcome.PortraitInZone)
                portraitFound = true;
        }

        var allClean = cleanSides.Count == format.Sides.Count;
        var coherence = IdentityDocumentCoherence.Unknown;
        if (allClean)
        {
            var started = Stopwatch.GetTimestamp();
            var report = pipeline.Coherence.Check(fields, machine, new DocumentCoherenceContext(day, DeclaredIdentity(plan.Truth)), format, policy);
            coherence = CoherenceOf(report, format);
            Add(stages, QualificationStages.DocumentCoherence, started);
        }

        // Complete as the flow means it: every side of the format passed its checks, the portrait is where the layout puts it, and the MRZ was read on a side.
        var complete = allClean && portraitFound && machine is not null;
        var result = new QualificationDocumentResult(
            format.Id,
            complete,
            CoherenceCode(coherence),
            FormatAgrees(format, machine),
            [.. codes.Distinct(StringComparer.Ordinal)],
            sides,
            ExactnessOf(plan.Truth, format, cleanSides, fields));
        return new DocumentStage(result, format, complete, coherence);
    }

    private async Task<SideOutcome> ReadSideAsync(
        DocumentFormat format,
        DocumentSide side,
        string path,
        DocumentPictureFingerprint? other,
        IdentityDecisionPolicy policy,
        Dictionary<string, double> stages,
        CancellationToken ct)
    {
        var sideCode = DocumentSides.CodeOf(side);
        byte[]? bytes = null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            bytes = await File.ReadAllBytesAsync(path, ct);

            // The quality of the capture, as the agent assesses it before the MRZ is read.
            IReadOnlyList<string> assessment;
            RasterImage? decoded = null;
            try
            {
                decoded = RasterImage.Decode(bytes, pipeline.BiometricOptions.MaxImagePixels);
                assessment = pipeline.CaptureQuality.Assess(decoded, side == DocumentSide.Back ? CaptureKind.DocumentBack : CaptureKind.Document, ct).GuidanceCodes;
            }
            catch (InvalidDataException)
            {
                return Unreadable(sideCode, [CaptureGuidanceCodes.RetakePicture]);
            }
            finally
            {
                decoded?.Dispose();
            }

            Add(stages, QualificationStages.DocumentCapture, started);

            started = Stopwatch.GetTimestamp();
            MrzReading mrz;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                mrz = await pipeline.Mrz.ReadAsync(stream, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                mrz = new MrzReading(null, []);
            }

            Add(stages, QualificationStages.DocumentMrz, started);

            started = Stopwatch.GetTimestamp();
            DocumentPicture picture;
            try
            {
                picture = pipeline.Pictures.Open(bytes);
            }
            catch (InvalidDataException)
            {
                return Unreadable(sideCode, [CaptureGuidanceCodes.RetakePicture]);
            }

            using (picture)
            {
                var report = picture.Inspect(format, side, ct);
                var shown = pipeline.Sides.Classify(report, mrz.Result is not null, format);
                var fingerprint = new DocumentPictureFingerprint(Convert.ToHexString(SHA256.HashData(bytes)), picture.DHash(), shown);
                var pair = pipeline.Sides.CheckPair(side, fingerprint, other, policy.Document.PerceptualDuplicateMaxDistance);

                // The codes of the flow, in its order: the repeated or wrong side, the geometry of the card, the quality of the capture.
                var codes = new List<string>();
                if (pair is not null)
                    codes.Add(pair);

                codes.AddRange(report.Codes);
                codes.AddRange(assessment);
                var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
                Add(stages, QualificationStages.DocumentGeometry, started);

                // The zones are read only on a picture the flow would accept: one with a guidance code is asked for again.
                IReadOnlyList<ZonedField> fields = [];
                if (distinct.Count == 0)
                {
                    started = Stopwatch.GetTimestamp();
                    fields = await picture.ReadFieldsAsync(format, side, report, ct);
                    Add(stages, QualificationStages.DocumentFields, started);
                }

                var result = new QualificationSideResult(
                    sideCode,
                    shown is { } showed ? DocumentSides.CodeOf(showed) : null,
                    mrz.Result is not null,
                    mrz.Result?.CheckDigitsValid,
                    fields.Count,
                    distinct);
                return new SideOutcome(result, fields, mrz, fingerprint, distinct.Count == 0, report.PortraitInZone == true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A model or a reader that fails never approves: the side is not clean, and the type of the failure is all that is kept.
            return Unreadable(sideCode, [QualificationDocumentCodes.ProcessingError]);
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    private static SideOutcome Unreadable(string sideCode, IReadOnlyList<string> codes)
    {
        return new SideOutcome(new QualificationSideResult(sideCode, null, false, null, 0, codes), [], new MrzReading(null, []), null, false, false);
    }

    /// <summary>
    /// The coherence as the decision reads it: the very rule of the flow (<see cref="IdentityDocumentCoherences"/>), an identifying conflict, an invalid MRZ, an expired document, a
    /// value declared for the booking that differs from the document, then a pass only when every check the format requires holds.
    /// </summary>
    private static IdentityDocumentCoherence CoherenceOf(DocumentCoherenceReport report, DocumentFormat format) => IdentityDocumentCoherences.Of(report, format);

    private static string CoherenceCode(IdentityDocumentCoherence coherence) => coherence switch
    {
        IdentityDocumentCoherence.Passed => QualificationDecisionCodes.CoherencePassed,
        IdentityDocumentCoherence.IdentifyingConflict => QualificationDecisionCodes.CoherenceIdentifyingConflict,
        IdentityDocumentCoherence.MrzInvalid => QualificationDecisionCodes.CoherenceMrzInvalid,
        IdentityDocumentCoherence.Expired => QualificationDecisionCodes.CoherenceExpired,
        IdentityDocumentCoherence.BookingConflict => QualificationDecisionCodes.CoherenceBookingConflict,
        _ => QualificationDecisionCodes.CoherenceUnknown,
    };

    /// <summary>Whether the format the registry detects from the MRZ is the declared one; null when no MRZ was read.</summary>
    private bool? FormatAgrees(DocumentFormat declared, MrzReading? machine)
    {
        if (machine?.Result is not { } read)
            return null;

        return string.Equals(pipeline.Formats.DetectFromMrz(read.DocumentCode, read.IssuingState, read.MrzType)?.Id, declared.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the guest would have typed, from the ground truth: the check of the document against the booking compares the document with the names, the birth date, the number and
    /// the citizenship of the guest, and a guest of the dataset types what is true. The values live in memory for the check and are never printed.
    /// </summary>
    private static CheckInIdentity DeclaredIdentity(IReadOnlyDictionary<string, string> truth)
    {
        DateOnly? birth = truth.TryGetValue(ZonedFieldNames.BirthDate, out var text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        return new CheckInIdentity(
            truth.GetValueOrDefault(ZonedFieldNames.GivenNames),
            truth.GetValueOrDefault(ZonedFieldNames.Surname),
            birth,
            truth.GetValueOrDefault(ZonedFieldNames.DocumentNumber),
            truth.GetValueOrDefault(ZonedFieldNames.Nationality));
    }

    /// <summary>
    /// How many of the values read were exact, per field of the ground truth, through <see cref="IdentityStatistics.FieldExactness"/> (the very comparison the dashboard makes). The value read
    /// in the visual zone is measured as <c>surname</c>, the one of the MRZ as <c>surname.mrz</c>; a field is measured only where it was read for: the side of the layout that has it (visual
    /// zone) or the side of the MRZ, and only on a side whose picture was clean (the zones of a picture that is asked for again are not read). A value that is not read is a miss. Only the
    /// names of the fields and the counts come out of this method.
    /// </summary>
    private static List<QualificationFieldResult> ExactnessOf(
        IReadOnlyDictionary<string, string> truth, DocumentFormat format, HashSet<string> cleanSides, IReadOnlyList<ZonedField> read)
    {
        var pairs = new List<(string Field, string? Expected, string? Read)>();
        foreach (var (name, expected) in truth)
        {
            var layoutSides = format.Layout.Sides.Where(side => side.Value.Fields.Any(field => string.Equals(field.Name, name, StringComparison.Ordinal))).Select(side => side.Key).ToList();
            if (layoutSides.Any(cleanSides.Contains))
            {
                var printed = read.Where(field => field.Name == name && field.Source == ZonedFieldSources.Viz).Select(field => field.Value).ToList();
                if (printed.Count == 0)
                    pairs.Add((name, expected, null));

                pairs.AddRange(printed.Select(value => (name, (string?)expected, (string?)value)));
            }

            if (MrzFields.Contains(name) && cleanSides.Contains(format.Mrz.Side))
            {
                var machine = read.Where(field => field.Name == name && field.Source == ZonedFieldSources.Mrz).Select(field => field.Value).ToList();
                var key = name + QualificationMetrics.MrzFieldSuffix;
                if (machine.Count == 0)
                    pairs.Add((key, expected, null));

                pairs.AddRange(machine.Select(value => (key, (string?)expected, (string?)value)));
            }
        }

        return [.. QualificationMetrics.ExactnessRates(pairs).Select(rate => new QualificationFieldResult(rate.Name, (int)rate.Rate.Count, (int)rate.Rate.Total))];
    }

    // ───── the presence part ─────

    private sealed class PresenceStage(QualificationPresenceResult result) : IDisposable
    {
        public QualificationPresenceResult Result { get; } = result;

        public PresenceEvidence? Evidence { get; init; }

        public FrameSequence? Sequence { get; init; }

        public IReadOnlyList<int> MatchIndices { get; init; } = [];

        public QualificationPairedScores? PairedMatch { get; set; }

        public QualificationPairedScores? PairedLiveness { get; set; }

        public static PresenceStage Failed()
        {
            return new PresenceStage(new QualificationPresenceResult(null, 0, 0, false, false, [QualificationDocumentCodes.ProcessingError], null, [], [], QualificationDecisionCodes.NotRun, [], QualificationDecisionCodes.NotRun, null, null, null, false));
        }

        public void Dispose() => Sequence?.Dispose();
    }

    private async Task<PresenceStage> RunPresenceAsync(
        CapturePlan plan,
        QualificationSequence sequencePart,
        QualificationDocument? document,
        string folder,
        IdentityDecisionPolicy policy,
        Dictionary<string, double> stages,
        CancellationToken ct)
    {
        // The frames: the JPEG files of the folder in the order of their names, with the timestamps the file gives; read through the intake of the application, with its limits.
        var started = Stopwatch.GetTimestamp();
        var directory = Resolve(folder, sequencePart.Dir!);
        var files = Directory.EnumerateFiles(directory)
            .Where(file => file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        var timestamps = await File.ReadAllTextAsync(Resolve(folder, sequencePart.TimestampsFile!), ct);
        var buffers = new List<byte[]>(files.Count);
        var parts = new List<FramePart>(files.Count);
        FrameSequenceReadResult read;
        var receivedAt = pipeline.Clock.GetUtcNow();
        try
        {
            foreach (var file in files)
            {
                var bytes = await File.ReadAllBytesAsync(file, ct);
                buffers.Add(bytes);
                parts.Add(new FramePart(new MemoryStream(bytes, writable: false), bytes.Length, "image/jpeg"));
            }

            read = await pipeline.FrameReader.ReadAsync(parts, timestamps, receivedAt, ct);
        }
        finally
        {
            foreach (var buffer in buffers)
                ImageBytes.Zero(buffer);
        }

        Add(stages, QualificationStages.PresenceIntake, started);
        if (!read.Succeeded)
        {
            return new PresenceStage(new QualificationPresenceResult(read.ErrorCode, files.Count, 0, false, false, [read.ErrorCode], null, [], [], QualificationDecisionCodes.NotRun, [], QualificationDecisionCodes.NotRun, null, null, null, false));
        }

        var sequence = read.Sequence;
        var disposeSequence = true;
        try
        {
            // The recording started when the challenge was issued: the check of «a recording longer than the time since the challenge» cannot be made offline (a dataset has no
            // moment of issue), so it is neutral here and never decides a case.
            var issuedAt = receivedAt - TimeSpan.FromMilliseconds(sequence.DurationMs);
            var challenge = new StoredPresenceChallenge(Guid.Empty, sequencePart.Challenge!, issuedAt, issuedAt.AddSeconds(policy.Presence.Challenge.LifetimeSeconds), 1);

            started = Stopwatch.GetTimestamp();
            var validation = await pipeline.Validator.ValidateAsync(sequence, challenge, policy, ct);
            Add(stages, QualificationStages.PresenceValidation, started);

            FrameSelection? selection = null;
            LivenessReport? liveness = null;
            MatchReport? match = null;
            IReadOnlyList<int> matchFrames = [];
            QualificationPairedScores? pairedMatch = null;
            QualificationPairedScores? pairedLiveness = null;
            if (validation.Passed)
            {
                started = Stopwatch.GetTimestamp();
                selection = pipeline.Selector.Select(sequence, validation, policy);
                Add(stages, QualificationStages.PresenceSelection, started);
            }

            if (selection is { IsSelected: true })
            {
                var chosen = selection.Frames;
                matchFrames = chosen.MatchIndices;
                started = Stopwatch.GetTimestamp();
                liveness = await pipeline.Liveness.EvaluateAsync(sequence, chosen.LivenessIndices, policy, ct);
                Add(stages, QualificationStages.PresenceLiveness, started);

                // The comparison is made whether or not the liveness passed (the decision does not use it then): the score of a genuine capture is what the false non-match rate is measured on.
                if (document?.Front is { } front)
                {
                    (match, pairedMatch) = await MatchAsync(plan.Id, Resolve(folder, front), sequence, chosen.MatchIndices, policy, stages, ct);
                }

                if (pipeline.HasAntiSpoofingCandidate)
                    pairedLiveness = await PairedLivenessAsync(plan.Id, sequence, chosen.LivenessIndices, ct);
            }

            // What the pipeline of the closure would hand the engine: a comparison with the document is made only for frames that are live.
            var evidence = new PresenceEvidence(validation, selection?.Failure, liveness, liveness is { Passed: true } ? match : null, selection?.Frames);
            var accepted = validation.Passed && selection is { IsSelected: true } && liveness is { Passed: true };
            var failures = validation.Failures.Select(found => found.Code ?? found.Check).ToList();
            if (selection?.Failure is { } notChosen)
                failures.Add(notChosen.Code ?? notChosen.Check);

            var result = new QualificationPresenceResult(
                null,
                sequence.Frames.Count,
                sequence.DurationMs,
                validation.Passed,
                validation.IsUnavailable,
                failures,
                selection?.Failure?.Check,
                matchFrames,
                selection?.Frames?.LivenessIndices ?? [],
                liveness switch
                {
                    { State: LivenessState.Passed } => QualificationDecisionCodes.LivenessPassed,
                    { State: LivenessState.Failed } => QualificationDecisionCodes.LivenessFailed,
                    { State: LivenessState.Unavailable } => QualificationDecisionCodes.LivenessUnavailable,
                    _ => QualificationDecisionCodes.NotRun,
                },
                [.. liveness?.Frames.Select(frame => frame.Score) ?? []],
                match switch
                {
                    { State: MatchState.Ok } => QualificationDecisionCodes.MatchOk,
                    { State: MatchState.NoPortrait } => QualificationDecisionCodes.MatchNoPortrait,
                    { State: MatchState.Unavailable } => QualificationDecisionCodes.MatchUnavailable,
                    _ => QualificationDecisionCodes.NotRun,
                },
                match is { State: MatchState.Ok } ? match.Aggregate : null,
                match is { State: MatchState.Ok } ? match.Min : null,
                match is { State: MatchState.Ok } ? match.AcceptPassed : null,
                accepted);

            disposeSequence = false;
            return new PresenceStage(result)
            {
                Evidence = evidence,
                Sequence = sequence,
                MatchIndices = matchFrames,
                PairedMatch = pairedMatch,
                PairedLiveness = pairedLiveness,
            };
        }
        finally
        {
            if (disposeSequence)
                sequence.Dispose();
        }
    }

    /// <summary>
    /// The comparison of the front of the document with the chosen frames by the matcher of the application, and, when a candidate of the recognition model is loaded, the paired
    /// scores of the reference and the candidate on the same pictures. A picture that cannot be decoded is a comparison that could not be made, never a match.
    /// </summary>
    private async Task<(MatchReport Match, QualificationPairedScores? Paired)> MatchAsync(
        string caseId,
        string frontPath,
        FrameSequence sequence,
        IReadOnlyList<int> matchIndices,
        IdentityDecisionPolicy policy,
        Dictionary<string, double> stages,
        CancellationToken ct)
    {
        byte[]? bytes = null;
        RasterImage? front = null;
        try
        {
            bytes = await File.ReadAllBytesAsync(frontPath, ct);
            try
            {
                front = RasterImage.Decode(bytes, pipeline.BiometricOptions.MaxImagePixels);
            }
            catch (InvalidDataException)
            {
                return (MatchReport.NotAvailable(), null);
            }

            var started = Stopwatch.GetTimestamp();
            var match = await pipeline.Matcher.MatchAsync(front, sequence, matchIndices, policy, ct);
            Add(stages, QualificationStages.PresenceMatch, started);
            var paired = pipeline.HasRecognitionCandidate ? await PairedMatchAsync(caseId, front, sequence, matchIndices, ct) : null;
            return (match, paired);
        }
        finally
        {
            ImageBytes.Zero(bytes);
            front?.Dispose();
        }
    }

    // ───── the impostor trials derived from the sequences ─────

    private async Task<QualificationImpostorTrial?> RunImpostorTrialAsync(
        string sequenceCaseId, string sequencePerson, PoolDocument document, RetainedSequence retained, IdentityDecisionPolicy policy, CancellationToken ct)
    {
        byte[]? bytes = null;
        RasterImage? front = null;
        try
        {
            bytes = await File.ReadAllBytesAsync(document.FrontPath, ct);
            try
            {
                front = RasterImage.Decode(bytes, pipeline.BiometricOptions.MaxImagePixels);
            }
            catch (InvalidDataException)
            {
                return null;
            }

            var started = Stopwatch.GetTimestamp();
            var match = await pipeline.Matcher.MatchAsync(front, retained.Sequence, retained.MatchIndices, policy, ct);
            var elapsed = Since(started);

            // A document with no face in it (or a comparison that could not be made) compares nothing: it is no trial.
            if (match.State != MatchState.Ok)
                return null;

            var paired = pipeline.HasRecognitionCandidate
                ? await PairedMatchAsync(sequenceCaseId + "~" + document.CaptureId, front, retained.Sequence, retained.MatchIndices, ct)
                : null;
            return new QualificationImpostorTrial(sequenceCaseId, document.CaptureId, match.Aggregate, match.Min, elapsed, paired, QualificationMetrics.CoupleKey(sequencePerson, document.Owner));
        }
        finally
        {
            ImageBytes.Zero(bytes);
            front?.Dispose();
        }
    }

    // ───── the candidate models, on the same cases ─────

    private async Task<QualificationPairedScores?> PairedMatchAsync(string caseId, RasterImage front, FrameSequence sequence, IReadOnlyList<int> matchIndices, CancellationToken ct)
    {
        var frames = matchIndices.Select(index => sequence.Frames[index].Image).ToList();
        var results = await pipeline.CandidateComparison.CompareAsync([new MatchCase(caseId, front, frames)], ct);
        var result = results[0];
        if (!result.Reference.Ran || result.Reference.Scores.Count == 0)
            return null;

        return new QualificationPairedScores(
            result.Reference.Scores,
            result.Candidate is { Ran: true } candidate ? candidate.Scores : null,
            result.Candidate is { Ran: false } failed ? failed.FailureCode : null);
    }

    private async Task<QualificationPairedScores?> PairedLivenessAsync(string caseId, FrameSequence sequence, IReadOnlyList<int> livenessIndices, CancellationToken ct)
    {
        var cases = livenessIndices.Select(index => (CandidateCase)new LivenessCase(caseId + "#" + index.ToString(CultureInfo.InvariantCulture), sequence.Frames[index].Jpeg)).ToList();
        var results = await pipeline.CandidateComparison.CompareAsync(cases, ct);
        var reference = new List<double>(results.Count);
        var candidate = new List<double>(results.Count);
        string? code = null;
        foreach (var result in results)
        {
            // A frame the reference did not score cannot be paired with anything.
            if (!result.Reference.Ran || result.Reference.Scores.Count == 0)
                return null;

            reference.Add(result.Reference.Scores[0]);
            if (result.Candidate is { Ran: true } run && run.Scores.Count > 0)
                candidate.Add(run.Scores[0]);
            else
                code ??= result.Candidate?.FailureCode;
        }

        var complete = candidate.Count == reference.Count;
        return new QualificationPairedScores(reference, complete ? candidate : null, complete ? null : code);
    }

    // ───── time ─────

    private static double Since(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void Add(Dictionary<string, double> stages, string stage, long started)
    {
        stages[stage] = stages.GetValueOrDefault(stage) + Since(started);
    }
}
