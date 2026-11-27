using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Infrastructure.Identity;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12): what a qualification run writes (results.json) and what the calibration, the lock and the report of the candidate version read back.
// PRIVACY: a result holds anonymous ids (the id of a capture, which the manifest keeps free of a name), codes, counts, scores of those cases, the places of the frames in
// a sequence and hashes. It never holds an image, a frame, a value read on a document, a ground-truth value, a name, a number of a document or an embedding: the types
// have no member for any of them, so nothing can be put there by mistake. Codes and constants are never renamed or reused: a new one goes at the end of its list.

/// <summary>The kinds of case of a run: the capture of a person present with their own document, an attack, and a person present with the document of another one.</summary>
public static class QualificationCaseKinds
{
    /// <summary>A real person present, with their own document (or none): the genuine trial.</summary>
    public const string Genuine = "genuine";

    /// <summary>A presentation attack of a class of <see cref="QualificationAttackClasses"/>.</summary>
    public const string Attack = "attack";

    /// <summary>A real person present with the document of ANOTHER person of the dataset (the manifest says so): an impostor trial that is a whole check-in.</summary>
    public const string ImpostorPair = "impostor_pair";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Genuine, Attack, ImpostorPair];
}

/// <summary>The stages whose time a run measures (the p95 of each goes to the report), in the order the pipeline runs them.</summary>
public static class QualificationStages
{
    /// <summary>Reading a picture of the document from the folder and the quality of its capture.</summary>
    public const string DocumentCapture = "document_capture";

    /// <summary>The MRZ of a picture of the document.</summary>
    public const string DocumentMrz = "document_mrz";

    /// <summary>The geometry of the card, the side it shows and the repeated side.</summary>
    public const string DocumentGeometry = "document_geometry";

    /// <summary>The fields read by zone on the card.</summary>
    public const string DocumentFields = "document_fields";

    /// <summary>The coherence of the sides, the dates and the MRZ.</summary>
    public const string DocumentCoherence = "document_coherence";

    /// <summary>Reading the frames of the sequence with the limits of the intake.</summary>
    public const string PresenceIntake = "presence_intake";

    /// <summary>The validation of the sequence (timing, faces, quality, continuity, challenge).</summary>
    public const string PresenceValidation = "presence_validation";

    /// <summary>The choice of the frames by the server.</summary>
    public const string PresenceSelection = "presence_selection";

    /// <summary>The liveness of the chosen frames.</summary>
    public const string PresenceLiveness = "presence_liveness";

    /// <summary>The comparison of the portrait of the document with the chosen frames.</summary>
    public const string PresenceMatch = "presence_match";

    /// <summary>The decision of the policy.</summary>
    public const string Decision = "decision";

    /// <summary>Every stage, in this order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DocumentCapture, DocumentMrz, DocumentGeometry, DocumentFields, DocumentCoherence, PresenceIntake, PresenceValidation, PresenceSelection, PresenceLiveness,
        PresenceMatch, Decision,
    ];
}

/// <summary>The policy a run was made with: its version and canonical hash, whether it was provisional, and the fields that identify the holder (the ones whose exactness is a target).</summary>
public sealed record QualificationPolicyInfo(string Version, string CanonicalHash, bool Provisional, IReadOnlyList<string> IdentifyingFields);

/// <summary>
/// The thresholds and the aggregation rules of a policy that the qualification freezes: the rule that combines the similarities of the frames the server chose (and how many it
/// compares) with the three thresholds of the match, and the rule that combines the liveness of the chosen frames (and how many it judges) with its minimum. Numbers of the
/// policy file, never of the code.
/// </summary>
public sealed record QualificationThresholds(
    string MatchAggregation,
    int MatchFrames,
    double Accept,
    double ReviewFloor,
    double MinEachFrame,
    string LivenessRule,
    int LivenessFrames,
    double LivenessMinScore)
{
    /// <summary>The thresholds and the rules of <paramref name="policy"/>.</summary>
    public static QualificationThresholds From(IdentityDecisionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new QualificationThresholds(
            policy.Match.Aggregation,
            policy.Match.Frames,
            policy.Match.Accept,
            policy.Match.ReviewFloor,
            policy.Match.MinEachFrame,
            policy.Presence.Liveness.Rule,
            policy.Presence.Liveness.Frames,
            policy.Presence.Liveness.MinScore);
    }
}

/// <summary>A model of the run, as the store reported it: who it is, its licence and provenance (from <c>BiometricModelOptions</c>) and the SHA-256 of the weights that were proved.</summary>
public sealed record QualificationModelInfo(string Role, string Name, string Version, string License, string Provenance, string? WeightSha256, bool Loaded, string? FailureCode);

/// <summary>
/// What the dataset holds, in numbers: the counts of every split (the persons, the captures, the impostor pairs, the altered and foreign documents and the attacks of each class), and
/// of the split that was run the captures by kind, attack class, format and channel. No name, no id, no path.
/// </summary>
public sealed record QualificationDatasetSummary(IReadOnlyList<QualificationSplitCounts> Splits, QualificationEvaluatedSummary Evaluated);

/// <summary>The captures of the split that was run, counted by kind, attack class, document format and channel (a capture with no document or no channel is not counted for it).</summary>
public sealed record QualificationEvaluatedSummary(
    int Captures,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> ByAttackClass,
    IReadOnlyDictionary<string, int> ByFormat,
    IReadOnlyDictionary<string, int> ByChannel);

/// <summary>
/// How many impostor trials derived from the sequence of a person against the documents of the others there could be (<see cref="PairsAvailable"/>), how many were run
/// (<see cref="PairsRun"/>), whether the run took a deterministic sample because there were more than <see cref="Cap"/>, and how many sequences gave no frames to compare (their
/// validation or the choice of the frames did not pass, so they could not be compared with any document).
/// </summary>
public sealed record QualificationImpostorSampling(long PairsAvailable, long PairsRun, bool Sampled, int Cap, int SequencesWithoutFrames);

/// <summary>What the document part of a run found on one side of the card: the side the picture showed, whether its MRZ was read, and the guidance codes the picture raised.</summary>
public sealed record QualificationSideResult(string Side, string? Shown, bool MrzRead, bool? MrzCheckDigitsValid, int FieldsRead, IReadOnlyList<string> Codes);

/// <summary>
/// How many of the values read for one field of the ground truth were exact (<see cref="Exact"/> of <see cref="Total"/>): the field is named as the zoned reader names it
/// (<c>surname</c>), with <c>.mrz</c> after the name for the value of the MRZ. Never the value read, nor the true one.
/// </summary>
public sealed record QualificationFieldResult(string Field, int Exact, int Total);

/// <summary>
/// The document part of one case: the format of the registry the dataset declares for it, whether every side passed its checks and the MRZ and the portrait were found (the
/// document is complete), the coherence of the sides as the decision reads it (<c>passed</c>, <c>identifying_conflict</c>, <c>mrz_invalid</c>, <c>expired</c> or
/// <c>unknown</c>), whether the format the MRZ names is the declared one, and the codes the pictures raised.
/// </summary>
public sealed record QualificationDocumentResult(
    string? Format,
    bool Complete,
    string Coherence,
    bool? MrzFormatAgrees,
    IReadOnlyList<string> Codes,
    IReadOnlyList<QualificationSideResult> Sides,
    IReadOnlyList<QualificationFieldResult> Fields);

/// <summary>
/// The presence part of one case. <see cref="Accepted"/> is the verdict of the presence alone, as the classification of an attack is stated (the sequence is valid, the server
/// could choose its frames and every one of them is live): the comparison with a document is not part of it. The places of the chosen frames are indexes in the sequence.
/// </summary>
/// <param name="IntakeError">The code of the first limit of the intake the frames broke (<c>presence_sequence_invalid</c>, <c>presence_sequence_too_large</c>); null when they were read.</param>
/// <param name="Frames">How many frames the folder holds.</param>
/// <param name="DurationMs">The time from the first frame to the last.</param>
/// <param name="ValidationPassed">The validation of the sequence passed.</param>
/// <param name="ValidationUnavailable">The sequence could not be judged (the models were not there, one failed or the time was over).</param>
/// <param name="ValidationFailures">The guidance code (or, for a failure of the processing, the check) of each failure of the validation and of the choice of the frames.</param>
/// <param name="SelectionFailure">The check that stopped the choice of the frames; null when they were chosen or the validation did not pass.</param>
/// <param name="MatchFrames">The places of the frames compared with the portrait.</param>
/// <param name="LivenessFrames">The places of the frames whose liveness was judged.</param>
/// <param name="LivenessState">How the liveness came out: <c>passed</c>, <c>failed</c>, <c>unavailable</c> or <c>not_run</c>.</param>
/// <param name="LivenessScores">The score of every chosen frame, in the order of <paramref name="LivenessFrames"/>; empty when it was not judged.</param>
/// <param name="MatchState">How the comparison came out: <c>ok</c>, <c>no_portrait</c>, <c>unavailable</c> or <c>not_run</c> (no document, or the frames were not chosen).</param>
/// <param name="Similarity">The similarities of the chosen frames combined by the rule of the policy; null unless the comparison is <c>ok</c>.</param>
/// <param name="MinSimilarity">The lowest similarity of a chosen frame; null unless the comparison is <c>ok</c>.</param>
/// <param name="AcceptPassed">The verdict of the matcher under the policy in force (the aggregate reaches the acceptance and every frame reaches its minimum); null unless the comparison is <c>ok</c>.</param>
/// <param name="Accepted">The presence is accepted as a person present: valid, chosen and live.</param>
public sealed record QualificationPresenceResult(
    string? IntakeError,
    int Frames,
    long DurationMs,
    bool ValidationPassed,
    bool ValidationUnavailable,
    IReadOnlyList<string> ValidationFailures,
    string? SelectionFailure,
    IReadOnlyList<int> MatchFrames,
    IReadOnlyList<int> LivenessFrames,
    string LivenessState,
    IReadOnlyList<double> LivenessScores,
    string MatchState,
    double? Similarity,
    double? MinSimilarity,
    bool? AcceptPassed,
    bool Accepted);

/// <summary>
/// What the policy decided for a case that is a whole check-in (a document and a sequence), with a segment qualified for the test: <see cref="Final"/> (<c>review</c>,
/// <c>guided_retry</c> or <c>auto_approved</c>), <see cref="Shadow"/> (<c>review</c> or <c>approve</c>), the mode and outcome code, and the reasons as <c>check:result</c> codes (the
/// numbers of the checks are in the scores of the case).
/// </summary>
public sealed record QualificationDecisionResult(string Final, string Shadow, string Mode, string? OutcomeCode, IReadOnlyList<string> Reasons, IReadOnlyList<string> Guidance);

/// <summary>
/// The scores of the reference model and of the candidate on the very same pictures: for a match the similarity of each chosen frame with the portrait, for the liveness the score of
/// each chosen frame. <see cref="Candidate"/> is null when no candidate gave scores, and then <see cref="CandidateCode"/> says why (a code of <c>CandidateRunCodes</c>).
/// </summary>
public sealed record QualificationPairedScores(IReadOnlyList<double> Reference, IReadOnlyList<double>? Candidate, string? CandidateCode);

/// <summary>
/// One case of a run: a capture of the manifest, by its anonymous id. The document and presence parts are there for the parts the capture has (a part that could not run is there
/// with its codes); the decision is there for a capture that is a whole check-in; the paired scores are there when a candidate model is loaded; <see cref="StageMs"/> is the time of
/// each stage that ran; <see cref="Error"/> is the type of the exception that stopped the case, never its message.
/// </summary>
public sealed record QualificationCaseResult(
    string Id,
    string Kind,
    string? AttackClass,
    string? Channel,
    string? Format,
    bool Altered,
    bool Foreign,
    QualificationDocumentResult? Document,
    QualificationPresenceResult? Presence,
    QualificationDecisionResult? Decision,
    QualificationPairedScores? PairedMatch,
    QualificationPairedScores? PairedLiveness,
    IReadOnlyDictionary<string, double> StageMs,
    string? Error);

/// <summary>An impostor trial derived from the manifest: the sequence of one capture against the document of ANOTHER person of the split, by the ids of the two captures.</summary>
public sealed record QualificationImpostorTrial(string SequenceCase, string DocumentCase, double Similarity, double MinSimilarity, double MatchMs, QualificationPairedScores? Paired);

/// <summary>
/// The result of a run of the qualification pipeline on a split of a dataset (<c>results.json</c>): the dataset and the configuration in force (the policy, its thresholds, the
/// models with the SHA-256 of their weights), the cases and the impostor trials with their raw scores, codes, decisions and timings, and the aggregate metrics with their intervals.
/// It is everything the calibration, the lock and the report of the candidate version need, and nothing that is of a person.
/// </summary>
public sealed record QualificationResults(
    int SchemaVersion,
    string DatasetId,
    string DatasetVersion,
    string DatasetManifestHash,
    string Split,
    DateTimeOffset RunAt,
    DateOnly ReferenceDay,
    double Confidence,
    QualificationPolicyInfo Policy,
    QualificationThresholds Thresholds,
    IReadOnlyList<QualificationModelInfo> Models,
    QualificationDatasetSummary Dataset,
    QualificationImpostorSampling ImpostorSampling,
    IReadOnlyList<QualificationCaseResult> Cases,
    IReadOnlyList<QualificationImpostorTrial> ImpostorTrials,
    QualificationMetricsBlock Metrics)
{
    /// <summary>The version of the layout of the file: a change of it is a new number, never a silent change.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The name of the file in the output folder.</summary>
    public const string FileName = "results.json";

    /// <summary>The JSON options of every file of the qualification: camel case, indented, nothing unknown accepted when reading.</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The JSON text of the results.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Reads results from their JSON text; text that is not results (or of another version of the layout) is an <see cref="InvalidDataException"/> that names nothing of it.</summary>
    public static QualificationResults Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        QualificationResults? results;
        try
        {
            results = JsonSerializer.Deserialize<QualificationResults>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // The message of the parser may quote the text: only the fact is kept.
            throw new InvalidDataException("The qualification results cannot be read.");
        }

        if (results is null || results.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("The qualification results cannot be read.");

        return results;
    }

    /// <summary>Reads the results file at <paramref name="path"/>.</summary>
    public static QualificationResults Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Writes the results to <paramref name="path"/> (replacing a file of an earlier run of the same folder), through a file of the same folder so that a stopped run never leaves half a file.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var temporary = path + ".writing";
        File.WriteAllText(temporary, ToJson());
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>
/// A run that cannot be made, or must not go on, with a code and, when it helps, a detail made of codes, ids and counts only (never a value of the dataset, a path of a file of a
/// person or the message of another exception). The codes are the ones of <see cref="QualificationRunCodes"/>.
/// </summary>
public sealed class QualificationRunException : Exception
{
    public QualificationRunException(string code, string? detail = null)
        : base(detail is null ? code : code + ": " + detail)
    {
        Code = code;
    }

    /// <summary>The code of the failure, a code of <see cref="QualificationRunCodes"/>.</summary>
    public string Code { get; }
}

/// <summary>The codes of <see cref="QualificationRunException"/>, never renamed or reused: a new one goes at the end.</summary>
public static class QualificationRunCodes
{
    /// <summary>The manifest cannot be read as a manifest.</summary>
    public const string ManifestUnreadable = "manifest_unreadable";

    /// <summary>The manifest is read and does not pass its checks (the detail lists the codes of its errors).</summary>
    public const string ManifestInvalid = "manifest_invalid";

    /// <summary>The split is not one of <see cref="QualificationSplits"/>.</summary>
    public const string SplitUnknown = "split_unknown";

    /// <summary>The split has no persons or no captures.</summary>
    public const string SplitEmpty = "split_empty";

    /// <summary>The models were not ready in the time given to load them.</summary>
    public const string ModelsNotReady = "models_not_ready";

    /// <summary>The models cannot be used (the detail is the reason code of the store).</summary>
    public const string ModelsUnavailable = "models_unavailable";

    /// <summary>The decision policy cannot be used (the detail is its reason code).</summary>
    public const string PolicyNotReady = "policy_not_ready";

    /// <summary>The registry of the formats is empty: nothing could be read as a document.</summary>
    public const string FormatsEmpty = "formats_empty";

    /// <summary>The ground-truth file of a capture is not a flat object of the fields of <c>ZonedFieldNames</c> with text values (the detail is the id of the capture).</summary>
    public const string GroundTruthInvalid = "ground_truth_invalid";

    /// <summary>The output folder is a folder that must not be written to (the folder of the committed policy files).</summary>
    public const string OutputFolderInvalid = "output_folder_invalid";

    /// <summary>A report of the final test is already in the output folder: the final test is run once.</summary>
    public const string FinalAlreadyRun = "qualification_final_already_run";

    /// <summary>The lock file is not there or is not a lock.</summary>
    public const string LockUnreadable = "qualification_lock_unreadable";

    /// <summary>The configuration cannot be frozen: the policy is provisional, or not ready, or the models are not.</summary>
    public const string LockNotFreezable = "qualification_not_freezable";

    /// <summary>The section <c>IdentityVerification</c> of the configuration is not valid (the detail names the settings, never their values).</summary>
    public const string ConfigurationInvalid = "configuration_invalid";

    /// <summary>The calibration cannot propose a policy from the evidence there is (the detail lists the codes of what is missing).</summary>
    public const string CalibrationInsufficient = "calibration_insufficient";
}
