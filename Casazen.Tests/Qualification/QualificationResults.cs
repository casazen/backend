using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Infrastructure.Identity;

namespace Casazen.Tests.Qualification;

// E74/F01/US02/T01 (NF-198, D12): what a qualification run writes (results.json) and what the calibration, the lock and the report of the candidate version read back.
// PRIVACY: a result holds anonymous ids (the id of a capture, which the manifest keeps free of a name), codes, counts, scores of those cases, the places of the frames in
// a sequence and hashes. It never holds an image, a frame, a value read on a document, a ground-truth value, a name, a number of a document or an embedding: the types
// have no member for any of them, so nothing can be put there by mistake. Codes and constants are never renamed or reused: a new one goes at the end of its list.

public static class QualificationCaseKinds
{
    public const string Genuine = "genuine";

    public const string Attack = "attack";

    public const string ImpostorPair = "impostor_pair";

    public static IReadOnlyList<string> All { get; } = [Genuine, Attack, ImpostorPair];
}

public static class QualificationStages
{
    public const string DocumentCapture = "document_capture";

    public const string DocumentMrz = "document_mrz";

    public const string DocumentGeometry = "document_geometry";

    public const string DocumentFields = "document_fields";

    public const string DocumentCoherence = "document_coherence";

    public const string PresenceIntake = "presence_intake";

    public const string PresenceValidation = "presence_validation";

    public const string PresenceSelection = "presence_selection";

    public const string PresenceLiveness = "presence_liveness";

    public const string PresenceMatch = "presence_match";

    public const string Decision = "decision";

    public static IReadOnlyList<string> All { get; } =
    [
        DocumentCapture, DocumentMrz, DocumentGeometry, DocumentFields, DocumentCoherence, PresenceIntake, PresenceValidation, PresenceSelection, PresenceLiveness,
        PresenceMatch, Decision,
    ];
}

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

public sealed record QualificationModelInfo(string Role, string Name, string Version, string License, string Provenance, string? WeightSha256, bool Loaded, string? FailureCode);

public sealed record QualificationDatasetSummary(IReadOnlyList<QualificationSplitCounts> Splits, QualificationEvaluatedSummary Evaluated);

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

public sealed record QualificationSideResult(string Side, string? Shown, bool MrzRead, bool? MrzCheckDigitsValid, int FieldsRead, IReadOnlyList<string> Codes);

public sealed record QualificationFieldResult(string Field, int Exact, int Total);

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

public sealed record QualificationDecisionResult(string Final, string Shadow, string Mode, string? OutcomeCode, IReadOnlyList<string> Reasons, IReadOnlyList<string> Guidance);

public sealed record QualificationPairedScores(IReadOnlyList<double> Reference, IReadOnlyList<double>? Candidate, string? CandidateCode);

/// <summary>
/// One case of a run: a capture of the manifest, by its anonymous id. The document and presence parts are there for the parts the capture has (a part that could not run is there
/// with its codes); the decision is there for a capture that is a whole check-in; the paired scores are there when a candidate model is loaded; <see cref="StageMs"/> is the time of
/// each stage that ran; <see cref="Error"/> is the type of the exception that stopped the case, never its message. <see cref="Couple"/> is the opaque key of the couple of persons
/// (the person present, the owner of the document) of a case that is a person with the document of ANOTHER one (<see cref="QualificationMetrics.CoupleKey"/>); null for every other case.
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
    string? Error,
    string? Couple = null);

public sealed record QualificationImpostorTrial(
    string SequenceCase,
    string DocumentCase,
    double Similarity,
    double MinSimilarity,
    double MatchMs,
    QualificationPairedScores? Paired,
    string? Couple = null);

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
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "results.json";

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

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
            throw new InvalidDataException("The qualification results cannot be read.");
        }

        if (results is null || results.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("The qualification results cannot be read.");

        return results;
    }

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

public sealed class QualificationRunException : Exception
{
    public QualificationRunException(string code, string? detail = null)
        : base(detail is null ? code : code + ": " + detail)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class QualificationRunCodes
{
    public const string ManifestUnreadable = "manifest_unreadable";

    public const string ManifestInvalid = "manifest_invalid";

    public const string SplitUnknown = "split_unknown";

    public const string SplitEmpty = "split_empty";

    public const string ModelsNotReady = "models_not_ready";

    public const string ModelsUnavailable = "models_unavailable";

    public const string PolicyNotReady = "policy_not_ready";

    public const string FormatsEmpty = "formats_empty";

    public const string GroundTruthInvalid = "ground_truth_invalid";

    public const string OutputFolderInvalid = "output_folder_invalid";

    public const string FinalAlreadyRun = "qualification_final_already_run";

    public const string LockUnreadable = "qualification_lock_unreadable";

    public const string LockNotFreezable = "qualification_not_freezable";

    public const string ConfigurationInvalid = "configuration_invalid";

    public const string CalibrationInsufficient = "calibration_insufficient";

    public const string CriticalKindsMissing = "qualification_critical_kinds_missing";
}
